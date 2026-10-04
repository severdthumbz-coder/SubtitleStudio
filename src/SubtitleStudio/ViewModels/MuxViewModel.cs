using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Muxing;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.ViewModels;

/// <summary>One subtitle file offered as a track: whether it's added, its language, name and flags.</summary>
public sealed class MuxTrackRow : ObservableObject
{
    private readonly Action<MuxTrackRow> _changed;
    private bool _include;
    private TranscribeLanguageOption _language;
    private string _title;
    private bool _titleEdited;
    private bool _isDefault;
    private bool _forced;
    private bool _hearingImpaired;

    public MuxTrackRow(string path, string? language, bool forced, bool hearingImpaired, bool include,
        IReadOnlyList<TranscribeLanguageOption> languages, Action<MuxTrackRow> changed)
    {
        Path = path;
        Languages = languages;
        _changed = changed;
        _include = include;
        _forced = forced;
        _hearingImpaired = hearingImpaired;
        var code = language is null ? null : TrackLanguages.FromTrackTag(language) ?? language.Split('-', '_')[0].ToLowerInvariant();
        _language = languages.FirstOrDefault(l => l.Code == code) ?? languages[0];
        _title = SubtitleMuxer.DefaultTitle(_language.Code, forced, hearingImpaired);
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public IReadOnlyList<TranscribeLanguageOption> Languages { get; }

    public bool Include
    {
        get => _include;
        set
        {
            if (!SetProperty(ref _include, value)) return;
            if (!value && _isDefault) IsDefault = false;
            _changed(this);
        }
    }

    public TranscribeLanguageOption Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value ?? Languages[0])) return;
            RetitleIfAutomatic();
            _changed(this);
        }
    }

    /// <summary>The name players show; follows the language and flags until it's typed over.</summary>
    public string Title
    {
        get => _title;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref _title, text)) return;
            _titleEdited = text != SubtitleMuxer.DefaultTitle(_language.Code, _forced, _hearingImpaired);
            _changed(this);
        }
    }

    /// <summary>Played without being chosen (only one track is default; choosing one clears the others).</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (!SetProperty(ref _isDefault, value)) return;
            if (value && !_include) Include = true;
            _changed(this);
        }
    }

    /// <summary>Only the lines in another language, or signs: shown even when subtitles are off.</summary>
    public bool Forced
    {
        get => _forced;
        set
        {
            if (!SetProperty(ref _forced, value)) return;
            RetitleIfAutomatic();
            _changed(this);
        }
    }

    /// <summary>For the deaf and hard of hearing (sounds described).</summary>
    public bool HearingImpaired
    {
        get => _hearingImpaired;
        set
        {
            if (!SetProperty(ref _hearingImpaired, value)) return;
            RetitleIfAutomatic();
            _changed(this);
        }
    }

    internal void ClearDefaultQuietly()
    {
        if (!_isDefault) return;
        _isDefault = false;
        OnPropertyChanged(nameof(IsDefault));
    }

    private void RetitleIfAutomatic()
    {
        if (_titleEdited) return;
        _title = SubtitleMuxer.DefaultTitle(_language.Code, _forced, _hearingImpaired);
        OnPropertyChanged(nameof(Title));
    }

    public SubtitleTrack ToTrack() => new(Path, _language.Code, string.IsNullOrWhiteSpace(_title) ? null : _title.Trim(), _isDefault, _forced, _hearingImpaired);
}

/// <summary>
/// "Add subtitles to video": subtitle files go into the video as tracks (FFmpeg, no re-encoding). The work
/// is in Services/Muxing/SubtitleMuxer; this is the window's state. Opened from Source / Files, the
/// Subtitles tab and the Batch tab.
/// </summary>
public sealed class MuxViewModel : ObservableObject
{
    public const string SubtitleFilter = "Subtitles|*.srt;*.vtt;*.ass;*.ssa;*.sub|All files|*.*";

    private readonly SubtitleMuxer _muxer;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly IDialogService _dialogs;
    private readonly Action<MuxResult>? _completed;
    private MediaInfo? _info;
    private bool _isMp4;
    private bool _keepExisting = true;
    private bool _replaceOriginal;
    private string _outputPath;
    private bool _outputChosen;
    private string _existingText = "Reading the video's tracks...";
    private string _planWarning = string.Empty;
    private string _notesText = string.Empty;
    private bool _busy;
    private bool _done;
    private double _progress;
    private string _status = string.Empty;
    private CancellationTokenSource? _cts;
    private bool _adjusting;

    public MuxViewModel(string videoPath, IEnumerable<string> subtitleFiles, string? preselect, string? defaultLanguage,
        SubtitleMuxer muxer, string ffmpeg, string ffprobe, IDialogService dialogs, Action<MuxResult>? completed)
    {
        VideoPath = Path.GetFullPath(videoPath);
        _muxer = muxer;
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
        _dialogs = dialogs;
        _completed = completed;
        _isMp4 = SubtitleMuxer.ContainerFor(VideoPath) == MuxContainer.Mp4;
        _outputPath = SubtitleMuxer.SuggestOutput(VideoPath, Container);

        foreach (var file in subtitleFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsTextSubtitle(file)) continue;
            var (language, forced, hi) = Parse(VideoPath, file);
            bool include = preselect is null || string.Equals(file, preselect, StringComparison.OrdinalIgnoreCase);
            Tracks.Add(new MuxTrackRow(file, language, forced, hi, include, Languages, OnTrackChanged));
        }
        // The default: the preselected file, else the first full (not forced) track in the usual language.
        var first = (preselect is not null ? Tracks.FirstOrDefault(t => t.Include && !t.Forced) : null)
                    ?? Tracks.FirstOrDefault(t => t.Include && !t.Forced && t.Language.Code == defaultLanguage);
        if (first is not null) first.IsDefault = true;

        AddFileCommand = new RelayCommand(AddFiles, () => !_busy && !_done);
        RemoveTrackCommand = new RelayCommand(p => { if (p is MuxTrackRow r) { Tracks.Remove(r); Refresh(); } }, p => !_busy && !_done && p is MuxTrackRow);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_busy && !_done && !_replaceOriginal);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        RevealCommand = new RelayCommand(() => _dialogs.RevealInExplorer(ResultPath ?? VideoPath));
        Refresh();
    }

    public IReadOnlyList<TranscribeLanguageOption> Languages { get; } =
        new[] { new TranscribeLanguageOption(null, "No language") }
            .Concat(WhisperLanguages.All.Select(l => new TranscribeLanguageOption(l.Code, l.Name)).OrderBy(l => l.Name)).ToList();

    public string VideoPath { get; }

    public string VideoName => Path.GetFileName(VideoPath);

    public ObservableCollection<MuxTrackRow> Tracks { get; } = new();

    public bool HasTracks => Tracks.Count > 0;

    /// <summary>What the video already has, one stream per line.</summary>
    public string ExistingText
    {
        get => _existingText;
        private set => SetProperty(ref _existingText, value);
    }

    public bool HasExistingSubtitles => _info?.Streams.Any(s => s.Kind == StreamKind.Subtitle) == true;

    public MuxContainer Container => _isMp4 ? MuxContainer.Mp4 : MuxContainer.Mkv;

    public bool IsMkv
    {
        get => !_isMp4;
        set => IsMp4 = !value;
    }

    public bool IsMp4
    {
        get => _isMp4;
        set
        {
            if (_isMp4 == value) return;
            _isMp4 = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMkv));
            if (!_outputChosen) OutputPath = SubtitleMuxer.SuggestOutput(VideoPath, Container);
            else OutputPath = Path.ChangeExtension(_outputPath, SubtitleMuxer.Extension(Container));
            Refresh();
        }
    }

    public bool KeepExisting
    {
        get => _keepExisting;
        set
        {
            if (SetProperty(ref _keepExisting, value)) Refresh();
        }
    }

    public bool SaveAsNew
    {
        get => !_replaceOriginal;
        set => ReplaceOriginal = !value;
    }

    public bool ReplaceOriginal
    {
        get => _replaceOriginal;
        set
        {
            if (!SetProperty(ref _replaceOriginal, value)) return;
            OnPropertyChanged(nameof(SaveAsNew));
            Refresh();
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value)) OnPropertyChanged(nameof(OutputName));
        }
    }

    public string OutputName => Path.GetFileName(_outputPath);

    /// <summary>"Episode 1.mp4 goes to the Recycle Bin; the new video is saved as Episode 1.mkv."</summary>
    public string ReplaceText
    {
        get
        {
            var target = Path.ChangeExtension(VideoPath, SubtitleMuxer.Extension(Container));
            return string.Equals(target, VideoPath, StringComparison.OrdinalIgnoreCase)
                ? $"{VideoName} goes to the Recycle Bin, and the new video takes its name."
                : $"{VideoName} goes to the Recycle Bin, and the new video is saved as {Path.GetFileName(target)}.";
        }
    }

    /// <summary>Why it can't be done as chosen (empty when it can).</summary>
    public string PlanWarning
    {
        get => _planWarning;
        private set
        {
            if (SetProperty(ref _planWarning, value)) OnPropertyChanged(nameof(HasPlanWarning));
        }
    }

    public bool HasPlanWarning => _planWarning.Length > 0;

    /// <summary>What will be left out or changed, and the result once done.</summary>
    public string NotesText
    {
        get => _notesText;
        private set
        {
            if (SetProperty(ref _notesText, value)) OnPropertyChanged(nameof(HasNotes));
        }
    }

    public bool HasNotes => _notesText.Length > 0;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            RelayCommand.Refresh();
        }
    }

    public bool IsDone
    {
        get => _done;
        private set
        {
            if (!SetProperty(ref _done, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CloseText));
            RelayCommand.Refresh();
        }
    }

    public bool CanEdit => !_busy && !_done;

    public string CloseText => _done ? "Close" : "Cancel";

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

    public string? ResultPath { get; private set; }

    public RelayCommand AddFileCommand { get; }
    public RelayCommand RemoveTrackCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RevealCommand { get; }

    private bool CanStart => !_busy && !_done && _info is not null && Tracks.Any(t => t.Include) && !HasPlanWarning;

    /// <summary>Reads the video's tracks (call once the window shows).</summary>
    public async Task LoadAsync()
    {
        try
        {
            _info = await SubtitleMuxer.ProbeAsync(_ffprobe, VideoPath, CancellationToken.None);
            ExistingText = _info.Streams.Count == 0 ? "No tracks found." : string.Join("\n", _info.Streams.Select(s => s.Describe()));
            OnPropertyChanged(nameof(HasExistingSubtitles));
        }
        catch (Exception ex) when (ex is MuxException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ExistingText = "The video's tracks couldn't be read: " + ex.Message;
        }
        Refresh();
    }

    /// <summary>The window is closing: stop a run that's going.</summary>
    public void CancelRun() => _cts?.Cancel();

    private void OnTrackChanged(MuxTrackRow row)
    {
        if (_adjusting) return;
        _adjusting = true;
        try
        {
            if (row.IsDefault)
                foreach (var other in Tracks.Where(t => !ReferenceEquals(t, row))) other.ClearDefaultQuietly();
        }
        finally
        {
            _adjusting = false;
        }
        Refresh();
    }

    private void AddFiles()
    {
        foreach (var file in _dialogs.PickFiles("Add subtitle files", SubtitleFilter))
        {
            if (Tracks.Any(t => string.Equals(t.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            var (language, forced, hi) = Parse(VideoPath, file);
            Tracks.Add(new MuxTrackRow(file, language, forced, hi, true, Languages, OnTrackChanged));
        }
        Refresh();
    }

    private void BrowseOutput()
    {
        var ext = SubtitleMuxer.Extension(Container);
        var filter = Container == MuxContainer.Mp4 ? "MP4 video|*.mp4" : "Matroska video|*.mkv";
        var picked = _dialogs.SaveFile("Save the video with subtitles as", filter, 1, Path.GetDirectoryName(_outputPath), Path.GetFileName(_outputPath));
        if (picked is null) return;
        _outputChosen = true;
        OutputPath = Path.ChangeExtension(picked, ext);
        Refresh();
    }

    private MuxRequest Request() => new(VideoPath, Tracks.Where(t => t.Include).Select(t => t.ToTrack()).ToList(), Container, _outputPath, _keepExisting, _replaceOriginal);

    /// <summary>Checks the choice against the video's tracks (without running anything).</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(ReplaceText));
        if (_done) return;
        var request = Request();
        var warning = string.Empty;
        var notes = new List<string>();
        if (!_replaceOriginal && string.Equals(Path.GetFullPath(_outputPath), VideoPath, StringComparison.OrdinalIgnoreCase))
            warning = "The new video can't overwrite the one it's made from: choose another name, or \"Replace the original\".";
        else if (_replaceOriginal && Path.ChangeExtension(VideoPath, SubtitleMuxer.Extension(Container)) is var target
                 && !string.Equals(target, VideoPath, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            warning = $"{Path.GetFileName(target)} already exists next to the original. Save as a new file instead.";
        else if (_info is not null)
        {
            try
            {
                var inputs = request.Tracks.Select(t => Container == MuxContainer.Mkv && IsAss(t.Path) ? "x.ass" : "x.srt").ToList();
                notes.AddRange(SubtitleMuxer.Plan(_info, request, inputs, "out").Notes);
            }
            catch (MuxException ex)
            {
                warning = ex.Message;
            }
        }
        if (request.Tracks.Count > 0 && !request.Tracks.Any(t => t.Default) && !_isMp4)
            notes.Add("No track is marked default: players start with subtitles off (or follow their own language setting).");
        if (request.Tracks.Any(t => t.Language is null))
            notes.Add("A track without a language shows as \"Unknown\" in most players.");
        PlanWarning = warning;
        NotesText = string.Join("\n", notes);
        RelayCommand.Refresh();
    }

    private async Task StartAsync()
    {
        var request = Request();
        if (_replaceOriginal && !_dialogs.Confirm("Replace the original?", ReplaceText + "\n\nThe new video is checked before the original is moved. Go ahead?")) return;
        _cts = new CancellationTokenSource();
        Busy = true;
        Progress = 0;
        Status = "Adding the subtitles (copying the picture and sound as they are)...";
        try
        {
            var progress = new Progress<double>(p =>
            {
                if (!_busy) return;
                Progress = p * 100;
                Status = $"Adding the subtitles... {p:P0}";
            });
            var result = await _muxer.RunAsync(_ffmpeg, _ffprobe, request, _dialogs.MoveToRecycleBin, progress, _cts.Token);
            ResultPath = result.OutputPath;
            Progress = 100;
            Status = "Done. " + SubtitleMuxer.Summary(result);
            IsDone = true;
            OnPropertyChanged(nameof(ResultPath));
            _completed?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Nothing was changed.";
        }
        catch (MuxException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The video couldn't be written: " + ex.Message;
        }
        finally
        {
            Busy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Language and flags from the file name ("Film.en.forced.srt").</summary>
    private static (string? Language, bool Forced, bool HearingImpaired) Parse(string video, string file)
    {
        if (SidecarDetector.TryParse(video, file) is { } s) return (s.Language, s.Forced, s.HearingImpaired || s.Sdh);
        var p = SidecarDetector.ParseStandalone(file);
        return (p.Language, p.Forced, p.HearingImpaired || p.Sdh);
    }

    private static bool IsAss(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ass" or ".ssa";

    /// <summary>Text subtitles only: a .sub beside an .idx is VobSub (pictures), not MicroDVD text.</summary>
    private static bool IsTextSubtitle(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".srt" or ".vtt" or ".ass" or ".ssa" => true,
        ".sub" => !File.Exists(Path.ChangeExtension(path, ".idx")),
        _ => false,
    };
}
