using System.Reflection;
using System.Text.RegularExpressions;

namespace SubtitleStudio.Services;

public sealed record RevisionLine(string Text, bool IsHeading);

public sealed record RevisionEntry(string Version, string Date, IReadOnlyList<RevisionLine> Lines)
{
    public string Title => string.IsNullOrWhiteSpace(Date) ? $"v{Version}" : $"v{Version}  ·  {Date}";
}

/// <summary>
/// Parses CHANGELOG.md (embedded at build time) for the Help tab, so the repo changelog and the
/// in-app revision history can never drift apart.
/// Recognised: "## [1.0.0.1] - 2026-09-25", "### Added", "- bullet", indented continuation lines.
/// </summary>
public static partial class ChangelogService
{
    private const string ResourceName = "SubtitleStudio.CHANGELOG.md";

    [GeneratedRegex(@"^##\s+\[?(?<ver>[^\]\s]+)\]?\s*(?:[-–—]\s*(?<date>.+))?$")]
    private static partial Regex VersionHeader();

    public static IReadOnlyList<RevisionEntry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return Array.Empty<RevisionEntry>();
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<RevisionEntry> Parse(string markdown)
    {
        var entries = new List<RevisionEntry>();
        string? version = null, date = null;
        var lines = new List<RevisionLine>();

        void Commit()
        {
            if (version is not null) entries.Add(new RevisionEntry(version, date ?? string.Empty, lines.ToList()));
            lines.Clear();
        }

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var header = VersionHeader().Match(line);
            if (header.Success)
            {
                Commit();
                version = header.Groups["ver"].Value;
                date = header.Groups["date"].Success ? header.Groups["date"].Value.Trim() : null;
                continue;
            }
            if (version is null || line.Length == 0) continue;

            if (line.StartsWith("### ", StringComparison.Ordinal))
                lines.Add(new RevisionLine(line[4..].Trim(), IsHeading: true));
            else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) && !char.IsWhiteSpace(line[0]))
                lines.Add(new RevisionLine(line.TrimStart()[2..].Trim(), IsHeading: false));
            else if (lines.Count > 0 && !lines[^1].IsHeading)
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + line.Trim().TrimStart('-').Trim() };
        }
        Commit();
        return entries;
    }
}
