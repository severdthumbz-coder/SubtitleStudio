using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleStudio.Services.Subtitles;

/// <summary>How formatting is written inside cue text.</summary>
public enum TextDialect
{
    /// <summary>SRT / VTT: &lt;i&gt; &lt;b&gt; &lt;u&gt; &lt;font&gt;, plus the common {\anN} position tag.</summary>
    Html,

    /// <summary>ASS / SSA: {\i1} override blocks, \N line breaks.</summary>
    Ass,

    /// <summary>MicroDVD: {y:i} / {Y:i} control codes, "|" line breaks.</summary>
    MicroDvd,
}

/// <summary>
/// Converts cue text between tag dialects when saving to a different format. Same-dialect saves are
/// untouched, so tags round-trip exactly. HTML-style is the pivot. Only portable formatting
/// (italic, bold, underline, {\anN} position) survives a conversion; styling that has no equivalent
/// in the target is dropped rather than written as garbage.
/// </summary>
public static partial class SubtitleText
{
    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex AssBlock();

    [GeneratedRegex(@"\\(?<tag>[ibu])(?<val>\d+)|\\an(?<an>[1-9])")]
    private static partial Regex AssTag();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyHtmlTag();

    [GeneratedRegex(@"</?font[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FontTag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssBlockInHtml();

    [GeneratedRegex(@"^\{(?<scope>[yY]):(?<styles>[ibus,]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex MicroDvdStyle();

    [GeneratedRegex(@"\{[a-zA-Z]:[^}]*\}")]
    private static partial Regex MicroDvdAnyCode();

    public static TextDialect DialectOf(string formatId) => formatId.ToLowerInvariant() switch
    {
        "ass" or "ssa" => TextDialect.Ass,
        "sub" => TextDialect.MicroDvd,
        _ => TextDialect.Html,
    };

    public static string Convert(string text, TextDialect from, TextDialect to)
    {
        if (from == to) return text;

        var html = from switch
        {
            TextDialect.Ass => AssToHtml(text),
            TextDialect.MicroDvd => MicroDvdToHtml(text),
            _ => text,
        };

        return to switch
        {
            TextDialect.Ass => HtmlToAss(html),
            TextDialect.MicroDvd => HtmlToMicroDvd(html),
            _ => html,
        };
    }

    /// <summary>Plain text with every tag removed (for display, search, translation length checks).</summary>
    public static string StripTags(string text)
        => AssBlockInHtml().Replace(AnyHtmlTag().Replace(text, string.Empty), string.Empty)
            .Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");

    /// <summary>VTT supports i/b/u/c/v/ruby but not &lt;font&gt; or ASS blocks.</summary>
    public static string ForVtt(string html) => AssBlockInHtml().Replace(FontTag().Replace(html, string.Empty), string.Empty);

    public static string AssToHtml(string text)
    {
        var s = text.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", "\u00A0");
        return AssBlock().Replace(s, block =>
        {
            var sb = new StringBuilder();
            foreach (Match tag in AssTag().Matches(block.Groups[1].Value))
            {
                if (tag.Groups["an"].Success)
                {
                    sb.Append("{\\an").Append(tag.Groups["an"].Value).Append('}');
                    continue;
                }
                var name = tag.Groups["tag"].Value;
                var on = tag.Groups["val"].Value != "0";
                sb.Append(on ? $"<{name}>" : $"</{name}>");
            }
            return sb.ToString();
        });
    }

    public static string HtmlToAss(string html)
    {
        var s = FontTag().Replace(html, string.Empty);
        s = Regex.Replace(s, @"<(/?)([ibu])>", m => $"{{\\{m.Groups[2].Value.ToLowerInvariant()}{(m.Groups[1].Value.Length == 0 ? "1" : "0")}}}", RegexOptions.IgnoreCase);
        s = AnyHtmlTag().Replace(s, string.Empty);
        s = s.Replace("}{\\", "\\");                // merge adjacent override blocks: {\an8}{\i1} -> {\an8\i1}
        return s.Replace("\r", string.Empty).Replace("\n", "\\N");
    }

    public static string MicroDvdToHtml(string text)
    {
        // On disk lines are separated by "|"; in the editor they are real newlines. Accept both.
        var lines = text.Replace("\r", string.Empty).Replace('\n', '|').Split('|');
        string wholeOpen = string.Empty, wholeClose = string.Empty;

        var first = MicroDvdStyle().Match(lines[0]);
        if (first.Success && first.Groups["scope"].Value == "Y")
        {
            (wholeOpen, wholeClose) = Wrap(first.Groups["styles"].Value);
            lines[0] = lines[0][first.Length..];
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var m = MicroDvdStyle().Match(lines[i]);
            string open = string.Empty, close = string.Empty;
            if (m.Success)
            {
                (open, close) = Wrap(m.Groups["styles"].Value);
                lines[i] = lines[i][m.Length..];
            }
            lines[i] = open + MicroDvdAnyCode().Replace(lines[i], string.Empty) + close;
        }

        return wholeOpen + string.Join("\n", lines) + wholeClose;

        static (string Open, string Close) Wrap(string styles)
        {
            var tags = styles.Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t is "i" or "b" or "u").Distinct().ToList();
            return (string.Concat(tags.Select(t => $"<{t}>")), string.Concat(Enumerable.Reverse(tags).Select(t => $"</{t}>")));
        }
    }

    public static string HtmlToMicroDvd(string html)
    {
        var s = AssBlockInHtml().Replace(html.Replace("\r", string.Empty), string.Empty);
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var styles = new List<string>();
            foreach (var t in new[] { "i", "b", "u" })
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith($"<{t}>", StringComparison.OrdinalIgnoreCase)
                    && trimmed.EndsWith($"</{t}>", StringComparison.OrdinalIgnoreCase))
                {
                    styles.Add(t);
                    line = trimmed[3..^4];
                }
            }
            line = AnyHtmlTag().Replace(line, string.Empty);
            lines[i] = (styles.Count > 0 ? $"{{y:{string.Join(",", styles)}}}" : string.Empty) + line;
        }
        return string.Join("|", lines);
    }
}
