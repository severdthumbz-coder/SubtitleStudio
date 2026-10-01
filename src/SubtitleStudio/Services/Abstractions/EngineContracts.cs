using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Abstractions;

// ---------------------------------------------------------------------------------------------
// Engine contracts. The UI only ever talks to these interfaces; each provider (cloud or local)
// is one concrete class. Adding a provider = add a class + register it; no UI changes.
// Every long operation reports IProgress<EngineProgress> and honours a CancellationToken.
// Quota / rate-limit failures are thrown as ServiceQuotaException so the UI can say so plainly.
// ---------------------------------------------------------------------------------------------

/// <summary>Common identity for any AI engine.</summary>
public interface IEngineInfo
{
    /// <summary>Stable id, e.g. "openai-whisper", "whisper-cpp".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>True for offline engines (local binary / model).</summary>
    bool IsLocal { get; }

    /// <summary>True when an API key (see ProviderIds) must be configured in Settings.</summary>
    bool RequiresApiKey { get; }

    /// <summary>Provider id used to look up the API key (null for local engines).</summary>
    string? ApiKeyProviderId { get; }

    /// <summary>One or two sentences for the "which engine?" choice: cost, privacy, speed.</summary>
    string Description => DisplayName;
}

/// <summary>Progress in [0..1] plus a short human-readable message (and, for speech, the latest text heard).</summary>
public readonly record struct EngineProgress(double Fraction, string Message, string? LatestText = null);

/// <param name="Language">Spoken language code (e.g. "ko"); ignored when <paramref name="AutoDetectLanguage"/>.</param>
/// <param name="Model">Engine-specific model (a file path for local Whisper).</param>
/// <param name="TranslateToEnglish">Write English subtitles for speech in another language.</param>
/// <param name="AudioTrack">Which audio stream (0 = the first).</param>
/// <param name="Start">Only transcribe from here (null: the beginning).</param>
/// <param name="Length">Only transcribe this much (null: to the end).</param>
/// <param name="MediaDuration">The file's length, if known (for progress).</param>
public sealed record TranscriptionOptions(
    string? Language,
    bool AutoDetectLanguage,
    string? Model = null,
    bool TranslateToEnglish = false,
    int AudioTrack = 0,
    TimeSpan? Start = null,
    TimeSpan? Length = null,
    TimeSpan? MediaDuration = null);

/// <summary>Speech-to-text: media (or extracted audio) in, timed cues out.</summary>
public interface ITranscriptionService : IEngineInfo
{
    Task<SubtitleDocument> TranscribeAsync(
        string mediaPath,
        TranscriptionOptions options,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Cue-by-cue translation. Implementations must return one cue per input cue with identical
/// timings, and must preserve formatting tags (&lt;i&gt;, {\an8}, ...).
/// </summary>
public interface ITranslationService : IEngineInfo
{
    Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        string? sourceLanguage,
        string targetLanguage,
        IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record TtsVoice(string Id, string DisplayName, string? Language, string? Gender);

/// <summary>Text-to-speech used by Dubbing: one clip per cue, duration returned for time-fitting.</summary>
public interface ITtsService : IEngineInfo
{
    Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken cancellationToken);

    /// <returns>Duration of the audio written to <paramref name="outputPath"/>.</returns>
    Task<TimeSpan> SynthesizeAsync(
        string text,
        TtsVoice voice,
        string outputPath,
        CancellationToken cancellationToken);
}

/// <summary>A subtitle file format (SRT, VTT, ASS/SSA, SUB). One implementation per format.</summary>
public interface ISubtitleFormat
{
    /// <summary>srt, vtt, ass, ssa, sub.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Extensions including the dot, e.g. ".srt".</summary>
    IReadOnlyList<string> Extensions { get; }

    bool CanRead { get; }
    bool CanWrite { get; }

    SubtitleDocument Read(Stream stream);

    void Write(SubtitleDocument document, Stream stream);
}

/// <summary>An engine offered in the "which engine?" window.</summary>
public sealed record EngineChoice(string Id, string Title, string Description);

/// <summary>Provider ids used as keys in AppSettings.ApiKeys.</summary>
public static class ProviderIds
{
    public const string OpenAI = "openai";
}

/// <summary>A provider refused the request because of a quota or rate limit.</summary>
public sealed class ServiceQuotaException : Exception
{
    public ServiceQuotaException(string provider, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        RetryAfter = retryAfter;
    }

    public string Provider { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>Plain-language text suitable for the status bar.</summary>
    public string UserMessage => RetryAfter is { } wait
        ? $"{Provider}: quota or rate limit reached. Try again in about {Math.Ceiling(wait.TotalMinutes)} min. ({Message})"
        : $"{Provider}: quota or rate limit reached. ({Message})";
}
