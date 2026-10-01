namespace SubtitleStudio.Services.Playback;

/// <summary>
/// Video playback used by the Subtitles tab and the burned-in before/after view (one instance per player).
/// Kept behind an interface so the view model never touches LibVLC types directly.
/// Events may be raised on a background (player) thread; subscribers marshal to the UI.
/// </summary>
public interface IPlaybackService : IDisposable
{
    /// <summary>Loads the player engine on first use. False when it can't (see UnavailableReason).</summary>
    bool EnsureInitialized();

    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    /// <summary>Engine object for the view to attach (LibVLCSharp MediaPlayer). Null until initialized.</summary>
    object? NativePlayer { get; }

    string? LoadedPath { get; }
    bool IsPlaying { get; }
    TimeSpan Position { get; }
    TimeSpan Length { get; }

    /// <summary>
    /// The view's video surface is on screen and connected to <see cref="NativePlayer"/>. Until then a
    /// loaded file waits: LibVLC opens its own popup window when it plays with nowhere to draw.
    /// </summary>
    void AttachOutput();

    /// <summary>The video surface is going away (tab hidden): stop, and resume at the same spot on the next attach.</summary>
    void DetachOutput();

    /// <summary>Opens a file paused at <paramref name="startAt"/> (the first frame is shown). Without audio for a second, muted player.</summary>
    void Load(string path, TimeSpan startAt, bool withAudio = true);

    void Unload();
    void Play();
    void Pause();
    void TogglePlay();
    void Seek(TimeSpan position);

    event Action<TimeSpan>? PositionChanged;
    event Action<TimeSpan>? LengthChanged;
    event Action<bool>? PlayingChanged;
    event Action<string>? Failed;
}
