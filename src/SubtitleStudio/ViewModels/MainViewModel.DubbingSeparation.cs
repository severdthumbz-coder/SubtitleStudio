using SubtitleStudio.Models;
using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Dubbing tab: the original voices removed, music and effects kept. The voices are separated from the
/// video's sound (Services/Dubbing/VoiceSeparator, the UVR MDX-Net model downloaded on first use), kept in the
/// temporary folder for the episode, and taken out where the English lines go (DubBed); each English line is
/// placed at the level and side of the voice it replaces (VoicePlacement).
/// </summary>
public sealed partial class MainViewModel
{
    private ISeparationModel? _separation;

    /// <summary>The original voices are taken out (not just turned down).</summary>
    public bool RemovingVoices => SelectedDubbingDuck.Level == DuckLevel.Removed;

    /// <summary>What the chosen level does, under the list.</summary>
    public string DubbingDuckHelp => RemovingVoices
        ? $"The original voices are taken out where the English lines go, keeping the music, effects and background. Each line is as loud as the voice it replaces, on the same side. The voice separation model ({TtsModelStore.SeparationModel.SizeText}) downloads the first time; separating takes a few minutes per episode and is kept for the next time."
        : "The whole original sound is turned down while the English voice speaks, and comes back up between lines. The original voices can still be heard underneath.";

    /// <summary>The separation model: downloaded the first time (checked), loaded once.</summary>
    private async Task<VoiceSeparator> EnsureSeparatorAsync(double from, CancellationToken ct)
    {
        if (!_s.TtsModels.HasSeparation)
        {
            DubbingStatus = $"Downloading the voice separation model ({TtsModelStore.SeparationModel.SizeText})...";
            var dl = new Progress<ModelDownloadProgress>(p => { if (_dubbingLive) DubbingProgress = from + p.Fraction * 2; });
            await _s.TtsModels.DownloadSeparationAsync(ModelHttp, dl, ct);
            _s.Log.Success("Dubbing", $"Voice separation model downloaded and checked: {TtsModelStore.SeparationModel.FileName}.");
        }
        if (_separation is null)
        {
            if (_s.LoadSeparationModel is not { } load) throw new InvalidOperationException("Voice separation isn't available in this copy of the app.");
            DubbingStatus = "Loading the voice separation model...";
            var gpu = _gpu ?? await Task.Run(GpuDetector.Detect, ct);
            var path = _s.TtsModels.SeparationPath;
            _separation = await Task.Run(() => load(path, gpu), ct);
        }
        return new VoiceSeparator(_separation);
    }

    /// <summary>
    /// The separated voices of the video's audio track: from the temporary folder if this video was separated
    /// before (with the same model), else separated now into <paramref name="share"/> percent of the bar.
    /// </summary>
    private async Task<string> SeparatedVoicesAsync(MediaItem video, string ffmpeg, double from, double share, CancellationToken ct)
    {
        var cached = DubBed.CachePath(video.FullPath, 0, TtsModelStore.SeparationModel.FileName);
        if (File.Exists(cached))
        {
            _s.Log.Info("Dubbing", $"Original voices already separated for {video.Name} (kept from last time): {cached}.");
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow); // the newest is kept
            DubbingProgress = from + share;
            return cached;
        }
        var separator = await EnsureSeparatorAsync(from, ct);
        var device = _separation!.DeviceLabel;
        var started = DateTime.UtcNow;
        var progress = new Progress<double>(f =>
        {
            if (!_dubbingLive) return;
            DubbingProgress = from + 2 + f * (share - 2);
            var elapsed = DateTime.UtcNow - started;
            var eta = f > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f) : (TimeSpan?)null;
            DubbingStatus = $"Taking the original voices out of the sound ({f:P0}" + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty) + ")...";
        });
        DubbingStatus = "Taking the original voices out of the sound...";
        _s.Log.Info("Dubbing", $"Separating the voices of {video.Name} on {device}...");
        await Task.Run(() => DubBed.SeparateToFileAsync(ffmpeg, video.FullPath, 0, separator, cached, video.Duration, progress, ct), ct);
        var took = DateTime.UtcNow - started;
        var length = video.Duration ?? TimeSpan.Zero;
        _s.Log.Success("Dubbing", $"Voices separated in {took.TotalMinutes:0.0} min"
            + (length > TimeSpan.Zero ? $" ({length.TotalSeconds / Math.Max(1, took.TotalSeconds):0.0}x real time)" : "")
            + $" on {device}; kept for next time in {DubBed.CacheFolder}.");
        DubBed.TrimCache(cached);
        return cached;
    }

    /// <summary>
    /// The voices of a preview's stretch: cut from the episode's separated voices when there are some,
    /// else separated now (a minute takes a few seconds). Returns them with the stretch's original sound.
    /// </summary>
    private async Task<(float[] MixLeft, float[] MixRight, float[] VoicesLeft, float[] VoicesRight)> PreviewVoicesAsync(
        MediaItem video, string ffmpeg, TimeSpan start, TimeSpan length, CancellationToken ct)
    {
        var (mixLeft, mixRight) = await DubBed.ReadAsync(ffmpeg, video.FullPath, 0, start, length, ct);
        var cached = DubBed.CachePath(video.FullPath, 0, TtsModelStore.SeparationModel.FileName);
        if (File.Exists(cached))
        {
            var l = new float[mixLeft.Length];
            var r = new float[mixLeft.Length];
            await Task.Run(() =>
            {
                using var wav = new WavReader(cached);
                wav.Read((long)Math.Round(start.TotalSeconds * DubBed.SampleRate), l, r);
            }, ct);
            return (mixLeft, mixRight, l, r);
        }
        var separator = await EnsureSeparatorAsync(0, ct);
        DubbingStatus = "Taking the original voices out of the stretch...";
        var progress = new Progress<double>(f => { if (_dubbingLive) DubbingProgress = 2 + f * 28; });
        var (vl, vr) = await Task.Run(() => separator.Separate(mixLeft, mixRight, progress, ct), ct);
        return (mixLeft, mixRight, vl, vr);
    }
}
