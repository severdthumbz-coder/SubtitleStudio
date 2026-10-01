using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.BurnedIn;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Crash recovery (Settings: "Offer to restore my session after a crash"). While the app runs, the session
/// is saved every few seconds when something changed (and at once when a Create video or Extract starts);
/// a normal close clears it. After a crash, App asks whether to restore it, and whether to restart the
/// interrupted job. Storage is Services/SessionStore.
/// </summary>
public sealed partial class MainViewModel
{
    private SessionJob? _sessionJob;
    private Timer? _sessionTimer;
    private bool _restoring;

    /// <summary>How often the session is checked for changes and saved.</summary>
    public static readonly TimeSpan SessionSaveInterval = TimeSpan.FromSeconds(5);

    public bool RestoreSessionAfterCrash
    {
        get => _s.Config.RestoreSessionAfterCrash;
        set
        {
            if (_s.Config.RestoreSessionAfterCrash == value) return;
            _s.Config.RestoreSessionAfterCrash = value;
            OnPropertyChanged();
            SaveSettings();
            if (value) SaveSessionNow();
            else _s.Session?.Clear();
        }
    }

    /// <summary>Starts saving the session in the background (after start-up and any restore).</summary>
    public void StartSessionAutosave()
    {
        if (_s.Session is null || _sessionTimer is not null) return;
        _sessionTimer = new Timer(_ => RunOnUi(SaveSessionNow), null, SessionSaveInterval, SessionSaveInterval);
    }

    /// <summary>Normal close: stop saving and leave nothing to restore.</summary>
    public void EndSession()
    {
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _s.Session?.Clear();
    }

    private void BeginSessionJob(SessionJobKind kind, string videoPath, string? outputPath)
    {
        _sessionJob = new SessionJob(kind, videoPath, outputPath, DateTime.UtcNow);
        SaveSessionNow();
    }

    private void EndSessionJob()
    {
        _sessionJob = null;
        SaveSessionNow();
    }

    /// <summary>Captures the session (UI thread) and saves it if it changed; an empty app clears it.</summary>
    public void SaveSessionNow()
    {
        if (_s.Session is not { } store || !_s.Config.RestoreSessionAfterCrash || _restoring) return;
        try
        {
            var snapshot = CaptureSession();
            if (!snapshot.HasContent) store.Clear();
            else store.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _s.Log.Detail("Session", "The session could not be saved: " + ex.Message);
        }
    }

    public SessionSnapshot CaptureSession()
    {
        var snapshot = new SessionSnapshot
        {
            AppVersion = AppInfo.Version,
            Files = Files.Select(f => f.FullPath).ToList(),
            SelectedFile = SelectedFile?.FullPath,
            SelectedTab = SelectedTabIndex,
            Job = _sessionJob,
        };
        if (BurnedInVideo is { } video)
        {
            snapshot.BurnedIn = new BurnedInSession
            {
                VideoPath = video.FullPath,
                Detection = _detection?.Result,
                Video = _detection?.Video,
                Scale = _detection?.Scale ?? 1,
                BandTopPercent = _bandTopPercent,
                BandBottomPercent = _bandBottomPercent,
                CleanUp = _burnedInCleanup,
                ExtractFramesPerSecond = SelectedExtractSpeed.FramesPerSecond,
                RemovalMethod = SelectedRemovalMethod.Method.ToString(),
                FrameSeconds = _frameTime.TotalSeconds,
            };
        }
        if (_doc is { } doc)
        {
            int selected = SelectedCue is { } cue ? Cues.IndexOf(cue) : 0;
            snapshot.Editor = EditorSession.From(doc, IsDirty, PairedVideo?.FullPath, Math.Max(0, selected), _standaloneBaseName);
        }
        return snapshot;
    }

    /// <summary>
    /// Puts a saved session back: files (missing ones are reported by the import), the Burned-in video
    /// with its Detect result and settings, the subtitle editor with its unsaved changes, the tab. With
    /// <paramref name="restartJob"/>, the interrupted Create video or Extract starts again.
    /// </summary>
    public async Task RestoreSessionAsync(SessionSnapshot snapshot, bool restartJob)
    {
        _restoring = true;
        try
        {
            var existing = snapshot.Files.Where(File.Exists).ToList();
            int missing = snapshot.Files.Count - existing.Count;
            if (existing.Count > 0) await ImportAsync(existing, "restored session");
            MediaItem? Find(string? path) => path is null ? null : Files.FirstOrDefault(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase));

            if (snapshot.BurnedIn is { } b && Find(b.VideoPath) is { } video)
            {
                BurnedInVideo = video;
                if (ExtractSpeeds.FirstOrDefault(s => Math.Abs(s.FramesPerSecond - b.ExtractFramesPerSecond) < 0.001) is { } speed) SelectedExtractSpeed = speed;
                if (Enum.TryParse<RemovalMethod>(b.RemovalMethod, out var method) && RemovalMethods.FirstOrDefault(m => m.Method == method) is { } option)
                    SelectedRemovalMethod = option;
                if (b.Detection is not null && b.Video is not null)
                {
                    _detection = new DetectionRun(b.Detection, b.Video, null, b.Scale);
                    _burnedInCleanup = b.CleanUp;
                    _bandTopPercent = b.BandTopPercent;
                    _bandBottomPercent = b.BandBottomPercent;
                    OnPropertyChanged(nameof(BurnedInCleanup));
                    OnPropertyChanged(nameof(BandTopPercent));
                    OnPropertyChanged(nameof(BandBottomPercent));
                    RaiseBandChanged();
                    OnPropertyChanged(nameof(HasDetection));
                    OnPropertyChanged(nameof(DetectionLikely));
                    OnPropertyChanged(nameof(DetectionHeadline));
                    OnPropertyChanged(nameof(DetectionDetails));
                    OnPropertyChanged(nameof(DetectionExamples));
                    OnPropertyChanged(nameof(IgnoredStaticTexts));
                    BurnedInStatus = "Restored from your previous session: Detect doesn't need to run again.";
                    // The frame on screen is read again from the video.
                    _frameTime = TimeSpan.FromSeconds(b.FrameSeconds);
                    OnPropertyChanged(nameof(FrameSeconds));
                    OnPropertyChanged(nameof(VideoSeconds));
                    OnPropertyChanged(nameof(FrameTimeText));
                    ScheduleFrameRead(grab: true);
                }
            }

            if (snapshot.Editor is { Cues.Count: > 0 } e)
            {
                var doc = e.ToDocument();
                if (e.StandaloneBaseName is { } baseName) _standaloneBaseName = baseName;
                LoadDocument(doc, Find(e.PairedVideoPath), e.SelectedIndex);
                IsDirty = e.Dirty;
            }

            if (Find(snapshot.SelectedFile) is { } selected) SelectedFile = selected;
            SelectedTabIndex = snapshot.SelectedTab;
            RelayCommand.Refresh();

            var restored = "Previous session restored" + (missing > 0 ? $" ({missing} file(s) no longer found)" : string.Empty) + ".";
            SetStatus(restored, missing > 0 ? StatusKind.Warning : StatusKind.Success);
            _s.Log.Info("Session", restored + " Saved " + snapshot.SavedUtc.ToLocalTime().ToString("g") + (snapshot.AppVersion is { } v ? $" by build {v}." : "."));
        }
        finally
        {
            _restoring = false;
        }

        if (restartJob && snapshot.Job is { } job) await RestartJobAsync(job);
        SaveSessionNow();
    }

    private async Task RestartJobAsync(SessionJob job)
    {
        if (job.Kind == SessionJobKind.Transcribe)
        {
            await RestartTranscribeAsync(job);
            return;
        }
        if (BurnedInVideo is not { } video || !string.Equals(video.FullPath, job.VideoPath, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"The interrupted {JobName(job.Kind)} couldn't be restarted: {Path.GetFileName(job.VideoPath)} is no longer available.", StatusKind.Warning);
            return;
        }
        if (_detection is null)
        {
            SetStatus($"The interrupted {JobName(job.Kind)} needs Detect first: press Detect, then start it again.", StatusKind.Warning);
            return;
        }
        SelectedTabIndex = Tabs.BurnedIn;
        _s.Log.Info("Session", $"Restarting the interrupted {JobName(job.Kind)} of {video.Name}" + (job.OutputPath is { } o ? $" -> {o}." : "."));
        if (job.Kind == SessionJobKind.CreateVideo && job.OutputPath is { } output)
        {
            // The half-written file from the crash is replaced.
            await CreateCleanVideoAsync(output);
        }
        else if (job.Kind == SessionJobKind.Extract)
        {
            await ExtractBurnedInAsync();
        }
    }

    public static string JobName(SessionJobKind kind) => kind switch
    {
        SessionJobKind.CreateVideo => "Create video",
        SessionJobKind.Transcribe => "Transcribe",
        _ => "Extract",
    };
}
