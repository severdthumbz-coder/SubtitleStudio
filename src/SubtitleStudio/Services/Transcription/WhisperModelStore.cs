using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SubtitleStudio.Services.Transcription;

/// <summary>A whisper.cpp model in the models folder.</summary>
public sealed record InstalledWhisperModel(string Path, long SizeBytes, WhisperModelHeader Header)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string SizeText => SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} GB" : $"{SizeBytes / (double)(1L << 20):0} MB";

    public string Label => $"{Header.Name}  ·  {SizeText}  ·  {FileName}";

    public override string ToString() => Label;
}

public enum ModelDownloadPhase { Downloading, Checking }

public readonly record struct ModelDownloadProgress(ModelDownloadPhase Phase, long Done, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : 0;
}

/// <summary>
/// Whisper models (whisper.cpp ggml files), kept in models\whisper next to the EXE like the AI fill
/// model. Downloaded by the app from whisper.cpp's page on Hugging Face and checked against the exact
/// size and SHA-256 published there (the Git LFS pointer), or copied from a file the user already has.
/// An interrupted download is resumed next time instead of starting over.
/// </summary>
public sealed class WhisperModelStore
{
    public const string PartialSuffix = ".download";

    public WhisperModelStore(string folder) => Folder = folder;

    public string Folder { get; }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Models in the folder whose header reads as Whisper, largest first.</summary>
    public IReadOnlyList<InstalledWhisperModel> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<InstalledWhisperModel>();
        var models = new List<InstalledWhisperModel>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.bin"))
        {
            try
            {
                models.Add(new InstalledWhisperModel(path, new FileInfo(path).Length, WhisperModelHeader.Read(path)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Not a Whisper model, or unreadable: not offered.
            }
        }
        return models.OrderByDescending(m => m.Header.AudioLayers).ThenByDescending(m => m.SizeBytes).ToList();
    }

    /// <summary>Bytes already downloaded of an interrupted download (0 when none).</summary>
    public long PartialBytes(WhisperCatalogEntry entry)
    {
        var partial = new FileInfo(PathFor(entry.FileName) + PartialSuffix);
        return partial.Exists ? partial.Length : 0;
    }

    public async Task<InstalledWhisperModel> DownloadAsync(WhisperCatalogEntry entry, HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var target = PathFor(entry.FileName);
        var partial = target + PartialSuffix;

        LfsPointer pointer;
        try
        {
            var text = await http.GetStringAsync(entry.PointerUrl, ct).ConfigureAwait(false);
            pointer = LfsPointer.TryParse(text)
                      ?? throw new InvalidOperationException("Hugging Face didn't return the model's size and checksum, so the download can't be checked. Try again later.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Couldn't reach Hugging Face ({ex.Message}). Check the internet connection, or download {entry.FileName} in a browser and use \"Use a model file\".", ex);
        }

        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > pointer.Size) { File.Delete(partial); have = 0; }

        try
        {
            if (have < pointer.Size)
                await FetchAsync(http, entry.DownloadUrl, partial, have, pointer.Size, progress, ct).ConfigureAwait(false);
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

        var header = WhisperModelHeader.Read(partial);
        File.Move(partial, target, overwrite: true);
        return new InstalledWhisperModel(target, pointer.Size, header);
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

    /// <summary>Checks a model file the user already has and copies it into the models folder.</summary>
    public async Task<InstalledWhisperModel> ImportAsync(string sourcePath, CancellationToken ct)
    {
        var header = WhisperModelHeader.Read(sourcePath);
        Directory.CreateDirectory(Folder);
        var target = PathFor(Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            var tmp = target + ".copy";
            try
            {
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    await input.CopyToAsync(output, 1 << 20, ct).ConfigureAwait(false);
                File.Move(tmp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return new InstalledWhisperModel(target, new FileInfo(target).Length, header);
    }

    public void Delete(InstalledWhisperModel model)
    {
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(model.Path)), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
            File.Delete(model.Path);
    }
}
