using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Subtitles;

/// <summary>What fixing the reading speed did.</summary>
public sealed record ReadingSpeedFix(int Fixed, int Improved, int NoRoom);

/// <summary>
/// How fast a subtitle has to be read, in characters per second, and the usual limit for the language.
/// Limits are the adult figures of Netflix's Timed Text Style Guides: English 20, Korean 12, Chinese 9,
/// Japanese 4 (full-width characters; half-width ones count half). Other languages: 17, a common
/// subtitling default. Spaces count; line breaks and tags don't.
/// </summary>
public static class ReadingSpeed
{
    public const double OtherLanguages = 17;

    /// <summary>Netflix's longest subtitle: a line is never stretched past this to slow it down.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(7);

    /// <summary>Gap kept before the next subtitle when lengthening one (about two frames).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(84);

    public static double LimitFor(string? language) => (language ?? string.Empty).Split('-', '_')[0].ToLowerInvariant() switch
    {
        "en" => 20,
        "ko" => 12,
        "zh" or "yue" => 9,
        "ja" => 4,
        _ => OtherLanguages,
    };

    /// <summary>Characters to read: tags and line breaks left out; for Japanese, half-width characters count half.</summary>
    public static double Characters(string text, string? language)
    {
        var plain = SubtitleText.StripTags(text ?? string.Empty).Replace("\r", string.Empty);
        bool japanese = (language ?? string.Empty).StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        double count = 0;
        var lines = plain.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        foreach (var line in lines)
            foreach (var c in line)
                count += japanese && IsHalfWidth(c) ? 0.5 : 1;
        return count;
    }

    private static bool IsHalfWidth(char c) => c < 'ᄀ' || c is >= '｡' and <= 'ￜ' || c is >= '￨' and <= '￮';

    /// <summary>Characters per second (0 for a cue with no duration: that's reported as an error elsewhere).</summary>
    public static double Cps(SubtitleCue cue, string? language)
    {
        var seconds = (cue.End - cue.Start).TotalSeconds;
        return seconds <= 0 ? 0 : Characters(cue.Text, language) / seconds;
    }

    /// <summary>The duration the text needs at the limit.</summary>
    public static TimeSpan Needed(SubtitleCue cue, string? language, double limit)
        => TimeSpan.FromSeconds(Characters(cue.Text, language) / Math.Max(0.1, limit));

    /// <summary>
    /// Gives the cues that are too fast the time they need, from the free time around them: the end moves
    /// later (up to the next subtitle, less a small gap), then the start earlier (down to the previous one).
    /// Never past 7 seconds in all. Cues must be in time order. Returns how many now read in time, how many
    /// are slower but still too fast, and how many had no room at all.
    /// </summary>
    public static ReadingSpeedFix Fix(IReadOnlyList<SubtitleCue> cues, string? language, double limit)
    {
        int fixedCount = 0, improved = 0, noRoom = 0;
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (cue.End <= cue.Start || Cps(cue, language) <= limit) continue;
            var target = Needed(cue, language, limit);
            if (target > MaxDuration) target = MaxDuration;
            var before = cue.End - cue.Start;

            var latestEnd = i + 1 < cues.Count ? cues[i + 1].Start - Gap : cue.Start + target;
            var end = Max(cue.End, Min(cue.Start + target, latestEnd));
            var earliestStart = i > 0 ? cues[i - 1].End + Gap : TimeSpan.Zero;
            var start = cue.Start;
            if (end - start < target) start = Min(cue.Start, Max(earliestStart, end - target));

            cue.End = end;
            cue.Start = start;
            if (Cps(cue, language) <= limit + 0.05) fixedCount++;
            else if (cue.End - cue.Start > before) improved++;
            else noRoom++;
        }
        return new ReadingSpeedFix(fixedCount, improved, noRoom);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
