using System.Text.RegularExpressions;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services;

/// <summary>
/// Finds and names Plex/Jellyfin-style sidecar subtitles:
///     &lt;VideoBase&gt;.&lt;lang&gt;[.forced][.hi|.sdh].&lt;ext&gt;
/// e.g. "Film (2024).en.srt", "Film (2024).en.forced.srt", "Film (2024).en.hi.srt".
/// Parsing rule for ".hi": the first language-like token is the language (so "Film.hi.srt" is Hindi);
/// ".hi" after a language is the hearing-impaired flag. ".cc" is treated as hearing-impaired too.
/// </summary>
public static partial class SidecarDetector
{
    [GeneratedRegex(@"^[a-z]{2,3}(-[a-z0-9]{2,8})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageToken();

    public static IReadOnlyList<SidecarInfo> Detect(string videoPath, IEnumerable<string> siblingFiles)
    {
        var list = new List<SidecarInfo>();
        foreach (var candidate in siblingFiles)
        {
            if (TryParse(videoPath, candidate) is { } info) list.Add(info);
        }
        return list
            .OrderBy(s => s.Language ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Forced)
            .ThenBy(s => s.HearingImpaired || s.Sdh)
            .ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static SidecarInfo? TryParse(string videoPath, string candidatePath)
    {
        var ext = Path.GetExtension(candidatePath);
        if (!FileImportService.SubtitleExtensions.Contains(ext)) return null;
        if (string.Equals(videoPath, candidatePath, StringComparison.OrdinalIgnoreCase)) return null;

        var videoBase = Path.GetFileNameWithoutExtension(videoPath);
        var candidateName = Path.GetFileName(candidatePath);
        if (!candidateName.StartsWith(videoBase, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = candidateName[videoBase.Length..];         // ".en.forced.srt" or ".srt"
        if (!rest.StartsWith('.')) return null;               // "Film2.srt" must not match "Film.mkv"

        // Strip the leading "." and the extension. For "Film.srt" there is no middle at all.
        var middleLength = rest.Length - 1 - ext.Length;
        var middle = middleLength > 0 ? rest.Substring(1, middleLength).Trim('.') : string.Empty;
        var tokens = middle.Length == 0
            ? Array.Empty<string>()
            : middle.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? language = null;
        bool forced = false, hi = false, sdh = false;
        var labels = new List<string>();

        foreach (var raw in tokens)
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "forced":
                    forced = true;
                    continue;
                case "sdh":
                    sdh = true;
                    continue;
                case "cc":
                    hi = true;
                    continue;
                case "default":
                    continue;
                case "hi" when language is not null:
                    hi = true;
                    continue;
            }

            if (language is null && LanguageToken().IsMatch(token))
                language = token;
            else
                labels.Add(raw);
        }

        return new SidecarInfo(
            candidatePath,
            language,
            forced,
            hi,
            sdh,
            labels.Count == 0 ? null : string.Join(" ", labels),
            ext.TrimStart('.'));
    }

    /// <summary>
    /// For a subtitle opened without its video: splits "Film (2024).en.forced.srt" into the video base
    /// name ("Film (2024)") and its suffix info by reading tokens from the end. Returns the whole
    /// name as base when no language/flag tokens are present.
    /// </summary>
    public static (string BaseName, string? Language, bool Forced, bool HearingImpaired, bool Sdh) ParseStandalone(string subtitlePath)
    {
        var tokens = Path.GetFileNameWithoutExtension(subtitlePath).Split('.');
        int i = tokens.Length - 1;
        bool forced = false, hi = false, sdh = false;
        string? language = null;

        while (i > 0)
        {
            var t = tokens[i].ToLowerInvariant();
            if (t == "forced") forced = true;
            else if (t == "sdh") sdh = true;
            else if (t == "cc") hi = true;
            else if (t == "default") { }
            else if (t == "hi" && i > 1 && LanguageToken().IsMatch(tokens[i - 1]) && !IsFlag(tokens[i - 1])) hi = true;
            else break;
            i--;
        }

        if (i > 0 && LanguageToken().IsMatch(tokens[i]))
        {
            language = tokens[i].ToLowerInvariant();
            i--;
        }

        var baseName = string.Join(".", tokens.Take(i + 1));
        return (baseName, language, forced, hi, sdh);

        static bool IsFlag(string token) => token.ToLowerInvariant() is "forced" or "sdh" or "cc" or "default";
    }

    /// <summary>Builds "&lt;VideoBase&gt;.&lt;lang&gt;[.forced][.hi|.sdh].&lt;ext&gt;" (ext without dot).</summary>
    public static string BuildSidecarName(string videoBaseName, string? language, bool forced, bool hearingImpaired, bool sdh, string extension)
    {
        var parts = new List<string> { videoBaseName };
        if (!string.IsNullOrWhiteSpace(language)) parts.Add(language.Trim().ToLowerInvariant());
        if (forced) parts.Add("forced");
        if (sdh) parts.Add("sdh");
        else if (hearingImpaired) parts.Add("hi");
        parts.Add(extension.TrimStart('.').ToLowerInvariant());
        return string.Join(".", parts);
    }
}
