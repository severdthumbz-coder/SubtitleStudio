using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Extraction;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// "Subtitles inside this video" (Source / Files): opens the window (EmbeddedTracksViewModel) and takes a
/// track into the editor. The work is in Services/Extraction.
/// </summary>
public sealed partial class MainViewModel
{
    public RelayCommand SubtitlesInsideCommand { get; private set; } = null!;

    private void OpenEmbeddedTracks(MediaItem video)
    {
        if (FfmpegStatus.FfmpegPath is not { } ffmpeg || FfmpegStatus.FfprobePath is not { } ffprobe)
        {
            SetStatus("FFmpeg and ffprobe are needed to read the subtitles inside a video: set them in Settings > Engines and tools.", StatusKind.Warning);
            return;
        }
        var reader = new EmbeddedSubtitleReader(_s.SubtitleFormats, _s.BurnedIn.Ocr, _s.Log);
        var model = new EmbeddedTracksViewModel(video.FullPath, reader, _s.SubtitleFormats, ffmpeg, ffprobe, _s.Dialogs,
            doc => OpenEmbeddedInEditor(doc, video),
            path => RefreshSidecarsIn(Path.GetDirectoryName(path)));
        _s.Dialogs.ShowEmbeddedTracks(model);
    }

    /// <summary>A track from inside the video opens in the editor, unsaved and paired with the video.</summary>
    private void OpenEmbeddedInEditor(SubtitleStudio.Models.SubtitleDocument doc, MediaItem video)
    {
        if (!ConfirmDiscardChanges()) return;
        _standaloneBaseName = Path.GetFileNameWithoutExtension(video.Name);
        LoadDocument(doc, Files.Contains(video) ? video : null, selectIndex: 0);
        IsDirty = true;
        SelectedTabIndex = Tabs.Subtitles;
        SetStatus($"Opened {doc.Cues.Count} cues from inside {video.Name}. Review them, then save (Save puts them next to the video).", StatusKind.Success);
    }

    private void InitEmbedded()
    {
        SubtitlesInsideCommand = new RelayCommand(p => { if (p is MediaItem { Kind: MediaKind.Video } v) OpenEmbeddedTracks(v); },
            p => p is MediaItem { Kind: MediaKind.Video } && FfmpegStatus.IsComplete);
    }
}
