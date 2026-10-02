using System.Text;
using System.Text.RegularExpressions;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>A cue's text made ready to translate, with what is put back afterwards.</summary>
/// <param name="Text">One line: the cue's lines joined (" / " between two speakers), tags removed.</param>
/// <param name="Prefix">Leading override tags such as {\an8} (position), put back in front.</param>
/// <param name="Italic">The whole cue was in &lt;i&gt;...&lt;/i&gt;.</param>
/// <param name="TwoSpeakers">Each line started with a dash (two people talking).</param>
public sealed record PreparedLine(string Text, string Prefix, bool Italic, bool TwoSpeakers)
{
    /// <summary>Nothing to translate (no letters: music notes, punctuation).</summary>
    public bool IsEmpty => !Text.Any(char.IsLetter);
}

/// <summary>
/// How a language model is asked to translate subtitles: numbered lines in, the same numbered lines
/// out, enforced by a grammar (the model can only write "1|...", "2|..." and so on, one per line, so no
/// line can be skipped, merged or wrapped in commentary). Earlier lines and their translations go along
/// as context, so names and tone stay consistent.
/// </summary>
public static partial class SubtitleTranslationPrompt
{
    [GeneratedRegex(@"^(\{[^}]*\})+")]
    private static partial Regex LeadingOverrides();

    [GeneratedRegex(@"\{[^}]*\}|</?[a-zA-Z][^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\*{1,2}|_{2})(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Emphasis();

    public static PreparedLine Prepare(string cueText)
    {
        var text = (cueText ?? string.Empty).Replace("\r", string.Empty).Trim();
        var prefix = LeadingOverrides().Match(text) is { Success: true } m ? m.Value : string.Empty;
        text = text[prefix.Length..].Trim();
        bool italic = text.StartsWith("<i>", StringComparison.OrdinalIgnoreCase) && text.EndsWith("</i>", StringComparison.OrdinalIgnoreCase)
                      && text.IndexOf("<i>", 3, StringComparison.OrdinalIgnoreCase) < 0;
        text = AnyTag().Replace(text, string.Empty);
        var lines = text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0).ToList();
        bool twoSpeakers = lines.Count >= 2 && lines.All(l => l.StartsWith('-'));
        var joined = twoSpeakers ? string.Join(" / ", lines) : string.Join(" ", lines);
        joined = joined.Replace('|', '/');
        return new PreparedLine(joined, prefix, italic, twoSpeakers);
    }

    /// <summary>The translated line back as a cue: two lines at most, speakers on their own lines, tags restored.</summary>
    public static string Restore(PreparedLine source, string translated, int maxColumns = 42)
    {
        var text = Tidy(source, translated);
        if (text.Length == 0) return source.Prefix + (source.Italic ? $"<i>{source.Text}</i>" : source.Text);
        string body;
        var parts = text.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (source.TwoSpeakers && parts.Length >= 2)
            body = string.Join("\n", parts.Select(p => p.StartsWith('-') ? p : "- " + p));
        else
            body = TranscriptShaper.Wrap(source.TwoSpeakers ? text : text.Replace(" / ", " "), maxColumns);
        if (source.Italic) body = $"<i>{body}</i>";
        return source.Prefix + body;
    }

    /// <summary>
    /// What a model adds that subtitles don't have: markdown emphasis (*you*), a speaker separator or dash
    /// the original line didn't have, spaces at the ends.
    /// </summary>
    public static string Tidy(PreparedLine source, string? translated)
    {
        var text = Spaces().Replace((translated ?? string.Empty).Replace('\n', ' '), " ").Trim();
        text = Emphasis().Replace(text, "$2");
        if (!source.TwoSpeakers)
        {
            text = text.Trim('/', ' ');
            if (text.StartsWith('-') && !source.Text.TrimStart().StartsWith('-')) text = text.TrimStart('-', ' ');
        }
        return text;
    }

    /// <summary>
    /// Letters of Korean, Japanese or Chinese left in a translation into a language that doesn't use
    /// them (names or words the model didn't translate, e.g. "Han 선생님").
    /// </summary>
    public static bool HasLeftoverScript(string? translated, string targetLanguage)
    {
        if (string.IsNullOrEmpty(translated)) return false;
        bool allowHangul = targetLanguage == "ko", allowKana = targetLanguage == "ja", allowHan = targetLanguage is "ja" or "zh" or "yue" or "ko";
        foreach (var c in translated)
        {
            if (!allowHangul && (c is >= '가' and <= '힣' || c is >= 'ᄀ' and <= 'ᇿ' || c is >= '㄰' and <= '㆏')) return true;
            if (!allowKana && c is >= '぀' and <= 'ヿ') return true;
            if (!allowHan && c is >= '一' and <= '鿿') return true;
        }
        return false;
    }

    /// <summary>Added to the question when a line came back with words left in the original script.</summary>
    public static string LeftoverReminder(string targetLanguage)
        => $"Your previous answer left words in the original script. Write the line entirely in {targetLanguage}: translate every word, titles and forms of address too, and write names in the alphabet {targetLanguage} uses.";

    public static string SystemMessage(string? sourceLanguage, string targetLanguage, IReadOnlyList<string>? names = null)
    {
        var from = string.IsNullOrWhiteSpace(sourceLanguage) ? "the language they are in" : sourceLanguage;
        bool korean = sourceLanguage is null or "Korean";
        var sb = new StringBuilder();
        sb.Append($"You translate film and TV subtitles from {from} into {targetLanguage}.\n")
          .Append("Rules:\n")
          .Append("- Translate every numbered line and keep its number: one line in, one line out, in the same order.\n")
          .Append($"- Write natural, spoken {targetLanguage}, the way people really talk in a film, not word for word. Keep it short enough to read as a subtitle.\n")
          .Append("- Keep the meaning, the tone and how polite or casual the speaker is. Don't add explanations, notes, quotes or the original text.\n")
          .Append("- Keep names as they sound and spell each name the same way every time. Never leave words in the original script.\n");
        if (korean)
            sb.Append("- Korean names in standard romanization (e.g. Kim Myeong-jin). Translate titles and forms of address into what a speaker of the target language would say (선생님: Doctor, Teacher or Sir by context; 교수님: Professor).\n");
        if (names is { Count: > 0 })
            sb.Append($"- Names in this show, always spelled exactly like this: {string.Join(", ", names.Take(60))}.\n");
        sb.Append("- Plain text only: no asterisks, quotes or other markup for emphasis.\n")
          .Append("- \" / \" inside a line separates two speakers: keep it, with one part per speaker.\n")
          .Append("- Lines under \"Earlier lines\" were already translated: they are only there for context. Translate only the lines under \"Translate\".");
        return sb.ToString();
    }

    public static string UserMessage(IReadOnlyList<(string Source, string Translation)> context, IReadOnlyList<string> lines, bool noThinking = false)
    {
        var sb = new StringBuilder();
        if (context.Count > 0)
        {
            sb.Append("Earlier lines (context only):\n");
            foreach (var (source, translation) in context) sb.Append(source).Append(" => ").Append(translation).Append('\n');
            sb.Append('\n');
        }
        sb.Append("Translate:\n");
        for (int i = 0; i < lines.Count; i++) sb.Append(i + 1).Append('|').Append(lines[i]).Append('\n');
        if (noThinking) sb.Append("/no_think");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>GBNF that allows exactly <paramref name="count"/> numbered lines, each with some text.</summary>
    public static string Grammar(int count)
    {
        var sb = new StringBuilder("root ::=");
        for (int i = 1; i <= count; i++) sb.Append(" l").Append(i);
        sb.Append('\n');
        for (int i = 1; i <= count; i++) sb.Append('l').Append(i).Append(" ::= \"").Append(i).Append("|\" text \"\\n\"\n");
        sb.Append("text ::= [^|\\n] [^|\\n]{0,400}\n");
        return sb.ToString();
    }

    /// <summary>The model's answer as one translation per line (empty where a line is missing).</summary>
    public static IReadOnlyList<string> Parse(string output, int count)
    {
        var result = new string[count];
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            int bar = line.IndexOf('|');
            if (bar <= 0 || !int.TryParse(line.AsSpan(0, bar), out var n) || n < 1 || n > count) continue;
            result[n - 1] ??= line[(bar + 1)..].Trim();
        }
        return result.Select(r => r ?? string.Empty).ToList();
    }
}
