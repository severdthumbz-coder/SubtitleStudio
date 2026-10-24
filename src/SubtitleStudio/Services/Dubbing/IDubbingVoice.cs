namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A loaded text-to-speech engine as the Dubbing path uses it: it turns a subtitle line into speech samples, says how
/// fast it runs, and reports its device. This is the surface the voice track and preview actually call (richer than
/// ITtsService, which is the catalogue-level contract) — so a second engine (Piper, a cloning model) can be dropped in
/// behind it without the dubbing view-model knowing which engine it is. Kokoro is the first implementation (KokoroTts).
/// </summary>
public interface IDubbingVoice : IDisposable
{
    /// <summary>The sample rate of the samples Speak returns. Per engine (Kokoro 24 kHz, Piper 22.05 kHz), so never assume one.</summary>
    int SampleRate { get; }

    /// <summary>"DirectML on AMD Radeon RX 6850M XT", "processor", or "card and processor".</summary>
    string DeviceLabel { get; }

    /// <summary>How many lines are best spoken at the same time on the device this engine runs on.</summary>
    int Concurrency { get; }

    /// <summary>The samples for a subtitle line in the given voice at the given speed (mono, at SampleRate); empty when there's nothing to say.</summary>
    float[] Speak(string subtitleText, string voiceId, float speed = 1f);

    /// <summary>What the engine will actually say for a line (phonemes/normalised text), for the Log. Empty when nothing is spoken.</summary>
    string Phonemes(string subtitleText);

    /// <summary>How many lines each device spoke since last asked, for the Log (null when the engine doesn't split across devices).</summary>
    string? TakeUsage() => null;
}
