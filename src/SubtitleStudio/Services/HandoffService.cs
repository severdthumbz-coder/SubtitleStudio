namespace SubtitleStudio.Services;

/// <summary>
/// Command-line hand-off contract (used by Video Metadata Editor's "external subtitle app" launch):
///
///     SubtitleStudio.exe "D:\Movies\Film (2024)\Film (2024).mkv" ["more paths" ...]
///
/// Every argument that is an existing file or folder is imported (folders honour the
/// "Include subfolders" setting). Arguments starting with "-" are reserved for future switches.
/// If Subtitle Studio is already running, the paths are forwarded to that window instead of
/// starting a second copy (see SingleInstanceService).
/// </summary>
public static class HandoffService
{
    public static IReadOnlyList<string> ParseArgs(IEnumerable<string>? args, string? baseDirectory = null)
    {
        var result = new List<string>();
        if (args is null) return result;

        var baseDir = baseDirectory ?? Environment.CurrentDirectory;
        var candidates = args
            .Select(a => (a ?? string.Empty).Trim().Trim('"').Trim())
            .Where(a => a.Length > 0 && !a.StartsWith('-'))
            .ToList();

        foreach (var candidate in candidates)
        {
            if (TryResolve(candidate, baseDir, out var full) && !result.Contains(full, StringComparer.OrdinalIgnoreCase))
                result.Add(full);
        }

        // Robustness for callers that forget to quote a path containing spaces: Windows then
        // splits it into several args, none of which exist on their own. Try the rejoined string.
        if (result.Count == 0 && candidates.Count > 1)
        {
            var joined = string.Join(' ', candidates);
            if (TryResolve(joined, baseDir, out var full))
                result.Add(full);
        }

        return result;
    }

    private static bool TryResolve(string candidate, string baseDir, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(baseDir, candidate));
            if (File.Exists(full) || Directory.Exists(full))
            {
                fullPath = full;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            // Not a usable path; ignore.
        }
        return false;
    }
}
