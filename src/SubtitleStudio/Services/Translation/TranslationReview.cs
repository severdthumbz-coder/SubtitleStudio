using System.Text.RegularExpressions;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Translation;

public enum ReviewFlagKind { Missing, Leftover, Unchanged, Repeated, Question }

/// <summary>Why a translated line deserves a look.</summary>
public sealed record ReviewFlag(ReviewFlagKind Kind, string Message);

/// <summary>
/// Reviewing a translation next to its original: each translated cue paired with the original line(s)
/// shown at the same time, and the lines worth checking found (words left in the original script, lines
/// left untranslated or empty, a line repeating the one before although the originals differ, a question
/// that lost its question mark).
/// </summary>
public static partial class TranslationReview
{
    [GeneratedRegex(@"\{[^}]*\}|</?[a-zA-Z][^>]*>")]
    private static partial Regex Tags();

    /// <summary>A cue's text for reading: tags removed, lines kept.</summary>
    public static string Plain(string text) => Tags().Replace(text ?? string.Empty, string.Empty).Replace("\r", string.Empty).Trim();

    /// <summary>
    /// The original text for each translated cue. A translation made here has the same cues at the same
    /// times (paired one to one); otherwise each cue gets the original lines that overlap it most in time.
    /// </summary>
    public static IReadOnlyList<string?> Pair(IReadOnlyList<SubtitleCue> translation, IReadOnlyList<SubtitleCue> original)
    {
        var result = new string?[translation.Count];
        if (translation.Count == original.Count
            && translation.Zip(original).All(p => Math.Abs((p.First.Start - p.Second.Start).TotalMilliseconds) < 60))
        {
            for (int i = 0; i < translation.Count; i++) result[i] = Plain(original[i].Text);
            return result;
        }
        var sorted = original.Where(c => c.End > c.Start).OrderBy(c => c.Start).ToList();
        int first = 0;
        for (int i = 0; i < translation.Count; i++)
        {
            var t = translation[i];
            while (first < sorted.Count && sorted[first].End <= t.Start - TimeSpan.FromSeconds(10)) first++;
            var parts = new List<string>();
            for (int j = first; j < sorted.Count && sorted[j].Start < t.End; j++)
            {
                var o = sorted[j];
                var overlap = (Min(o.End, t.End) - Max(o.Start, t.Start)).TotalSeconds;
                if (overlap <= 0) continue;
                // Counted when it covers a good part of either cue (a short original inside a long cue, or the other way).
                var shorter = Math.Min((o.End - o.Start).TotalSeconds, (t.End - t.Start).TotalSeconds);
                if (overlap >= shorter * 0.4) parts.Add(Plain(o.Text));
            }
            result[i] = parts.Count == 0 ? null : string.Join("\n", parts);
        }
        return result;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>What's worth checking in one translated line (null: nothing).</summary>
    public static ReviewFlag? Check(string? original, string translation, string? previousOriginal, string? previousTranslation, string targetLanguage)
    {
        if (original is null) return null;
        var o = Plain(original);
        var t = Plain(translation);
        if (!o.Any(char.IsLetter)) return null; // music, symbols
        if (!t.Any(char.IsLetter)) return new ReviewFlag(ReviewFlagKind.Missing, "No translation: the line is empty or has no words.");
        if (SubtitleTranslationPrompt.HasLeftoverScript(t, targetLanguage))
            return new ReviewFlag(ReviewFlagKind.Leftover, "Words left in the original script.");
        if (Key(o) == Key(t) && Words(o) >= 2)
            return new ReviewFlag(ReviewFlagKind.Unchanged, "Not translated: the same as the original.");
        if (previousOriginal is not null && previousTranslation is not null && Words(t) >= 2
            && Key(t) == Key(Plain(previousTranslation)) && Key(o) != Key(Plain(previousOriginal)))
            return new ReviewFlag(ReviewFlagKind.Repeated, "The same as the line before, though the originals differ.");
        if (IsQuestion(o) && !t.Contains('?') && !t.Contains('？'))
            return new ReviewFlag(ReviewFlagKind.Question, "The original is a question; the translation isn't.");
        return null;
    }

    private static bool IsQuestion(string text)
    {
        var trimmed = text.TrimEnd(' ', '.', '"', '\'', '」', '』', ')', '-');
        return trimmed.EndsWith('?') || trimmed.EndsWith('？');
    }

    private static string Key(string text) => new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
}
