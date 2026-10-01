using LibVLCSharp.Shared;

namespace SubtitleStudio.Services.Playback;

/// <summary>
/// LibVLC implementation. Rules that matter with LibVLC:
///  - never call back into the player from one of its own event handlers (it deadlocks), so
///    follow-up work from events is queued to the thread pool;
///  - never play without the view's video surface attached: with no window handle, VLC opens its own
///    "VLC (Direct3D11 output)" popup. Loads wait for AttachOutput, and DetachOutput stops the video;
///  - Subtitle Studio draws its own cue overlay, so VLC's subtitle rendering is switched off
///    (":no-spu") to avoid showing embedded or auto-detected subtitles on top of ours;
///  - a video opened "paused at a time" must first draw a frame, or the surface stays black: it starts
///    at that time (":start-time"), plays silently until VLC reports a time there, then pauses and
///    settles on the exact time (see PausedStart).
/// </summary>
public sealed class VlcPlaybackService : IPlaybackService
{
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _initAttempted;
    private TimeSpan? _pendingSeek;
    private bool _pauseWhenStarted;
    private bool _outputAttached;
    private (string Path, TimeSpan At, bool Audio)? _waiting;
    private bool _withAudio = true;
    private readonly PausedStart _start = new();
    private Timer? _startTimer;
    private static readonly object CoreGate = new();
    private static bool _coreInitialized;

    public bool IsAvailable => _player is not null;
    public string? UnavailableReason { get; private set; }
    public object? NativePlayer => _player;
    public string? LoadedPath { get; private set; }
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public TimeSpan Position => _waiting is { } w ? w.At : _start.Active ? _start.Target : _player is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
    public TimeSpan Length => _player is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, _player.Length));

    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? LengthChanged;
    public event Action<bool>? PlayingChanged;
    public event Action<string>? Failed;

    public bool EnsureInitialized()
    {
        if (_player is not null) return true;
        if (_initAttempted) return false;
        _initAttempted = true;

        try
        {
            lock (CoreGate)
            {
                // Once per process: the before/after view creates a second player.
                if (!_coreInitialized)
                {
                    var dir = FindLibVlcDirectory();
                    if (dir is not null) Core.Initialize(dir);
                    else Core.Initialize();
                    _coreInitialized = true;
                }
            }

            _libVlc = new LibVLC("--no-video-title-show", "--no-sub-autodetect-file", "--no-snapshot-preview", "--quiet");
            _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };

            _player.TimeChanged += (_, e) => OnTimeChanged(TimeSpan.FromMilliseconds(Math.Max(0, e.Time)));
            _player.LengthChanged += (_, e) => LengthChanged?.Invoke(TimeSpan.FromMilliseconds(Math.Max(0, e.Length)));
            _player.Playing += (_, _) => OnPlaying();
            _player.Paused += (_, _) => PlayingChanged?.Invoke(false);
            _player.Stopped += (_, _) => PlayingChanged?.Invoke(false);
            _player.EndReached += (_, _) => PlayingChanged?.Invoke(false);
            _player.EncounteredError += (_, _) => Failed?.Invoke("The video could not be played (VLC reported an error).");
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"The video player (LibVLC) could not start: {ex.Message}";
            _player?.Dispose();
            _libVlc?.Dispose();
            _player = null;
            _libVlc = null;
            return false;
        }
    }

    public void AttachOutput()
    {
        (string Path, TimeSpan At, bool Audio)? start;
        lock (_gate)
        {
            _outputAttached = true;
            start = _waiting;
            _waiting = null;
        }
        if (start is { } s) StartMedia(s.Path, s.At, s.Audio);
    }

    public void DetachOutput()
    {
        if (_player is null) return;
        lock (_gate)
        {
            _outputAttached = false;
            if (LoadedPath is null || _waiting is not null) return;
            _waiting = (LoadedPath, TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)), _withAudio);
            _pendingSeek = null;
            _pauseWhenStarted = false;
        }
        EndPausedStart(restoreVolume: true);
        var media = _media;
        _media = null;
        _player.Stop();
        media?.Dispose();
        PlayingChanged?.Invoke(false);
    }

    public void Load(string path, TimeSpan startAt, bool withAudio = true)
    {
        if (!EnsureInitialized() || _player is null || _libVlc is null) return;
        lock (_gate)
        {
            LoadedPath = path;
            _withAudio = withAudio;
            if (!_outputAttached)
            {
                // Nowhere to draw yet: start when the view attaches (see class remarks).
                _waiting = (path, startAt, withAudio);
                return;
            }
        }
        StartMedia(path, startAt, withAudio);
    }

    private void StartMedia(string path, TimeSpan startAt, bool withAudio)
    {
        if (_player is null || _libVlc is null) return;
        lock (_gate)
        {
            var old = _media;
            _media = new Media(_libVlc, path, FromType.FromPath);
            _media.AddOption(":no-spu");
            if (!withAudio) _media.AddOption(":no-audio");
            // Start right at the time (no visible jump from the beginning); silent until paused there.
            if (startAt > TimeSpan.Zero)
                _media.AddOption(":start-time=" + startAt.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            _player.Volume = 0;
            _pendingSeek = startAt;
            _pauseWhenStarted = true;
            _player.Play(_media);
            old?.Dispose();
        }
    }

    public void Unload()
    {
        if (_player is null) return;
        EndPausedStart(restoreVolume: true);
        lock (_gate)
        {
            _pendingSeek = null;
            _pauseWhenStarted = false;
            _waiting = null;
            LoadedPath = null;
        }
        // Synchronous on purpose: a background Stop could race a following Load and stop the new file.
        var media = _media;
        _media = null;
        _player.Stop();
        media?.Dispose();
        PlayingChanged?.Invoke(false);
    }

    public void Play()
    {
        if (_player is null || LoadedPath is null || _waiting is not null) return;
        if (_start.Active)
        {
            // Pressed play while the first frame was being drawn: it is already playing, just audible now.
            EndPausedStart(restoreVolume: true);
            PlayingChanged?.Invoke(true);
            return;
        }
        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            // Finished media can't resume; restart it.
            _player.Stop();
            _player.Play();
        }
        else
        {
            _player.SetPause(false);
            if (!_player.IsPlaying && _player.State != VLCState.Paused) _player.Play();
        }
    }

    public void Pause() => _player?.SetPause(true);

    public void TogglePlay()
    {
        if (_player is null) return;
        if (_player.IsPlaying) Pause(); else Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_player is null || LoadedPath is null) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        bool priming;
        lock (_gate)
        {
            if (_waiting is { } w)
            {
                _waiting = (w.Path, position, w.Audio);
                PositionChanged?.Invoke(position);
                return;
            }
            priming = _start.Active;
            if (priming) _start.Begin(position, DateTime.UtcNow); // still drawing the first frame: aim for the new time
        }
        if (priming)
        {
            // Outside the lock: VLC's event thread takes it (see class remarks).
            _player.Time = (long)position.TotalMilliseconds;
            PositionChanged?.Invoke(position);
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            lock (_gate)
            {
                _pendingSeek = position;
                _pauseWhenStarted = true;
            }
            _player.Stop();
            _player.Volume = 0; // silent until paused on the frame (PausedStart)
            _player.Play();
            return;
        }

        _player.Time = (long)position.TotalMilliseconds;
        PositionChanged?.Invoke(position);
    }

    private void OnPlaying()
    {
        TimeSpan? seek;
        bool pause;
        lock (_gate)
        {
            seek = _pendingSeek;
            pause = _pauseWhenStarted;
            _pendingSeek = null;
            _pauseWhenStarted = false;
            if (pause) _start.Begin(seek ?? TimeSpan.Zero, DateTime.UtcNow);
        }

        if (!pause)
        {
            RestoreVolume();
            if (seek is { } s0 && s0 > TimeSpan.Zero)
                ThreadPool.QueueUserWorkItem(_ => { if (_player is { } p) p.Time = (long)s0.TotalMilliseconds; });
            PlayingChanged?.Invoke(true);
            return;
        }

        // Keep playing (silently) until a frame at the target has been drawn; see PausedStart. Not on
        // VLC's event thread: see class remarks.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var player = _player;
            if (player is null) return;
            // If the media ignored :start-time (some formats), jump there now.
            if (seek is { } s && s > TimeSpan.Zero && player.Time + PausedStart.Tolerance.TotalMilliseconds < s.TotalMilliseconds - 500)
                player.Time = (long)s.TotalMilliseconds;
            PositionChanged?.Invoke(seek ?? TimeSpan.Zero);
        });
        _startTimer?.Dispose();
        _startTimer = new Timer(_ =>
        {
            bool settle;
            lock (_gate) settle = _start.TimedOut(DateTime.UtcNow);
            if (settle) SettlePaused();
        }, null, 250, 250);
    }

    private void OnTimeChanged(TimeSpan time)
    {
        bool settle, priming;
        lock (_gate)
        {
            priming = _start.Active;
            settle = priming && _start.OnTime(time, DateTime.UtcNow);
        }
        if (settle) ThreadPool.QueueUserWorkItem(_ => SettlePaused()); // not on VLC's event thread
        else if (!priming) PositionChanged?.Invoke(time);
    }

    /// <summary>A frame has been drawn: pause, settle on the exact time, sound back on.</summary>
    private void SettlePaused()
    {
        var player = _player;
        _startTimer?.Dispose();
        _startTimer = null;
        if (player is null) return;
        player.SetPause(true);
        var target = _start.Target;
        player.Time = (long)target.TotalMilliseconds;
        RestoreVolume();
        PositionChanged?.Invoke(target);
        PlayingChanged?.Invoke(false);
    }

    private void EndPausedStart(bool restoreVolume)
    {
        lock (_gate) _start.Cancel();
        _startTimer?.Dispose();
        _startTimer = null;
        if (restoreVolume) RestoreVolume();
    }

    private void RestoreVolume()
    {
        if (_player is { } p && p.Volume != 100) p.Volume = 100;
    }

    /// <summary>
    /// libvlc ships as a libvlc\win-x64 folder. In the single-file EXE it is self-extracted with the
    /// app (AppContext.BaseDirectory); a copy next to the EXE is also accepted.
    /// </summary>
    private static string? FindLibVlcDirectory()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, AppPaths.ExeDirectory })
        {
            var dir = Path.Combine(root, "libvlc", "win-x64");
            if (File.Exists(Path.Combine(dir, "libvlc.dll"))) return dir;
        }
        return null;
    }

    public void Dispose()
    {
        _startTimer?.Dispose();
        _startTimer = null;
        var player = _player;
        _player = null;
        if (player is not null)
        {
            try { player.Stop(); } catch (Exception) { }
            player.Dispose();
        }
        _media?.Dispose();
        _libVlc?.Dispose();
        _media = null;
        _libVlc = null;
    }
}
