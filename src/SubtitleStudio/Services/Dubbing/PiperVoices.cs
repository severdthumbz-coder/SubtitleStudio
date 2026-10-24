namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// The Piper voices the app offers, and where to download them. Piper's official voices live in the Hugging Face
/// repo rhasspy/piper-voices as a `.onnx` (the model, an LFS file) plus a small `.onnx.json` (the config: the
/// phoneme→id map, the inference scales, the sample rate). The app downloads them on demand like the Kokoro model,
/// verifying the big `.onnx` against Hugging Face's LFS checksum (no hashes baked into the app).
///
/// A curated set to start (per the integration plan): a few voices verified to sound right when driven from the
/// app's own phonemizer (PiperPhonemes), not Piper's whole library. English, both genders, medium/high quality.
/// </summary>
public sealed record PiperVoiceInfo(string Id, string DisplayName, string Gender, string RelativePath)
{
    private const string Repo = "rhasspy/piper-voices";

    /// <summary>The model file on Hugging Face (LFS): downloaded and checked against the repo's LFS pointer.</summary>
    public string ModelUrl => $"https://huggingface.co/{Repo}/resolve/main/{RelativePath}.onnx";

    /// <summary>The LFS pointer for the model, giving its size and SHA-256 so the download can be verified.</summary>
    public string ModelPointerUrl => $"https://huggingface.co/{Repo}/raw/main/{RelativePath}.onnx";

    /// <summary>The config file (small, not LFS): fetched directly, no checksum needed.</summary>
    public string ConfigUrl => $"https://huggingface.co/{Repo}/resolve/main/{RelativePath}.onnx.json";

    /// <summary>"en_US-ryan-medium.onnx" — the local file name under the piper folder.</summary>
    public string ModelFileName => RelativePath[(RelativePath.LastIndexOf('/') + 1)..] + ".onnx";
    public string ConfigFileName => ModelFileName + ".json";
}

public static class PiperVoices
{
    /// <summary>The curated English voices the app offers through Piper.</summary>
    public static readonly IReadOnlyList<PiperVoiceInfo> English = new[]
    {
        new PiperVoiceInfo("piper-ryan", "Ryan (Piper)", "male", "en/en_US/ryan/medium/en_US-ryan-medium"),
        new PiperVoiceInfo("piper-lessac", "Lessac (Piper)", "female", "en/en_US/lessac/medium/en_US-lessac-medium"),
        new PiperVoiceInfo("piper-amy", "Amy (Piper)", "female", "en/en_US/amy/medium/en_US-amy-medium"),
        new PiperVoiceInfo("piper-joe", "Joe (Piper)", "male", "en/en_US/joe/medium/en_US-joe-medium"),
        new PiperVoiceInfo("piper-alan", "Alan (Piper, British)", "male", "en/en_GB/alan/medium/en_GB-alan-medium"),
        new PiperVoiceInfo("piper-jenny", "Jenny (Piper, British)", "female", "en/en_GB/jenny_dioco/medium/en_GB-jenny_dioco-medium"),
    };

    public static PiperVoiceInfo? Find(string id) => English.FirstOrDefault(v => v.Id == id);
}
