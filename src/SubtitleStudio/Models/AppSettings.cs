namespace SubtitleStudio.Models;

/// <summary>
/// Everything persisted to subt_settings.json (next to the EXE).
/// API keys are stored ONLY as DPAPI-protected blobs in <see cref="ApiKeys"/>.
/// Add new properties with safe defaults; unknown/missing JSON members are tolerated.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Appearance
    public string Theme { get; set; } = "Dark";

    // Engines & tools
    public string FfmpegPath { get; set; } = string.Empty;

    // AI Transcribe (whisper.cpp in the app; models in models\whisper next to the EXE)
    /// <summary>File name of the chosen Whisper model.</summary>
    public string WhisperModel { get; set; } = string.Empty;
    /// <summary>"auto" (the graphics card, else the processor), "cpu", or a Vulkan device name.</summary>
    public string WhisperDevice { get; set; } = "auto";
    /// <summary>"auto" or a Whisper language code such as "ko".</summary>
    public string TranscribeLanguage { get; set; } = "auto";
    public bool TranscribeTranslate { get; set; }
    /// <summary>Remembered answer to "which engine?" when several can transcribe (engine id; empty: ask).</summary>
    public string TranscriptionEngine { get; set; } = string.Empty;

    // Burned-in removal
    /// <summary>Text left in the picture when burned-in subtitles are removed (one entry per item).</summary>
    public List<string> RemovalKeepList { get; set; } = new();

    // Translate (a language model with llama.cpp in the app; models in models\llm next to the EXE; runs on WhisperDevice)
    /// <summary>File name of the chosen language model (GGUF).</summary>
    public string TranslateModel { get; set; } = string.Empty;
    /// <summary>"auto" (the subtitle's own language tag) or a language code.</summary>
    public string TranslateSource { get; set; } = "auto";
    public string TranslateTarget { get; set; } = "en";
    /// <summary>Remembered answer to "which engine?" when several can translate (engine id; empty: ask).</summary>
    public string TranslationEngine { get; set; } = string.Empty;

    // Output defaults
    public string DefaultOutputFormat { get; set; } = "srt";
    public string DefaultLanguage { get; set; } = "en";

    // Behaviour
    public bool IncludeSubfolders { get; set; } = true;
    public bool ShowHints { get; set; } = true;
    /// <summary>Draw the current cue over the video preview (off shows the bare picture, e.g. burned-in text).</summary>
    public bool ShowSubtitleOverlay { get; set; } = true;
    public bool BringToFrontOnHandoff { get; set; } = true;

    /// <summary>Save the session while working and offer to restore it after a crash.</summary>
    public bool RestoreSessionAfterCrash { get; set; } = true;
    public bool ShowGuidedTourOnFirstRun { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    /// <summary>Provider id (lower-case, e.g. "openai") to "dpapi:&lt;base64&gt;".</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    public WindowPlacement? Window { get; set; }
}

public sealed class WindowPlacement
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
