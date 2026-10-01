using System.Globalization;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Playback;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Before / after view: two players side by side, kept in step. The original ("before") leads and has
/// the sound; the result ("after") is muted and follows it. A result can be a short preview clip that
/// starts at <see cref="Offset"/> in the original: the players then stay within that stretch.
/// </summary>
public sealed class ComparePlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>The follower is re-seeked when it drifts further than this from the leader.</summary>
    public static readonly TimeSpan MaxDrift = TimeSpan.FromMilliseconds(200);

    private readonly IPlaybackService _before;
    private readonly IPlaybackService _after;
    private readonly Action<Action> _runOnUi;
    private TimeSpan _position, _rangeStart, _rangeEnd;
    private bool _isPlaying;
    private string? _beforeName, _afterName;

    public ComparePlayerViewModel(IPlaybackService before, IPlaybackService after, Action<Action> runOnUi)
    {
        _before = before;
        _after = after;
        _runOnUi = runOnUi;

        _before.PositionChanged += p => _runOnUi(() => OnLeaderPosition(p));
        _before.PlayingChanged += playing => _runOnUi(() =>
        {
            IsPlaying = playing;
            if (!playing && _after.IsPlaying) _after.Pause();
        });
        _before.Failed += m => _runOnUi(() => Failed?.Invoke(m));
        _after.Failed += m => _runOnUi(() => Failed?.Invoke(m));

        PlayPauseCommand = new RelayCommand(TogglePlay, () => IsLoaded);
        BackCommand = new RelayCommand(() => Seek(_position - TimeSpan.FromSeconds(2)), () => IsLoaded);
        ForwardCommand = new RelayCommand(() => Seek(_position + TimeSpan.FromSeconds(2)), () => IsLoaded);
    }

    public event Action<string>? Failed;

    public IPlaybackService Before => _before;
    public IPlaybackService After => _after;
    public object? BeforeHandle => _before.NativePlayer;
    public object? AfterHandle => _after.NativePlayer;

    public bool IsLoaded => _before.LoadedPath is not null && _after.LoadedPath is not null;

    /// <summary>Where the result starts in the original's timeline (a preview clip), else zero.</summary>
    public TimeSpan Offset { get; private set; }

    public string BeforeName { get => _beforeName ?? string.Empty; private set => SetProperty(ref _beforeName, value); }
    public string AfterName { get => _afterName ?? string.Empty; private set => SetProperty(ref _afterName, value); }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    /// <summary>Slider: seconds in the original's timeline, limited to the stretch the result covers.</summary>
    public double PositionSeconds
    {
        get => _position.TotalSeconds;
        set
        {
            if (Math.Abs(value - _position.TotalSeconds) < 0.05) return;
            Seek(TimeSpan.FromSeconds(value));
        }
    }

    public double RangeStartSeconds => _rangeStart.TotalSeconds;
    public double RangeEndSeconds => Math.Max(_rangeStart.TotalSeconds + 0.1, _rangeEnd.TotalSeconds);

    public string PositionText => $"{Format(_position)} / {Format(_rangeEnd)}";

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }

    /// <summary>
    /// Loads both videos paused at <paramref name="startAt"/> (original timeline). <paramref name="resultLength"/>
    /// is the result's duration; the result begins at <paramref name="offset"/> in the original.
    /// </summary>
    public bool Load(string beforePath, string afterPath, TimeSpan offset, TimeSpan resultLength, TimeSpan startAt)
    {
        if (!_before.EnsureInitialized() || !_after.EnsureInitialized()) return false;
        Offset = offset;
        _rangeStart = offset;
        _rangeEnd = offset + resultLength;
        var start = Clamp(startAt);
        _before.Load(beforePath, start);
        _after.Load(afterPath, start - offset, withAudio: false);
        _position = start;
        BeforeName = Path.GetFileName(beforePath);
        AfterName = Path.GetFileName(afterPath);
        IsPlaying = false;
        OnPropertyChanged(nameof(BeforeHandle));
        OnPropertyChanged(nameof(AfterHandle));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(RangeStartSeconds));
        OnPropertyChanged(nameof(RangeEndSeconds));
        RaisePosition();
        RelayCommand.Refresh();
        return true;
    }

    public void Unload()
    {
        if (_before.LoadedPath is not null) _before.Unload();
        if (_after.LoadedPath is not null) _after.Unload();
        IsPlaying = false;
        OnPropertyChanged(nameof(IsLoaded));
        RelayCommand.Refresh();
    }

    public void TogglePlay()
    {
        if (!IsLoaded) return;
        if (_before.IsPlaying || IsPlaying)
        {
            _before.Pause();
            _after.Pause();
            IsPlaying = false;
            return;
        }
        if (_position >= _rangeEnd - TimeSpan.FromMilliseconds(100)) Seek(_rangeStart);
        _after.Seek(_position - Offset);
        _before.Play();
        _after.Play();
        IsPlaying = true;
    }

    public void Seek(TimeSpan position)
    {
        if (!IsLoaded) return;
        position = Clamp(position);
        _before.Seek(position);
        _after.Seek(position - Offset);
        _position = position;
        RaisePosition();
    }

    /// <summary>The view's video surfaces (see IPlaybackService.AttachOutput).</summary>
    public void AttachBefore() => _before.AttachOutput();
    public void AttachAfter() => _after.AttachOutput();

    public void Detach()
    {
        _before.DetachOutput();
        _after.DetachOutput();
        IsPlaying = false;
    }

    private void OnLeaderPosition(TimeSpan position)
    {
        _position = position;
        RaisePosition();
        if (!IsPlaying) return;

        if (position >= _rangeEnd)
        {
            // End of the preview stretch: stop both.
            _before.Pause();
            _after.Pause();
            IsPlaying = false;
            return;
        }
        var expected = position - Offset;
        if ((_after.Position - expected).Duration() > MaxDrift) _after.Seek(expected);
    }

    private TimeSpan Clamp(TimeSpan t) => t < _rangeStart ? _rangeStart : t > _rangeEnd ? _rangeEnd : t;

    private void RaisePosition()
    {
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
    }

    private static string Format(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _before.Dispose();
        _after.Dispose();
    }
}
