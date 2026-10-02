using System.Globalization;
using System.Text;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// Text the user wants left in the picture when burned-in subtitles are removed (a title card, a sign, a
/// translator's note...). An entry matches a line read by OCR when the line contains it, ignoring case,
/// spaces and punctuation, with a few OCR misreadings allowed for longer entries.
/// </summary>
public sealed class KeepList
{
    private readonly List<(string Entry, string Key)> _entries;

    public KeepList(IEnumerable<string> entries)
    {
        _entries = entries.Select(e => (e.Trim(), Normalize(e))).Where(e => e.Item2.Length > 0)
            .GroupBy(e => e.Item2).Select(g => g.First()).ToList();
    }

    /// <summary>One entry per line (blank lines ignored).</summary>
    public static KeepList Parse(string? text)
        => new((text ?? string.Empty).Replace("\r", string.Empty).Split('\n'));

    public IReadOnlyList<string> Entries => _entries.Select(e => e.Entry).ToList();

    public int Count => _entries.Count;

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The entry this OCR line matches, or null.</summary>
    public string? Match(string? ocrText)
    {
        var line = Normalize(ocrText);
        if (line.Length == 0) return null;
        foreach (var (entry, key) in _entries)
            if (Contains(line, key, AllowedErrors(key.Length))) return entry;
        return null;
    }

    /// <summary>OCR misreads a letter now and then: one per 6 letters is forgiven, none for short entries.</summary>
    public static int AllowedErrors(int length) => length < 6 ? 0 : length / 6;

    /// <summary>Lower case, letters and digits only (OCR spacing and punctuation vary from frame to frame).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="pattern"/> occurs in <paramref name="text"/> with at most
    /// <paramref name="errors"/> letters different, missing or extra (approximate substring match).
    /// </summary>
    public static bool Contains(string text, string pattern, int errors)
    {
        if (pattern.Length == 0) return true;
        if (errors == 0) return text.Contains(pattern, StringComparison.Ordinal);
        if (pattern.Length - errors > text.Length) return false;
        // Sellers' algorithm: edit distance of the pattern against the best substring of the text.
        var previous = new int[pattern.Length + 1];
        var current = new int[pattern.Length + 1];
        for (int i = 0; i <= pattern.Length; i++) previous[i] = i;
        if (previous[pattern.Length] <= errors) return true;
        foreach (var c in text)
        {
            current[0] = 0;
            for (int i = 1; i <= pattern.Length; i++)
            {
                int cost = pattern[i - 1] == c ? 0 : 1;
                current[i] = Math.Min(Math.Min(previous[i] + 1, current[i - 1] + 1), previous[i - 1] + cost);
            }
            if (current[pattern.Length] <= errors) return true;
            (previous, current) = (current, previous);
        }
        return false;
    }
}
