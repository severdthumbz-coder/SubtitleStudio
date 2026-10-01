using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleStudio.Services.Subtitles;

/// <summary>Parsing and formatting of subtitle timestamps in every supported notation.</summary>
public static partial class Timecode
{
    // h:mm:ss[,.:]fff  |  mm:ss.fff  |  ss.fff   (hours and fraction optional, 1-3 fraction digits)
    [GeneratedRegex(@"^\s*(?<neg>-)?(?:(?:(?<h>\d+):)?(?<m>\d{1,2}):)?(?<s>\d{1,2})(?:[,.:](?<f>\d{1,3}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRx();

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = TimeRx().Match(text);
        if (!m.Success) return false;

        int h = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        int s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (m.Groups["m"].Success && (min > 59 || s > 59)) return false;

        int ms = 0;
        if (m.Groups["f"].Success)
        {
            // Fractions are decimal: "5" = 500 ms, "05" = 50 ms (ASS centiseconds), "005" = 5 ms.
            ms = int.Parse(m.Groups["f"].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        }

        value = new TimeSpan(0, h, min, s, ms);
        if (m.Groups["neg"].Success) value = value.Negate();
        return true;
    }

    /// <summary>Editor display: 00:01:02,345 (SRT style, familiar to most users).</summary>
    public static string Format(TimeSpan t) => FormatCore(t, ',', 3);

    public static string FormatSrt(TimeSpan t) => FormatCore(Clamp(t), ',', 3);

    public static string FormatVtt(TimeSpan t) => FormatCore(Clamp(t), '.', 3);

    /// <summary>ASS/SSA: H:MM:SS.cc (centiseconds, single-digit hours allowed).</summary>
    public static string FormatAss(TimeSpan t)
    {
        t = Clamp(t);
        long cs = (long)Math.Round(t.TotalMilliseconds / 10.0, MidpointRounding.AwayFromZero);
        long h = cs / 360000; cs %= 360000;
        long m = cs / 6000; cs %= 6000;
        long s = cs / 100; cs %= 100;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", h, m, s, cs);
    }

    public static string FormatDuration(TimeSpan d)
        => (d < TimeSpan.Zero ? "-" : string.Empty) + Math.Abs(d.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture) + "s";

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static string FormatCore(TimeSpan t, char sep, int digits)
    {
        var neg = t < TimeSpan.Zero;
        if (neg) t = t.Negate();
        long ms = (long)Math.Round(t.TotalMilliseconds, MidpointRounding.AwayFromZero);
        long h = ms / 3600000; ms %= 3600000;
        long m = ms / 60000; ms %= 60000;
        long s = ms / 1000; ms %= 1000;
        var frac = digits == 3 ? ms.ToString("000", CultureInfo.InvariantCulture) : (ms / 10).ToString("00", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.InvariantCulture, "{0}{1:00}:{2:00}:{3:00}{4}{5}", neg ? "-" : "", h, m, s, sep, frac);
    }
}
