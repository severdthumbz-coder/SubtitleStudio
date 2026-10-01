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
        var text = Spaces().Replace((translated ?? string.Empty).Replace('\n', ' '), " ").Trim();
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

    public static string SystemMessage(string? sourceLanguage, string targetLanguage)
    {
        var from = string.IsNullOrWhiteSpace(sourceLanguage) ? "the language they are in" : sourceLanguage;
        return $"You translate film and TV subtitles from {from} into {targetLanguage}.\n"
             + "Rules:\n"
             + $"- Translate every numbered line and keep its number: one line in, one line out, in the same order.\n"
             + $"- Write natural, spoken {targetLanguage}, the way people really talk in a film, not word for word. Keep it short enough to read as a subtitle.\n"
             + "- Keep the meaning, the tone and how polite or casual the speaker is. Don't add explanations, notes, quotes or the original text.\n"
             + "- Keep names as they sound and spell each name the same way every time.\n"
             + "- \" / \" inside a line separates two speakers: keep it, with one part per speaker.\n"
             + "- Lines under \"Earlier lines\" were already translated: they are only there for context. Translate only the lines under \"Translate\".";
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
