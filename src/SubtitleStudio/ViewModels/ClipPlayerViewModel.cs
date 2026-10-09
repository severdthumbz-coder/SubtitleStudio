using System.Globalization;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Playback;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// One player for a short clip (the dub preview), with play/pause, 2-second steps and a slider. Times are
/// shown in the original video's timeline: the clip starts at <see cref="Offset"/> in it.
/// </summary>
public sealed class ClipPlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackService _player;
    private readonly Action<Action> _runOnUi;
    private TimeSpan _position, _length;
    private bool _isPlaying;

    public ClipPlayerViewModel(IPlaybackService player, Action<Action> runOnUi)
    {
        _player = player;
        _runOnUi = runOnUi;
        _player.PositionChanged += p => _runOnUi(() =>
        {
            _position = p;
            RaisePosition();
        });
        _player.PlayingChanged += playing => _runOnUi(() => IsPlaying = playing);
        _player.Failed += m => _runOnUi(() => Failed?.Invoke(m));

        PlayPauseCommand = new RelayCommand(TogglePlay, () => IsLoaded);
        BackCommand = new RelayCommand(() => Seek(_position - TimeSpan.FromSeconds(2)), () => IsLoaded);
        ForwardCommand = new RelayCommand(() => Seek(_position + TimeSpan.FromSeconds(2)), () => IsLoaded);
        RestartCommand = new RelayCommand(() => { Seek(TimeSpan.Zero); if (!IsPlaying) TogglePlay(); }, () => IsLoaded);
    }

    public event Action<string>? Failed;

    public IPlaybackService Player => _player;
    public object? Handle => _player.NativePlayer;
    public bool IsLoaded => _player.LoadedPath is not null;

    /// <summary>Where the clip starts in the original video.</summary>
    public TimeSpan Offset { get; private set; }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public double PositionSeconds
    {
        get => _position.TotalSeconds;
        set
        {
            if (Math.Abs(value - _position.TotalSeconds) < 0.05) return;
            Seek(TimeSpan.FromSeconds(value));
        }
    }

    public double LengthSeconds => Math.Max(0.1, _length.TotalSeconds);

    /// <summary>"12:04 / 12:34", in the original video's time.</summary>
    public string PositionText => $"{Format(Offset + _position)} / {Format(Offset + _length)}";

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand RestartCommand { get; }

    public bool Load(string path, TimeSpan offset, TimeSpan length)
    {
        if (!_player.EnsureInitialized()) return false;
        Offset = offset;
        _length = length;
        _position = TimeSpan.Zero;
        _player.Load(path, TimeSpan.Zero);
        IsPlaying = false;
        OnPropertyChanged(nameof(Handle));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(LengthSeconds));
        RaisePosition();
        RelayCommand.Refresh();
        return true;
    }

    public void Unload()
    {
        if (_player.LoadedPath is not null) _player.Unload();
        IsPlaying = false;
        OnPropertyChanged(nameof(IsLoaded));
        RelayCommand.Refresh();
    }

    public void TogglePlay()
    {
        if (!IsLoaded) return;
        if (_player.IsPlaying || IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }
        if (_position >= _length - TimeSpan.FromMilliseconds(150)) _player.Seek(TimeSpan.Zero);
        _player.Play();
        IsPlaying = true;
    }

    public void Seek(TimeSpan position)
    {
        if (!IsLoaded) return;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > _length) position = _length;
        _player.Seek(position);
        _position = position;
        RaisePosition();
    }

    /// <summary>The view's video surface is ready (see IPlaybackService.AttachOutput).</summary>
    public void Attach() => _player.AttachOutput();

    public void Detach()
    {
        _player.DetachOutput();
        IsPlaying = false;
    }

    private void RaisePosition()
    {
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
    }

    private static string Format(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    public void Dispose() => _player.Dispose();
}
