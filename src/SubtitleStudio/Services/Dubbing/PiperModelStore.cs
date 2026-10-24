using System.Net.Http;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Downloads and holds Piper voices, each a `.onnx` (the model) plus a small `.onnx.json` (the config), under a
/// `piper` folder beside the Kokoro model. The big `.onnx` is downloaded from Hugging Face and checked against the
/// repo's own LFS checksum (no hashes baked into the app — the app reads the expected size and SHA-256 from the
/// file's LFS pointer, exactly as the translation models do). The small config is fetched directly. Voices are
/// downloaded on demand — only the ones a cast actually uses — not all at once.
/// </summary>
public sealed class PiperModelStore
{
    public const string PartialSuffix = ".download";

    public PiperModelStore(string folder) => Folder = folder;

    public string Folder { get; }

    public string ModelPath(PiperVoiceInfo v) => Path.Combine(Folder, v.ModelFileName);
    public string ConfigPath(PiperVoiceInfo v) => Path.Combine(Folder, v.ConfigFileName);

    /// <summary>Both files present (the model's size is checked by the download; here we only confirm it's there and non-empty).</summary>
    public bool Has(PiperVoiceInfo v)
    {
        var m = new FileInfo(ModelPath(v));
        var c = new FileInfo(ConfigPath(v));
        return m.Exists && m.Length > 0 && c.Exists && c.Length > 0;
    }

    /// <summary>
    /// Downloads a voice if it isn't already present: the config first (small, direct), then the model (verified
    /// against its Hugging Face LFS pointer). Interrupted model downloads continue from what arrived.
    /// </summary>
    public async Task DownloadAsync(PiperVoiceInfo v, HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);

        // Config: a few KB, not an LFS file, so fetch it straight (no checksum to verify against).
        if (!File.Exists(ConfigPath(v)) || new FileInfo(ConfigPath(v)).Length == 0)
        {
            var json = await http.GetStringAsync(v.ConfigUrl, ct).ConfigureAwait(false);
            PiperConfig.Parse(json); // throws early if the config is malformed, before we fetch the big model
            await File.WriteAllTextAsync(ConfigPath(v), json, ct).ConfigureAwait(false);
        }

        // Model: the large .onnx, verified against the repo's LFS pointer (size + SHA-256 read from Hugging Face).
        if (!Has(v))
        {
            var partial = await HfModelDownload.FetchVerifiedAsync(v.ModelUrl, v.ModelPointerUrl, v.ModelFileName,
                ModelPath(v) + PartialSuffix, http, progress, ct).ConfigureAwait(false);
            File.Move(partial, ModelPath(v), overwrite: true);
        }
    }

    /// <summary>Loads a downloaded voice into a PiperModel (on the graphics card when one is given, else the processor).</summary>
    public PiperModel Load(PiperVoiceInfo v, GpuAdapter? adapter, Action<string>? log = null)
    {
        if (!Has(v)) throw new InvalidOperationException($"The Piper voice {v.DisplayName} isn't downloaded yet.");
        var config = PiperConfig.Parse(File.ReadAllText(ConfigPath(v)));
        return PiperModel.Load(ModelPath(v), config, adapter, log);
    }
}
