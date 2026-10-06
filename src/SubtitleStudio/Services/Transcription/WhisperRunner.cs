namespace SubtitleStudio.Services.Transcription;

/// <summary>Where whisper.cpp runs: a Vulkan device (by its index in the Vulkan list) or the processor.</summary>
public sealed record WhisperDevice(bool UseGpu, int? VulkanIndex, string Label)
{
    public static WhisperDevice Processor { get; } = new(false, null, "processor");

    public static WhisperDevice Gpu(VulkanDevice device) => new(true, device.Index, device.Name);
}

/// <param name="CarryContext">Feed the text so far back in as context. Off by default: after a long pause
/// Whisper then tends to repeat earlier lines instead of hearing new ones.</param>
/// <param name="ChunkLanguages">A language per chunk (scenes in another language); null entries, or no list, use <paramref name="Language"/>.</param>
public sealed record WhisperRunOptions(string ModelPath, string? Language, bool Translate, WhisperDevice Device, int Threads, bool CarryContext = false,
    IReadOnlyList<string?>? ChunkLanguages = null);

/// <summary>One token of a segment: text (may be part of a character), times, probability.</summary>
public sealed record WhisperTokenInfo(string Text, TimeSpan Start, TimeSpan End, float Probability, bool Special);

/// <summary>A piece of speech as whisper.cpp returns it (usually a sentence or part of one).</summary>
/// <param name="Chunk">Which chunk of audio it came from (times are within that chunk).</param>
public sealed record WhisperSegment(TimeSpan Start, TimeSpan End, string Text, float Probability, float NoSpeechProbability,
    string? Language, IReadOnlyList<WhisperTokenInfo> Tokens, int Chunk = 0);

/// <summary>Runs a Whisper model over 16 kHz mono samples. Implemented with Whisper.net (whisper.cpp).</summary>
public interface IWhisperRunner : IDisposable
{
    /// <summary>What the last run actually used, e.g. "Vulkan: AMD Radeon RX 6850M XT" or "processor, 8 threads".</summary>
    string? DeviceUsed { get; }

    /// <summary>Why a requested graphics card wasn't used (null when it was, or wasn't asked for).</summary>
    string? DeviceProblem { get; }

    /// <summary>Loads the model if needed. Separate so the time to load and to transcribe can be told apart.</summary>
    Task LoadAsync(WhisperRunOptions options, CancellationToken ct);

    /// <summary>
    /// Where the speech is (Silero VAD), or null when the speech detector isn't available (then the
    /// caller falls back to loudness). Loads the native library with the device of <paramref name="options"/>.
    /// </summary>
    Task<IReadOnlyList<SpeechRegion>?> DetectSpeechAsync(float[] samples, WhisperRunOptions options, CancellationToken ct);

    /// <summary>The language found by automatic detection in the last run, and how sure it was (0..1).</summary>
    (string Code, float Probability)? DetectedLanguage { get; }

    /// <summary>
    /// Transcribes the chunks in order with one loaded model. With automatic language detection the
    /// language is detected once, from the first chunk, and kept for the rest (a short chunk of music
    /// would otherwise be "detected" as something else). Progress: (chunk, percent of that chunk).
    /// </summary>
    IAsyncEnumerable<WhisperSegment> RunAsync(IReadOnlyList<ReadOnlyMemory<float>> chunks, WhisperRunOptions options, IProgress<(int Chunk, int Percent)>? progress, CancellationToken ct);

    /// <summary>
    /// The language spoken in each piece of audio, and how sure (0..1), with the loaded model; null for a
    /// piece it couldn't judge. Null altogether when this runner can't detect languages.
    /// </summary>
    /// <param name="candidates">Only these languages are considered (null: all).</param>
    Task<IReadOnlyList<(string Code, float Probability)?>?> DetectLanguagesAsync(IReadOnlyList<ReadOnlyMemory<float>> audio, WhisperRunOptions options,
        IProgress<int>? progress, CancellationToken ct, IReadOnlyList<string>? candidates = null)
        => Task.FromResult<IReadOnlyList<(string Code, float Probability)?>?>(null);

    /// <summary>Frees the loaded model (graphics memory) until the next run, e.g. before the translator loads its own.</summary>
    void ReleaseModel() { }
}
