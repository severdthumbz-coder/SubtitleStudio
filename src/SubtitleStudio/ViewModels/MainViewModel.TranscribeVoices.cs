using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// AI Transcribe (and Batch): listening to the voices alone. The music and effects are taken out first with
/// the voice separation model that dubbing uses (downloaded the first time), and the episode's separated voices
/// are kept in the temporary folder, so a full dub of the same episode doesn't separate them again.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _separationGate = new(1, 1);

    /// <summary>The engine's listening choices from the settings.</summary>
    private void ConfigureListening(LocalWhisperEngine engine)
    {
        engine.WriteHesitations = TranscribeHesitations;
        engine.SeparateVoices = TranscribeVoicesAlone && _s.LoadSeparationModel is not null ? TranscribeVoicesAsync : null;
    }

    private async Task<VoicesSource?> TranscribeVoicesAsync(VoicesRequest request, CancellationToken ct)
    {
        if (_s.Ffmpeg.Resolve(_s.Config.FfmpegPath).FfmpegPath is not { } ffmpeg) return null;
        var cached = DubBed.CachePath(request.MediaPath, request.Track, TtsModelStore.SeparationModel.FileName);
        if (File.Exists(cached))
        {
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            return new VoicesSource(cached, true, false);
        }
        VoiceSeparator separator;
        try
        {
            separator = await SeparatorForTranscribeAsync(request.Progress, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException)
        {
            _s.Log.Warning("Transcribe", "The voices couldn't be separated (" + ex.Message + "); listening to the sound as it is.");
            return null;
        }
        var started = DateTime.UtcNow;
        bool whole = request.Start is null && request.Length is null;
        var target = whole ? cached : Path.Combine(Path.GetTempPath(), "SubtitleStudio", $"transcribe-{Guid.NewGuid():N}.voices.wav");
        var expected = request.Length ?? (request.MediaDuration is { } d ? d - (request.Start ?? TimeSpan.Zero) : null);
        await Task.Run(() => DubBed.SeparateToFileAsync(ffmpeg, request.MediaPath, request.Track, separator, target, expected, request.Progress, ct, request.Start, request.Length), ct);
        var took = DateTime.UtcNow - started;
        _s.Log.Info("Transcribe", $"Voices separated from the music and effects in {took.TotalMinutes:0.0} min on {_separation?.DeviceLabel ?? "?"}"
            + (expected is { } e && took.TotalSeconds > 0 ? $" ({e.TotalSeconds / took.TotalSeconds:0.0}x real time)" : "")
            + (whole ? $"; kept for dubbing and the next time in {DubBed.CacheFolder}." : " (this part only)."));
        if (whole) DubBed.TrimCache(cached);
        return new VoicesSource(target, whole, !whole);
    }

    /// <summary>The separation model for Transcribe: downloaded the first time (checked), loaded once and shared with dubbing.</summary>
    private async Task<VoiceSeparator> SeparatorForTranscribeAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _separationGate.WaitAsync(ct);
        try
        {
            if (!_s.TtsModels.HasSeparation)
            {
                _s.Log.Info("Transcribe", $"Downloading the voice separation model ({TtsModelStore.SeparationModel.SizeText}), once...");
                await _s.TtsModels.DownloadSeparationAsync(ModelHttp, null, ct);
                _s.Log.Success("Transcribe", $"Voice separation model downloaded and checked: {TtsModelStore.SeparationModel.FileName}.");
            }
            progress?.Report(0);
            if (_separation is null)
            {
                if (_s.LoadSeparationModel is not { } load) throw new InvalidOperationException("Voice separation isn't available in this copy of the app.");
                var gpu = _gpu ?? await Task.Run(GpuDetector.Detect, ct);
                var path = _s.TtsModels.SeparationPath;
                _separation = await Task.Run(() => load(path, gpu), ct);
            }
            return new VoiceSeparator(_separation);
        }
        finally
        {
            _separationGate.Release();
        }
    }
}
