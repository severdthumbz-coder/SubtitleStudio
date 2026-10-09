using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>A Kokoro model the app can download, with the size and SHA-256 it is checked against.</summary>
public sealed record TtsCatalogEntry(string Id, string Title, string FileName, long Size, string Sha256, string Description)
{
    public string SizeText => $"{Size / (1024.0 * 1024):0} MB";

    public string Label => $"{Title}  ·  {SizeText}";

    public string DownloadUrl => TtsModelStore.ReleaseUrl + FileName;

    public override string ToString() => Label;
}

/// <summary>
/// The dubbing voice model in models\tts next to the app: Kokoro v1.0 (Apache-2.0) and its voices file,
/// downloaded once from the kokoro-onnx project's release on GitHub (the ONNX export of hexgrad's
/// Kokoro-82M) and checked against sizes and SHA-256 written into the app.
/// </summary>
public sealed class TtsModelStore
{
    public const string PartialSuffix = ".download";
    public const string ReleaseUrl = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/";

    /// <summary>
    /// The full model. (The kokoro-onnx release also has an int8 one: with the ONNX Runtime in the app it
    /// is three times slower on the processor and its speech comes out broken, so it isn't offered.)
    /// </summary>
    public static TtsCatalogEntry Model { get; } = new("full", "Kokoro v1.0", "kokoro-v1.0.onnx", 325_532_387,
        "7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5",
        "The voice model (82 million parameters, Apache-2.0). Runs on the graphics card with DirectML, or on the processor.");

    public static TtsCatalogEntry VoicesFile { get; } = new("voices", "Kokoro voices", "voices-v1.0.bin", 28_214_398,
        "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d",
        "The voices (downloaded with the model).");

    public TtsModelStore(string folder) => Folder = folder;

    public string Folder { get; }

    public string PathFor(TtsCatalogEntry entry) => Path.Combine(Folder, entry.FileName);

    /// <summary>Complete files only (a file of the wrong size is an interrupted copy).</summary>
    public bool Has(TtsCatalogEntry entry)
    {
        var info = new FileInfo(PathFor(entry));
        return info.Exists && info.Length == entry.Size;
    }

    public string ModelPath => PathFor(Model);

    public string VoicesPath => PathFor(VoicesFile);

    /// <summary>Ready to speak: the model and the voices.</summary>
    public bool IsReady => Has(Model) && Has(VoicesFile);

    /// <summary>Bytes still to download (less what already arrived of an interrupted download).</summary>
    public long BytesToFetch => Needed().Sum(e => e.Size - Partial(e));

    private IEnumerable<TtsCatalogEntry> Needed()
    {
        if (!Has(VoicesFile)) yield return VoicesFile;
        if (!Has(Model)) yield return Model;
    }

    private long Partial(TtsCatalogEntry e)
    {
        var info = new FileInfo(PathFor(e) + PartialSuffix);
        return info.Exists ? Math.Min(info.Length, e.Size) : 0;
    }

    /// <summary>Downloads the voices and the model (what's missing), each checked; interrupted downloads continue.</summary>
    public async Task DownloadAsync(HttpClient http, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var needed = Needed().ToList();
        long total = needed.Sum(e => e.Size), before = 0;
        foreach (var e in needed)
        {
            long offset = before;
            var inner = progress is null ? null : new SyncProgress<ModelDownloadProgress>(p =>
                progress.Report(p with { Done = offset + p.Done, Total = total }));
            var partial = await HfModelDownload.FetchKnownAsync(e.DownloadUrl, e.Size, e.Sha256, PathFor(e) + PartialSuffix, http, inner, ct, "the kokoro-onnx release").ConfigureAwait(false);
            File.Move(partial, PathFor(e), overwrite: true);
            before += e.Size;
        }
    }

    /// <summary>Reports on the caller's thread (Progress&lt;T&gt; would post, and the outer one posts already).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
