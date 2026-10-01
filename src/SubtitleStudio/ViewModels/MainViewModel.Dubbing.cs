using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Dubbing tab. Build 1: engine registry only. Per-cue TTS, time-fit and ffmpeg mux (to a NEW file,
/// never the original) arrive in a later build.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<ITtsService> TtsEngines => _s.TtsEngines;

    public bool HasTtsEngines => _s.TtsEngines.Count > 0;

    private void InitDubbing()
    {
    }
}
