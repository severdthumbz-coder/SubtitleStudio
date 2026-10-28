using System.IO.Compression;
using System.Net.Http;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Holds the voice-cloning sidecar: a prebuilt, no-install <c>index-tts.exe</c> bundle (the IndexTTS-2.5 CLI frozen
/// with its Python runtime and ONNX Runtime) under a <c>cloning</c> folder beside the other models. The app drives
/// this exe like it drives FFmpeg — it does not reimplement IndexTTS in C#.
///
/// The exe bundle is downloaded on demand from a GitHub release the project controls (so no Python install for the
/// user), the same download pattern as the Kokoro/whisper models. The ~11.7 GB IndexTTS *model* is NOT downloaded
/// here: the exe fetches it itself (via huggingface_hub) on its first run, into the user's HF cache — so this store
/// only manages the exe. Cloning is personal-use (IndexTTS's licence); it is an opt-in engine, never the default.
///
/// NOTE: the exe must be built once on Windows (PyInstaller) and uploaded to the release — Claude can't build a
/// Windows exe. Until <see cref="BundleUrl"/> points at a real release asset, <see cref="HasExe"/> is false and the
/// engine simply reports it isn't available yet.
/// </summary>
public sealed class CloningSidecarStore
{
    public const string PartialSuffix = ".download";

    /// <summary>The release zip holding index-tts.exe and its support files. Set once the bundle is uploaded.</summary>
    public static readonly string? BundleUrl = "https://github.com/severdthumbz-coder/SubtitleStudio/releases/download/cloning-v1/index-tts-win-x64.zip";

    /// <summary>Expected name of the executable inside the extracted bundle.</summary>
    public const string ExeName = "index-tts.exe";

    public CloningSidecarStore(string folder) => Folder = folder;

    public string Folder { get; }

    public string ExePath => Path.Combine(Folder, ExeName);

    /// <summary>The sidecar exe is present (downloaded and extracted).</summary>
    public bool HasExe => File.Exists(ExePath) && new FileInfo(ExePath).Length > 0;

    /// <summary>Whether a bundle URL is configured at all (false until the exe is built and the release made).</summary>
    public static bool BundleAvailable => !string.IsNullOrEmpty(BundleUrl);

    /// <summary>
    /// Downloads and extracts the exe bundle if it isn't already present. The bundle is a zip on our own GitHub
    /// release, so it's fetched directly over HTTPS (no LFS pointer); the whole zip is written to a temp file, then
    /// extracted into the cloning folder. Interrupted downloads restart (the exe is one artifact, not resumable here).
    /// </summary>
    public async Task DownloadAsync(HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (!BundleAvailable)
            throw new InvalidOperationException("The voice-cloning download isn't set up in this version of the app yet.");
        if (HasExe) return;
        Directory.CreateDirectory(Folder);

        var zipPath = Path.Combine(Folder, "bundle.zip" + PartialSuffix);
        try
        {
            using (var resp = await http.GetAsync(BundleUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total is { } t && t > 0)
                        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, t));
                }
            }
            ZipFile.ExtractToDirectory(zipPath, Folder, overwriteFiles: true);
            if (!HasExe)
                throw new InvalidOperationException($"The cloning bundle didn't contain {ExeName}.");
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
