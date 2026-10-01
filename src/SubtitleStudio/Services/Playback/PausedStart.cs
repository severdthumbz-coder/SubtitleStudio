namespace SubtitleStudio.Services.Playback;

/// <summary>
/// Opening a video paused at a given time, so the picture shows: pausing the moment playback starts
/// leaves the video surface black (nothing has been drawn yet). Instead the player plays, silently, until
/// it reports a time at or past the target (a frame there has been drawn), then pauses and settles on
/// the target. If no time arrives (a stalled decoder), it pauses anyway after <see cref="Timeout"/>.
/// Pure logic, so it can be tested without VLC.
/// </summary>
public sealed class PausedStart
{
    /// <summary>Longest silent playback before pausing regardless.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>A reported time this far before the target still counts (keyframes, rounding).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(150);

    private TimeSpan _target;
    private DateTime _startedUtc;

    /// <summary>Waiting for the first frame at the target.</summary>
    public bool Active { get; private set; }

    public TimeSpan Target => _target;

    public void Begin(TimeSpan target, DateTime nowUtc)
    {
        _target = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        _startedUtc = nowUtc;
        Active = true;
    }

    /// <summary>A time reported while playing. True once: pause now and settle on <see cref="Target"/>.</summary>
    public bool OnTime(TimeSpan reported, DateTime nowUtc)
    {
        if (!Active) return false;
        if (reported + Tolerance >= _target || nowUtc - _startedUtc >= Timeout)
        {
            Active = false;
            return true;
        }
        return false;
    }

    /// <summary>Polled (no time reported at all): true once when the timeout has passed.</summary>
    public bool TimedOut(DateTime nowUtc)
    {
        if (!Active || nowUtc - _startedUtc < Timeout) return false;
        Active = false;
        return true;
    }

    public void Cancel() => Active = false;
}
