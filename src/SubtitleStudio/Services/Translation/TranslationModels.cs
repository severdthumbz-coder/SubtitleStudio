using System.Net.Http;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>A language model the app offers for translation (a single GGUF file on Hugging Face).</summary>
public sealed record TranslationCatalogEntry(string Repository, string FileName, string Title, string SizeText, string Description, string License, bool Recommended = false)
{
    public string DownloadUrl => $"https://huggingface.co/{Repository}/resolve/main/{FileName}";
    public string PointerUrl => $"https://huggingface.co/{Repository}/raw/main/{FileName}";
    public string Label => $"{Title} ({SizeText})" + (Recommended ? " - recommended" : string.Empty);
}

public static class TranslationCatalog
{
    /// <summary>
    /// Chosen to fit a 12 GB graphics card with room to spare, and to load with the llama.cpp version
    /// built into the app (LLamaSharp 0.27.0, llama.cpp of April 2026).
    /// </summary>
    public static IReadOnlyList<TranslationCatalogEntry> Entries { get; } = new[]
    {
        new TranslationCatalogEntry("bartowski/google_gemma-3-12b-it-GGUF", "google_gemma-3-12b-it-Q4_K_M.gguf", "Gemma 3 12B", "7.3 GB",
            "Google's 12B model, trained on 140+ languages: natural, idiomatic dialogue, good with Korean and Japanese. Needs about 9 GB of graphics memory.",
            "Gemma Terms of Use", Recommended: true),
        new TranslationCatalogEntry("Qwen/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "Qwen3 8B", "5.0 GB",
            "Alibaba's 8B model: strong on Chinese, Korean and Japanese, a little faster and smaller than Gemma 3 12B. Needs about 6 GB of graphics memory.",
            "Apache 2.0"),
        new TranslationCatalogEntry("bartowski/google_gemma-3-4b-it-GGUF", "google_gemma-3-4b-it-Q4_K_M.gguf", "Gemma 3 4B", "2.5 GB",
            "Small and fast, rougher translations. For a quick first pass, or PCs without a graphics card.",
            "Gemma Terms of Use"),
    };
}

/// <summary>A language model in the models folder.</summary>
public sealed record InstalledTranslationModel(string Path, long SizeBytes, GgufInfo Info)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string SizeText => SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):0.0} GB" : $"{SizeBytes / (double)(1L << 20):0} MB";

    public string Title => TranslationCatalog.Entries.FirstOrDefault(e => e.FileName.Equals(FileName, StringComparison.OrdinalIgnoreCase))?.Title ?? Info.Title;

    public string Label => $"{Title}  ·  {SizeText}  ·  {FileName}";

    public override string ToString() => Label;
}

/// <summary>
/// Translation models (GGUF), kept in models\llm next to the EXE. Downloaded from Hugging Face and checked
/// against the published size and SHA-256 (resumable), or copied from a file the user already has.
/// </summary>
public sealed class TranslationModelStore
{
    public const string PartialSuffix = ".download";

    public TranslationModelStore(string folder) => Folder = folder;

    public string Folder { get; }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    public long PartialBytes(TranslationCatalogEntry entry) => HfModelDownload.PartialBytes(PathFor(entry.FileName) + PartialSuffix);

    public IReadOnlyList<InstalledTranslationModel> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<InstalledTranslationModel>();
        var models = new List<InstalledTranslationModel>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.gguf"))
        {
            try
            {
                var info = GgufInfo.Read(path);
                if (info.HasChatTemplate) models.Add(new InstalledTranslationModel(path, new FileInfo(path).Length, info));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return models.OrderByDescending(m => TranslationCatalog.Entries.Any(e => e.Recommended && e.FileName == m.FileName)).ThenByDescending(m => m.SizeBytes).ToList();
    }

    public async Task<InstalledTranslationModel> DownloadAsync(TranslationCatalogEntry entry, HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var target = PathFor(entry.FileName);
        var partial = await HfModelDownload.FetchVerifiedAsync(entry.DownloadUrl, entry.PointerUrl, entry.FileName, target + PartialSuffix, http, progress, ct).ConfigureAwait(false);
        GgufInfo info;
        try
        {
            info = Check(partial);
        }
        catch (InvalidDataException)
        {
            File.Delete(partial);
            throw;
        }
        File.Move(partial, target, overwrite: true);
        return new InstalledTranslationModel(target, new FileInfo(target).Length, info);
    }

    public async Task<InstalledTranslationModel> ImportAsync(string sourcePath, CancellationToken ct)
    {
        var info = Check(sourcePath);
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
        return new InstalledTranslationModel(target, new FileInfo(target).Length, info);
    }

    /// <summary>A GGUF language model with a chat template (needed to give it instructions).</summary>
    public static GgufInfo Check(string path)
    {
        var info = GgufInfo.Read(path);
        if (!info.HasChatTemplate)
            throw new InvalidDataException($"This model ({info.Title}) has no chat template, so it can't follow translation instructions. Use an \"instruct\" or \"it\" model.");
        return info;
    }
}
