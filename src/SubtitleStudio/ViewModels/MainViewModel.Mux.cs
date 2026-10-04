using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Muxing;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// "Add subtitles to video": opens the window (MuxViewModel) from Source / Files, the Subtitles tab and the
/// Batch tab, and brings the file list up to date afterwards. The work is in Services/Muxing.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Source / Files (a video), or Batch (a queue row): every subtitle file next to the video is offered.</summary>
    public RelayCommand AddSubtitlesToVideoCommand { get; private set; } = null!;

    /// <summary>Subtitles tab: the open subtitle (saved first) into its paired video.</summary>
    public RelayCommand AddEditorSubtitleToVideoCommand { get; private set; } = null!;

    private static MediaItem? VideoOf(object? parameter) => parameter switch
    {
        MediaItem { Kind: MediaKind.Video } m => m,
        QueueRowViewModel { Item.Kind: MediaKind.Video } r => r.Item,
        _ => null,
    };

    private bool CanMux => FfmpegStatus.IsComplete && !BatchBusy;

    private void OpenMux(string videoPath, string? preselect)
    {
        if (FfmpegStatus.FfmpegPath is not { } ffmpeg || FfmpegStatus.FfprobePath is not { } ffprobe)
        {
            SetStatus("FFmpeg and ffprobe are needed to add subtitles to a video: set them in Settings > Engines and tools.", StatusKind.Warning);
            return;
        }
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        IEnumerable<string> found;
        try
        {
            found = SidecarDetector.Detect(videoPath, Directory.GetFiles(folder)).Select(s => s.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = Array.Empty<string>();
        }
        var files = (preselect is null ? found : found.Prepend(preselect)).ToList();
        var model = new MuxViewModel(videoPath, files, preselect, _s.Config.DefaultLanguage,
            new SubtitleMuxer(_s.SubtitleFormats, _s.Log), ffmpeg, ffprobe, _s.Dialogs, AfterMux);
        _s.Dialogs.ShowAddToVideo(model);
    }

    private void AddEditorSubtitleToVideo()
    {
        if (PairedVideo is not { } video || _doc is null) return;
        if (IsDirty || _doc.SourcePath is null)
        {
            if (!_s.Dialogs.Confirm("Save the subtitles first?", "The subtitles have to be saved before they can go into the video. Save them now?")) return;
            Save();
            if (IsDirty || _doc.SourcePath is null) return; // save cancelled or failed
        }
        OpenMux(video.FullPath, _doc.SourcePath);
    }

    /// <summary>The new video joins the list (and a replaced original leaves it).</summary>
    private void AfterMux(MuxResult result)
    {
        SetStatus(SubtitleMuxer.Summary(result), StatusKind.Success);
        if (result.ReplacedOriginal is { } old && !string.Equals(old, result.OutputPath, StringComparison.OrdinalIgnoreCase))
        {
            var gone = Files.FirstOrDefault(f => string.Equals(f.FullPath, old, StringComparison.OrdinalIgnoreCase));
            if (gone is not null)
            {
                Files.Remove(gone);
                _filePaths.Remove(gone.FullPath);
            }
        }
        var existing = Files.FirstOrDefault(f => string.Equals(f.FullPath, result.OutputPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Files.Remove(existing);
        _filePaths.Remove(result.OutputPath);
        try
        {
            var info = new FileInfo(result.OutputPath);
            var item = new MediaItem(info.FullName, MediaKind.Video, info.Length, SidecarDetector.Detect(info.FullName, Directory.GetFiles(info.DirectoryName!)));
            _filePaths.Add(item.FullPath);
            Files.Add(item);
            _ = ProbeDurationsAsync(new[] { item }, _probeCts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _s.Log.Detail("Mux", "The new video couldn't be listed: " + ex.Message);
        }
    }

    private void InitMux()
    {
        AddSubtitlesToVideoCommand = new RelayCommand(p => { if (VideoOf(p) is { } v) OpenMux(v.FullPath, null); }, p => CanMux && VideoOf(p) is not null);
        AddEditorSubtitleToVideoCommand = new RelayCommand(AddEditorSubtitleToVideo, () => CanMux && PairedVideo is not null && _doc is not null && Cues.Count > 0);
    }
}
