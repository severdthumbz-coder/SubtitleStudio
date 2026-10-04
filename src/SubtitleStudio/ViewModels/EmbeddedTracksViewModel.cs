using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Extraction;
using SubtitleStudio.Services.Muxing;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.ViewModels;

/// <summary>One subtitle track inside the video (or in a .sup / .idx beside it), with what was done with it.</summary>
public sealed class EmbeddedTrackRow : ObservableObject
{
    private string _status;
    private ExtractedTrack? _result;

    public EmbeddedTrackRow(EmbeddedTrack track)
    {
        Track = track;
        _status = track.CanRead ? string.Empty : track.CannotReadReason;
    }

    public EmbeddedTrack Track { get; }

    public string Label => Track.Label;

    public bool CanRead => Track.CanRead;

    /// <summary>How it's read: copied exactly, or OCR.</summary>
    public string HowText => Track.IsText ? "Text: copied out exactly." : Track.IsPicture ? "Pictures: read with Windows OCR (check the result)." : string.Empty;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Read already (opening or saving again doesn't read it again).</summary>
    public ExtractedTrack? Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }
}

/// <summary>
/// "Subtitles inside this video": lists the subtitle tracks in a video (and picture-subtitle files beside
/// it), and opens one in the editor or saves it next to the video. The work is in
/// Services/Extraction/EmbeddedSubtitleReader; this is the window's state.
/// </summary>
public sealed class EmbeddedTracksViewModel : ObservableObject
{
    private readonly EmbeddedSubtitleReader _reader;
    private readonly SubtitleFormatRegistry _formats;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly IDialogService _dialogs;
    private readonly Action<SubtitleDocument> _openInEditor;
    private readonly Action<string>? _saved;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private double _progress;
    private string _status = "Looking for subtitle tracks...";
    private string _latestText = string.Empty;
    private bool _loaded;

    public EmbeddedTracksViewModel(string videoPath, EmbeddedSubtitleReader reader, SubtitleFormatRegistry formats, string ffmpeg, string ffprobe,
        IDialogService dialogs, Action<SubtitleDocument> openInEditor, Action<string>? saved)
    {
        VideoPath = Path.GetFullPath(videoPath);
        _reader = reader;
        _formats = formats;
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
        _dialogs = dialogs;
        _openInEditor = openInEditor;
        _saved = saved;
        OpenCommand = new AsyncRelayCommand(p => p is EmbeddedTrackRow r ? OpenAsync(r) : Task.CompletedTask, p => !_busy && p is EmbeddedTrackRow { CanRead: true });
        SaveCommand = new AsyncRelayCommand(p => p is EmbeddedTrackRow r ? SaveAsync(r) : Task.CompletedTask, p => !_busy && p is EmbeddedTrackRow { CanRead: true });
        SaveAllCommand = new AsyncRelayCommand(SaveAllAsync, () => !_busy && Tracks.Any(t => t.CanRead));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        RevealCommand = new RelayCommand(() => _dialogs.RevealInExplorer(VideoPath));
    }

    public string VideoPath { get; }

    public string VideoName => Path.GetFileName(VideoPath);

    public ObservableCollection<EmbeddedTrackRow> Tracks { get; } = new();

    public bool HasTracks => Tracks.Count > 0;

    public bool NoTracks => _loaded && Tracks.Count == 0;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            RelayCommand.Refresh();
        }
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>The line just read (while reading pictures).</summary>
    public string LatestText
    {
        get => _latestText;
        private set => SetProperty(ref _latestText, value);
    }

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RevealCommand { get; }

    /// <summary>Asks the window to close (after opening a track in the editor).</summary>
    public event Action? CloseRequested;

    public async Task LoadAsync()
    {
        try
        {
            var tracks = await EmbeddedSubtitleReader.ListAsync(_ffprobe, VideoPath, EmbeddedSubtitleReader.PictureFilesBeside(VideoPath), CancellationToken.None);
            foreach (var t in tracks) Tracks.Add(new EmbeddedTrackRow(t));
            int readable = tracks.Count(t => t.CanRead);
            Status = tracks.Count == 0 ? "This video has no subtitle tracks inside it."
                : $"{tracks.Count} subtitle track{(tracks.Count == 1 ? "" : "s")}" + (readable < tracks.Count ? $", {readable} readable." : ".")
                  + " Open one in the editor, or save it next to the video.";
        }
        catch (Exception ex) when (ex is MuxException or IOException or System.Text.Json.JsonException)
        {
            Status = "The video's tracks couldn't be read: " + ex.Message;
        }
        _loaded = true;
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(NoTracks));
        RelayCommand.Refresh();
    }

    /// <summary>The window is closing: stop reading.</summary>
    public void CancelRun() => _cts?.Cancel();

    private async Task<ExtractedTrack?> ReadAsync(EmbeddedTrackRow row)
    {
        if (row.Result is { } done) return done;
        _cts = new CancellationTokenSource();
        Busy = true;
        Progress = 0;
        LatestText = string.Empty;
        Status = row.Track.IsPicture ? "Reading the pictures with Windows OCR..." : "Copying the track out...";
        try
        {
            var progress = new Progress<ExtractionStep>(s =>
            {
                if (!_busy) return;
                Progress = s.Fraction * 100;
                Status = s.Text;
                if (s.LatestText is { Length: > 0 } t) LatestText = t;
            });
            var result = await _reader.ReadAsync(_ffmpeg, _ffprobe, row.Track, progress, _cts.Token);
            row.Result = result;
            var cues = result.Document.Cues.Count;
            row.Status = row.Track.IsPicture
                ? $"{cues} cue{(cues == 1 ? "" : "s")} read from {result.Pictures} picture{(result.Pictures == 1 ? "" : "s")}"
                  + (result.Unreadable > 0 ? $" ({result.Unreadable} without readable text)." : ".")
                : $"{cues} cue{(cues == 1 ? "" : "s")}.";
            return result;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            return null;
        }
        catch (Exception ex) when (ex is MuxException or IOException or InvalidDataException or SubtitleFormatException or UnauthorizedAccessException)
        {
            row.Status = ex.Message;
            Status = ex.Message;
            return null;
        }
        finally
        {
            Busy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task OpenAsync(EmbeddedTrackRow row)
    {
        var result = await ReadAsync(row);
        if (result is null) return;
        if (result.Document.Cues.Count == 0)
        {
            Status = "The track has no subtitles to open.";
            return;
        }
        _openInEditor(Copy(result.Document));
        CloseRequested?.Invoke();
    }

    private async Task SaveAsync(EmbeddedTrackRow row)
    {
        var result = await ReadAsync(row);
        if (result is null) return;
        var path = SaveBeside(row, result);
        if (path is not null) Status = $"Saved {Path.GetFileName(path)}.";
    }

    private async Task SaveAllAsync()
    {
        var saved = new List<string>();
        bool? replace = null; // asked once, the first time a name is taken
        foreach (var row in Tracks.Where(t => t.CanRead).ToList())
        {
            var result = await ReadAsync(row);
            if (result is null) break;
            if (File.Exists(TargetPath(result.Document)) && replace is null)
                replace = _dialogs.Confirm("Replace the files?", "Some of these subtitle files already exist next to the video. Replace them? (No: the new ones are saved with a number added.)");
            if (SaveBeside(row, result, replace ?? false) is { } path) saved.Add(Path.GetFileName(path));
        }
        if (saved.Count > 0) Status = $"Saved {saved.Count} file{(saved.Count == 1 ? "" : "s")}: {string.Join(", ", saved)}.";
    }

    private static string Extension(SubtitleDocument doc) => doc.Format is "ass" or "ssa" ? "ass" : "srt";

    /// <summary>"Film.en.srt" (or .ass, .forced, .sdh) next to the video.</summary>
    private string TargetPath(SubtitleDocument doc)
        => Path.Combine(Path.GetDirectoryName(VideoPath)!, SidecarDetector.BuildSidecarName(Path.GetFileNameWithoutExtension(VideoPath), doc.Language, doc.Forced, false, doc.HearingImpaired, Extension(doc)));

    /// <summary>Saves next to the video; a name already taken is replaced, or (asked, when <paramref name="replace"/> is null) gets a number.</summary>
    private string? SaveBeside(EmbeddedTrackRow row, ExtractedTrack result, bool? replace = null)
    {
        var doc = result.Document;
        if (doc.Cues.Count == 0)
        {
            row.Status = "Nothing to save (no subtitles).";
            return null;
        }
        var ext = Extension(doc);
        var path = TargetPath(doc);
        if (File.Exists(path))
        {
            replace ??= _dialogs.Confirm("Replace the file?", $"{Path.GetFileName(path)} already exists next to the video. Replace it with this track? (No: saved with a number added.)");
            if (replace != true)
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var folder = Path.GetDirectoryName(path)!;
                for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}).{ext}");
            }
        }
        try
        {
            var copy = Copy(doc);
            _formats.Save(copy, path, ext);
            row.Status = $"Saved {Path.GetFileName(path)} ({doc.Cues.Count} cues).";
            _saved?.Invoke(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SubtitleFormatException)
        {
            row.Status = "Couldn't save: " + ex.Message;
            return null;
        }
    }

    private static SubtitleDocument Copy(SubtitleDocument d)
    {
        var copy = new SubtitleDocument
        {
            Language = d.Language, Format = d.Format, FormatHeader = d.FormatHeader, FormatTrailer = d.FormatTrailer, FrameRate = d.FrameRate,
            Forced = d.Forced, HearingImpaired = d.HearingImpaired, Sdh = d.Sdh,
        };
        copy.Cues.AddRange(d.Cues.Select(c => c.Clone()));
        return copy;
    }
}
