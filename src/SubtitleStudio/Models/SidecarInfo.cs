namespace SubtitleStudio.Models;

/// <summary>
/// A subtitle file found next to a video and named so Plex/Jellyfin pair it:
/// &lt;VideoBase&gt;.&lt;lang&gt;[.forced][.hi|.sdh].&lt;ext&gt;
/// </summary>
public sealed record SidecarInfo(
    string FilePath,
    string? Language,
    bool Forced,
    bool HearingImpaired,
    bool Sdh,
    string? Label,
    string Format)
{
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Upper-case tags for badge display, e.g. EN, FORCED, HI, SRT.</summary>
    public IReadOnlyList<string> Tags
    {
        get
        {
            var tags = new List<string>();
            tags.Add(string.IsNullOrEmpty(Language) ? "NO LANG" : Language.ToUpperInvariant());
            if (Forced) tags.Add("FORCED");
            if (HearingImpaired) tags.Add("HI");
            if (Sdh) tags.Add("SDH");
            if (!string.IsNullOrEmpty(Label)) tags.Add(Label);
            tags.Add(Format.ToUpperInvariant());
            return tags;
        }
    }

    public string ShortLabel
    {
        get
        {
            var parts = new List<string> { string.IsNullOrEmpty(Language) ? "?" : Language };
            if (Forced) parts.Add("forced");
            if (HearingImpaired) parts.Add("hi");
            if (Sdh) parts.Add("sdh");
            return $"{string.Join(".", parts)} ({Format.ToLowerInvariant()})";
        }
    }
}
