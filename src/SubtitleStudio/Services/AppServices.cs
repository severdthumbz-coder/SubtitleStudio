using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Audio;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Playback;
using SubtitleStudio.Services.Onboarding;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.Services;

/// <summary>
/// Composition root: one instance of every service, built once in App.OnStartup and handed to
/// the view model. No DI container needed at this size.
/// Engine lists start empty in build 1; concrete providers are registered here as they arrive.
/// </summary>
public sealed class AppServices
{
    public required AppSettings Config { get; init; }
    public required SettingsService Settings { get; init; }
    public required ApiKeyStore ApiKeys { get; init; }
    public required ThemeService Theme { get; init; }
    public required FileImportService Import { get; init; }
    public required FfmpegLocator Ffmpeg { get; init; }
    public required MediaProbeService Probe { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required IGuidedTourService Tour { get; init; }
    public required IPlaybackService Playback { get; init; }

    /// <summary>Makes an extra, independent player (the burned-in before/after view). Null: not available.</summary>
    public Func<IPlaybackService>? CreatePlayer { get; init; }

    /// <summary>The AI fill model file (models folder next to the EXE).</summary>
    public InpaintModelStore InpaintModels { get; init; } = new(Path.Combine(AppPaths.ExeDirectory, "models"));

    /// <summary>Whisper models for AI Transcribe (models\whisper next to the EXE).</summary>
    public Transcription.WhisperModelStore WhisperModels { get; init; } = new(Path.Combine(AppPaths.ExeDirectory, "models", "whisper"));

    /// <summary>Lists the Vulkan graphics devices (whisper.cpp's graphics card backend). Replaceable in tests.</summary>
    public Func<Transcription.VulkanReport> ListVulkanDevices { get; init; } = Transcription.VulkanDevices.List;

    /// <summary>Loads the AI fill model on the given graphics card (ONNX Runtime in the app). Null: AI fill unavailable.</summary>
    public Func<string, Gpu.GpuInfo?, int, IInpaintModel>? LoadInpaintModel { get; init; }

    /// <summary>Crash recovery: the working session (subt_session.json next to the app). Null: not saved.</summary>
    public SessionStore? Session { get; init; }
    public required BurnedInService BurnedIn { get; init; }
    public AudioEnvelopeService Audio { get; } = new();

    /// <summary>What the app did: the Log tab and subt_activity.log. In memory only unless given a file.</summary>
    public ActivityLog Log { get; init; } = new(null);

    public List<ITranscriptionService> TranscriptionEngines { get; } = new();
    public List<ITranslationService> TranslationEngines { get; } = new();
    public List<ITtsService> TtsEngines { get; } = new();
    public SubtitleFormatRegistry SubtitleFormats { get; } = new();
}
