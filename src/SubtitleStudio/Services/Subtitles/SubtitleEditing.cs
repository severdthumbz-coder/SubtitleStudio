using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Subtitles;

public enum CueIssueSeverity
{
    None,
    Warning,
    Error,
}

public sealed record CueIssue(CueIssueSeverity Severity, string Message);

/// <summary>Checks cues before saving. Errors break players; warnings are worth a look.</summary>
public static class SubtitleValidator
{
    /// <param name="checkOverlaps">
    /// False for ASS/SSA, where overlapping lines (signs, multiple layers) are normal.
    /// </param>
    /// <param name="readingLimit">Characters a second above which a line is too fast to read (null: not checked).</param>
    /// <param name="language">The subtitles' language, for counting characters (Japanese half-width ones count half).</param>
    public static IReadOnlyDictionary<SubtitleCue, CueIssue> Validate(IReadOnlyList<SubtitleCue> cues, bool checkOverlaps, double? readingLimit = null, string? language = null)
    {
        var issues = new Dictionary<SubtitleCue, CueIssue>();
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var messages = new List<string>();
            var severity = CueIssueSeverity.None;

            void Add(CueIssueSeverity s, string m)
            {
                messages.Add(m);
                if (s > severity) severity = s;
            }

            if (cue.Start < TimeSpan.Zero) Add(CueIssueSeverity.Error, "Starts before 0:00.");
            if (cue.End < cue.Start) Add(CueIssueSeverity.Error, "Ends before it starts (negative duration).");
            else if (cue.End == cue.Start) Add(CueIssueSeverity.Error, "Zero duration: it will never be shown.");

            if (string.IsNullOrWhiteSpace(SubtitleText.StripTags(cue.Text)))
                Add(CueIssueSeverity.Warning, "No text.");

            if (i > 0 && cue.Start < cues[i - 1].Start)
                Add(CueIssueSeverity.Warning, $"Starts before cue #{i} (out of order). Use Sort by time.");

            if (checkOverlaps && i + 1 < cues.Count && cue.End > cues[i + 1].Start && cues[i + 1].Start >= cue.Start)
                Add(CueIssueSeverity.Warning, $"Overlaps the next cue (#{i + 2}) by {(cue.End - cues[i + 1].Start).TotalMilliseconds:0} ms.");

            if (readingLimit is { } limit && cue.End > cue.Start && ReadingSpeed.Cps(cue, language) is var cps && cps > limit)
                Add(CueIssueSeverity.Warning, $"Too fast to read: {cps:0} characters a second (limit {limit:0.#}). Lengthen it, shorten the text, or use Fix reading speed.");

            if (messages.Count > 0) issues[cue] = new CueIssue(severity, string.Join(" ", messages));
        }
        return issues;
    }
}

/// <summary>Structural cue edits. Pure functions on the model; the view model only orchestrates.</summary>
public static class CueOperations
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(100);

    public static void Renumber(IList<SubtitleCue> cues)
    {
        for (int i = 0; i < cues.Count; i++) cues[i].Index = i + 1;
    }

    /// <summary>New empty cue placed after <paramref name="previous"/> and, if possible, before <paramref name="next"/>.</summary>
    public static SubtitleCue CreateAfter(SubtitleCue? previous, SubtitleCue? next)
    {
        var start = previous is null ? TimeSpan.Zero : previous.End + Gap;
        var end = start + DefaultDuration;
        if (next is not null && end > next.Start - Gap && next.Start - Gap > start + TimeSpan.FromMilliseconds(300))
            end = next.Start - Gap;
        return new SubtitleCue { Start = start, End = end, Text = string.Empty, Extra = CopyStyleExtra(previous) };
    }

    /// <summary>
    /// Splits at the time midpoint. Text is split between lines (or at the space nearest the middle
    /// of a single line); if it can't be split, the first half keeps all the text.
    /// </summary>
    public static (SubtitleCue First, SubtitleCue Second) Split(SubtitleCue cue)
    {
        var mid = cue.Start + TimeSpan.FromTicks((cue.End - cue.Start).Ticks / 2);
        var (a, b) = SplitText(cue.Text);
        var first = cue.Clone();
        first.End = mid;
        first.Text = a;
        var second = cue.Clone();
        second.Start = mid;
        second.Text = b;
        second.Extra?.Remove("id");
        return (first, second);
    }

    public static SubtitleCue Merge(SubtitleCue first, SubtitleCue second)
    {
        var merged = first.Clone();
        merged.Start = first.Start < second.Start ? first.Start : second.Start;
        merged.End = first.End > second.End ? first.End : second.End;
        merged.Text = string.Join("\n", new[] { first.Text.Trim(), second.Text.Trim() }.Where(t => t.Length > 0));
        return merged;
    }

    public static List<SubtitleCue> SortedByTime(IEnumerable<SubtitleCue> cues)
        => cues.Select((c, i) => (c, i)).OrderBy(x => x.c.Start).ThenBy(x => x.i).Select(x => x.c).ToList();

    internal static (string, string) SplitText(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length > 1)
        {
            int half = (lines.Length + 1) / 2;
            return (string.Join("\n", lines.Take(half)), string.Join("\n", lines.Skip(half)));
        }

        var t = text.Trim();
        int middle = t.Length / 2;
        int best = -1;
        for (int d = 0; d <= middle; d++)
        {
            if (middle - d >= 0 && t[middle - d] == ' ') { best = middle - d; break; }
            if (middle + d < t.Length && t[middle + d] == ' ') { best = middle + d; break; }
        }
        return best < 0 ? (t, string.Empty) : (t[..best].TrimEnd(), t[(best + 1)..].TrimStart());
    }

    private static Dictionary<string, string>? CopyStyleExtra(SubtitleCue? source)
    {
        if (source?.Extra is null) return null;
        // Keep ASS styling columns for new lines; drop identity-like data (VTT ids, comment marker).
        var copy = new Dictionary<string, string>(source.Extra, StringComparer.OrdinalIgnoreCase);
        copy.Remove("_kind");
        copy.Remove("id");
        return copy;
    }
}
