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
        var partial = await HfModelDownload.FetchVerifiedAsync(entry.DownloadUrl, entry.PointerUrl, entry.FileName, target + PartialSuffix, http, progress, ct).ConfigureAwait(false);
        WhisperModelHeader header;
        try
        {
            header = WhisperModelHeader.Read(partial);
        }
        catch (InvalidDataException)
        {
            File.Delete(partial);
            throw;
        }
        File.Move(partial, target, overwrite: true);
        return new InstalledWhisperModel(target, new FileInfo(target).Length, header);
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
