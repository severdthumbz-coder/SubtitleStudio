using SubtitleStudio.Models;

namespace SubtitleStudio.Services;

public sealed record ScannedFile(string Path, MediaKind Kind, long SizeBytes, IReadOnlyList<SidecarInfo> Sidecars);

public sealed record ImportScanResult(IReadOnlyList<ScannedFile> Files, int UnsupportedCount, int MissingCount);

/// <summary>Expands dropped / picked / handed-off paths into supported media files. Runs off the UI thread.</summary>
public sealed class FileImportService
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".ts", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".flv", ".3gp", ".vob", ".ogv",
    };

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".ac3", ".eac3",
    };

    /// <summary>Recognised for import/detection. idx/sup (image-based) are listed but not editable.</summary>
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".vtt", ".ass", ".ssa", ".sub", ".idx", ".sup",
    };

    public static MediaKind? Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        if (SubtitleExtensions.Contains(ext)) return MediaKind.Subtitle;
        return null;
    }

    public static string OpenFileFilter
    {
        get
        {
            static string Patterns(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
            var all = VideoExtensions.Concat(AudioExtensions).Concat(SubtitleExtensions);
            return $"Media and subtitles|{Patterns(all)}"
                 + $"|Video|{Patterns(VideoExtensions)}"
                 + $"|Audio|{Patterns(AudioExtensions)}"
                 + $"|Subtitles|{Patterns(SubtitleExtensions)}"
                 + "|All files|*.*";
        }
    }

    /// <param name="progress">Receives the running count of files checked (about every 250 files).</param>
    public Task<ImportScanResult> ScanAsync(IEnumerable<string> paths, bool recursive, IProgress<int>? progress, CancellationToken ct)
        => Task.Run(() => Scan(paths.ToList(), recursive, progress, ct), ct);

    private static ImportScanResult Scan(IReadOnlyList<string> inputs, bool recursive, IProgress<int>? progress, CancellationToken ct)
    {
        var files = new List<string>();
        int missing = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(input))
                files.Add(Path.GetFullPath(input));
            else if (Directory.Exists(input))
            {
                foreach (var file in Directory.EnumerateFiles(input, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file);
                    if (files.Count % 250 == 0) progress?.Report(files.Count);
                }
            }
            else
                missing++;
        }

        var results = new List<ScannedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var siblingsCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        int unsupported = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(file)) continue;

            var kind = Classify(file);
            if (kind is null)
            {
                unsupported++;
                continue;
            }

            long size = 0;
            try { size = new FileInfo(file).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            IReadOnlyList<SidecarInfo> sidecars = Array.Empty<SidecarInfo>();
            if (kind == MediaKind.Video)
            {
                var dir = Path.GetDirectoryName(file) ?? string.Empty;
                if (!siblingsCache.TryGetValue(dir, out var siblings))
                {
                    try { siblings = Directory.GetFiles(dir); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { siblings = Array.Empty<string>(); }
                    siblingsCache[dir] = siblings;
                }
                sidecars = SidecarDetector.Detect(file, siblings);
            }

            results.Add(new ScannedFile(file, kind.Value, size, sidecars));
        }

        return new ImportScanResult(results, unsupported, missing);
    }
}
