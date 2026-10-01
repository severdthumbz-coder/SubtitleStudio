using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Video preview in the Subtitles tab: plays the paired video, shows the cue under the playhead
/// as an overlay, follows playback in the cue list and seeks when a cue is picked.
/// Engine work is in Services/Playback (IPlaybackService); nothing here touches LibVLC.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(2);

    private object? _playerHandle;
    private bool _isPlaying;
    private TimeSpan _position, _length;
    private string _overlayText = string.Empty;
    private bool _followPlayback = true;
    private bool _suppressCueSeek;
    private string? _previewMessage = "Pair this subtitle with a video (Save as sidecar > Video) to preview it here.";

    public object? PlayerHandle
    {
        get => _playerHandle;
        private set => SetProperty(ref _playerHandle, value);
    }

    public bool IsPreviewLoaded => _s.Playback.LoadedPath is not null;

    /// <summary>Shown in place of the video when there is nothing to play (or the player can't start).</summary>
    public string? PreviewMessage
    {
        get => _previewMessage;
        private set => SetProperty(ref _previewMessage, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!SetProperty(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public string PositionText => $"{Timecode.Format(_position)} / {Timecode.Format(_length)}";

    /// <summary>Slider value in seconds. Setting it (user drag) seeks.</summary>
    public double PositionSeconds
    {
        get => _position.TotalSeconds;
        set
        {
            if (Math.Abs(value - _position.TotalSeconds) < 0.05) return;
            _s.Playback.Seek(TimeSpan.FromSeconds(value));
        }
    }

    public double LengthSeconds => Math.Max(1, _length.TotalSeconds);

    public string OverlayText
    {
        get => _overlayText;
        private set
        {
            if (!SetProperty(ref _overlayText, value)) return;
            OnPropertyChanged(nameof(HasOverlayText));
            OnPropertyChanged(nameof(OverlayVisible));
        }
    }

    public bool HasOverlayText => _overlayText.Length > 0;

    /// <summary>Show the cue over the video. Off shows the bare picture (to compare with burned-in text). Remembered.</summary>
    public bool ShowOverlay
    {
        get => _s.Config.ShowSubtitleOverlay;
        set
        {
            if (_s.Config.ShowSubtitleOverlay == value) return;
            _s.Config.ShowSubtitleOverlay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OverlayVisible));
            SaveSettings();
        }
    }

    public bool OverlayVisible => ShowOverlay && HasOverlayText;

    public RelayCommand ToggleOverlayCommand { get; private set; } = null!;

    public bool FollowPlayback
    {
        get => _followPlayback;
        set => SetProperty(ref _followPlayback, value);
    }

    public TimeSpan Playhead => _position;

    public RelayCommand PlayPauseCommand { get; private set; } = null!;
    public RelayCommand SeekBackCommand { get; private set; } = null!;
    public RelayCommand SeekForwardCommand { get; private set; } = null!;
    public RelayCommand StartAtPlayheadCommand { get; private set; } = null!;
    public RelayCommand EndAtPlayheadCommand { get; private set; } = null!;

    private void InitPreview()
    {
        ToggleOverlayCommand = new RelayCommand(() => ShowOverlay = !ShowOverlay);
        PlayPauseCommand = new RelayCommand(() => _s.Playback.TogglePlay(), () => IsPreviewLoaded);
        SeekBackCommand = new RelayCommand(() => _s.Playback.Seek(_position - SeekStep), () => IsPreviewLoaded);
        SeekForwardCommand = new RelayCommand(() => _s.Playback.Seek(_position + SeekStep), () => IsPreviewLoaded);
        StartAtPlayheadCommand = new RelayCommand(() => SetSelectedTime(isStart: true), () => IsPreviewLoaded && SelectedCue is not null);
        EndAtPlayheadCommand = new RelayCommand(() => SetSelectedTime(isStart: false), () => IsPreviewLoaded && SelectedCue is not null);

        _s.Playback.PositionChanged += p => RunOnUi(() => OnPlayerPosition(p));
        _s.Playback.LengthChanged += l => RunOnUi(() =>
        {
            _length = l;
            OnPropertyChanged(nameof(LengthSeconds));
            OnPropertyChanged(nameof(PositionText));
        });
        _s.Playback.PlayingChanged += playing => RunOnUi(() => IsPlaying = playing);
        _s.Playback.Failed += message => RunOnUi(() => SetStatus(message, StatusKind.Error));
    }

    /// <summary>Called whenever the paired video changes.</summary>
    private void RefreshPreview()
    {
        var video = PairedVideo;
        if (video is null || _doc is null)
        {
            if (_s.Playback.LoadedPath is not null) _s.Playback.Unload();
            ResetPlayerState();
            PreviewMessage = "Pair this subtitle with a video (Save as sidecar > Video) to preview it here.";
            return;
        }

        if (!_s.Playback.EnsureInitialized())
        {
            PreviewMessage = _s.Playback.UnavailableReason ?? "The video player is not available.";
            return;
        }

        PlayerHandle = _s.Playback.NativePlayer;
        if (string.Equals(_s.Playback.LoadedPath, video.FullPath, StringComparison.OrdinalIgnoreCase)) return;

        ResetPlayerState();
        _s.Playback.Load(video.FullPath, SelectedCue?.Cue.Start ?? TimeSpan.Zero);
        PreviewMessage = null;
        OnPropertyChanged(nameof(IsPreviewLoaded));
        RelayCommand.Refresh();
    }

    /// <summary>The view's video surface is on screen and connected: a waiting video can start.</summary>
    public void AttachVideoOutput() => _s.Playback.AttachOutput();

    /// <summary>The view is hidden: stop the video (it resumes at the same spot when the tab is shown again).</summary>
    public void DetachVideoOutput()
    {
        _s.Playback.DetachOutput();
        IsPlaying = false;
    }

    private void ResetPlayerState()
    {
        _position = TimeSpan.Zero;
        _length = TimeSpan.Zero;
        IsPlaying = false;
        OverlayText = string.Empty;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(LengthSeconds));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(IsPreviewLoaded));
    }

    private void OnPlayerPosition(TimeSpan position)
    {
        _position = position;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
        UpdateOverlay();

        if (FollowPlayback && IsPlaying)
        {
            var current = Cues.FirstOrDefault(r => !r.IsComment && r.Cue.Start <= position && position < r.Cue.End);
            if (current is not null && !ReferenceEquals(current, SelectedCue))
            {
                _suppressCueSeek = true;
                SelectedCue = current;
                _suppressCueSeek = false;
            }
        }
    }

    private void UpdateOverlay()
    {
        if (!IsPreviewLoaded)
        {
            OverlayText = string.Empty;
            return;
        }
        var lines = Cues
            .Where(r => !r.IsComment && r.Cue.Start <= _position && _position < r.Cue.End)
            .Select(r => SubtitleText.StripTags(r.Cue.Text).Trim())
            .Where(t => t.Length > 0);
        OverlayText = string.Join("\n", lines);
    }

    /// <summary>Picking a cue (not following playback) jumps the video to it.</summary>
    private void OnSelectedCueChangedForPreview()
    {
        if (_suppressCueSeek || SelectedCue is null || !IsPreviewLoaded || IsPlaying) return;
        _s.Playback.Seek(SelectedCue.Cue.Start);
    }

    private void SetSelectedTime(bool isStart)
    {
        if (SelectedCue is null) return;
        PushUndo();
        if (isStart) SelectedCue.StartText = Timecode.Format(_position);
        else SelectedCue.EndText = Timecode.Format(_position);
        UpdateOverlay();
    }
}
