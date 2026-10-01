using System.Net.Http;
using System.Security.Cryptography;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// The AI fill model file: LaMa (Apache-2.0) as published in the OpenCV model zoo, about 90 MB.
/// Kept in a "models" folder next to the EXE (portable, like the settings). Downloaded by the app
/// itself on request and checked against its SHA-256, or copied from a file the user already has.
/// </summary>
public sealed class InpaintModelStore
{
    public const string FileName = "inpainting_lama_2025jan.onnx";
    public const long ExpectedSize = 92_591_623;
    public const string ExpectedSha256 = "7df918ac3921d3daf0aae1d219776cf0dc4e4935f035af81841b40adcf74fdf2";
    public const string DownloadUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/inpainting_lama/inpainting_lama_2025jan.onnx";
    public const string SourcePage = "https://github.com/opencv/opencv_zoo/tree/main/models/inpainting_lama";

    public InpaintModelStore(string folder) => Folder = folder;

    public string Folder { get; }
    public string ModelPath => Path.Combine(Folder, FileName);

    /// <summary>Present with the right size (the full hash is checked when it is downloaded or imported).</summary>
    public bool IsInstalled => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedSize;

    /// <summary>Downloads to a temporary file, checks size and SHA-256, then moves it into place.</summary>
    public async Task DownloadAsync(HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var tmp = ModelPath + ".download";
        try
        {
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"The model download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
                long total = response.Content.Headers.ContentLength ?? ExpectedSize;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(tmp);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    int percent = (int)(100 * Math.Min(1, done / (double)total));
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent / 100.0); }
                }
            }
            await VerifyAsync(tmp, ct).ConfigureAwait(false);
            File.Move(tmp, ModelPath, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The model could not be downloaded: {ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies a model file the user already has (checked the same way).</summary>
    public async Task ImportAsync(string sourcePath, CancellationToken ct)
    {
        await VerifyAsync(sourcePath, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Folder);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(ModelPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, ModelPath, overwrite: true);
    }

    public static async Task VerifyAsync(string path, CancellationToken ct)
    {
        var length = new FileInfo(path).Length;
        if (length != ExpectedSize)
            throw new InvalidOperationException($"This is not the expected model file ({length:N0} bytes, expected {ExpectedSize:N0}).");
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (hash != ExpectedSha256)
            throw new InvalidOperationException("The model file is damaged or a different version (its SHA-256 does not match).");
    }
}
