using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SubtitleStudio.Services.Transcription;

/// <summary>
/// Downloads a model file from Hugging Face and checks it against the exact size and SHA-256 published
/// there (the file's Git LFS pointer), so a damaged or swapped file is never used. What arrived is kept
/// in a ".download" file and an interrupted download continues from there. Shared by Whisper and
/// translation models.
/// </summary>
public static class HfModelDownload
{
    /// <summary>Bytes already downloaded into <paramref name="partial"/> (0 when none).</summary>
    public static long PartialBytes(string partial) => File.Exists(partial) ? new FileInfo(partial).Length : 0;

    /// <summary>Downloads into <paramref name="partial"/> and returns it once complete and verified (the caller moves it into place).</summary>
    public static async Task<string> FetchVerifiedAsync(string downloadUrl, string pointerUrl, string fileName, string partial,
        HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        LfsPointer pointer;
        try
        {
            var text = await http.GetStringAsync(pointerUrl, ct).ConfigureAwait(false);
            pointer = LfsPointer.TryParse(text)
                      ?? throw new InvalidOperationException("Hugging Face didn't return the model's size and checksum, so the download can't be checked. Try again later.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach Hugging Face ({ex.Message}). Check the internet connection, or download {fileName} in a browser and use \"Use a model file\".", ex);
        }

        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > pointer.Size) { File.Delete(partial); have = 0; }

        try
        {
            if (have < pointer.Size)
                await FetchAsync(http, downloadUrl, partial, have, pointer.Size, progress, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"The download stopped ({ex.Message}). What arrived is kept: press Download again to continue where it stopped.", ex);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"The download stopped ({ex.Message}). What arrived is kept: press Download again to continue where it stopped.", ex);
        }

        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Checking, 0, pointer.Size));
        string hash;
        await using (var stream = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            hash = await HashAsync(stream, pointer.Size, progress, ct).ConfigureAwait(false);
        if (!string.Equals(hash, pointer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidOperationException("The downloaded model is damaged (its SHA-256 doesn't match the one Hugging Face publishes). It was deleted; press Download to try again.");
        }

        return partial;
    }

    private static async Task FetchAsync(HttpClient http, string url, string partial, long have, long size, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server can't continue from there: start over.
            File.Delete(partial);
            await FetchAsync(http, url, partial, 0, size, progress, ct).ConfigureAwait(false);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The model download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");

        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0; // the server sent the whole file

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        long done = have, lastReport = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (done - lastReport >= 4 << 20 || done >= size)
            {
                lastReport = done;
                progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, size));
            }
        }
        if (done != size)
            throw new IOException($"the connection closed after {done:N0} of {size:N0} bytes");
    }

    private static async Task<string> HashAsync(Stream stream, long total, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0, lastReport = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            if (done - lastReport >= 32 << 20)
            {
                lastReport = done;
                progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Checking, done, total));
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
