using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Muxing;

/// <summary>
/// Language tags for tracks inside MKV and MP4: both containers use the three-letter ISO 639-2 codes
/// (the "bibliographic" ones, "ger" and "fre", which players and Plex read; the "terminology" ones,
/// "deu" and "fra", are understood when reading). The app's own codes are two letters ("ko").
/// </summary>
public static class TrackLanguages
{
    private static readonly Dictionary<string, string> ToThree = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "eng", ["zh"] = "chi", ["de"] = "ger", ["es"] = "spa", ["ru"] = "rus", ["ko"] = "kor", ["fr"] = "fre", ["ja"] = "jpn",
        ["pt"] = "por", ["tr"] = "tur", ["pl"] = "pol", ["ca"] = "cat", ["nl"] = "dut", ["ar"] = "ara", ["sv"] = "swe", ["it"] = "ita",
        ["id"] = "ind", ["hi"] = "hin", ["fi"] = "fin", ["vi"] = "vie", ["he"] = "heb", ["uk"] = "ukr", ["el"] = "gre", ["ms"] = "may",
        ["cs"] = "cze", ["ro"] = "rum", ["da"] = "dan", ["hu"] = "hun", ["ta"] = "tam", ["no"] = "nor", ["th"] = "tha", ["ur"] = "urd",
        ["hr"] = "hrv", ["bg"] = "bul", ["lt"] = "lit", ["la"] = "lat", ["mi"] = "mao", ["ml"] = "mal", ["cy"] = "wel", ["sk"] = "slo",
        ["te"] = "tel", ["fa"] = "per", ["lv"] = "lav", ["bn"] = "ben", ["sr"] = "srp", ["az"] = "aze", ["sl"] = "slv", ["kn"] = "kan",
        ["et"] = "est", ["mk"] = "mac", ["br"] = "bre", ["eu"] = "baq", ["is"] = "ice", ["hy"] = "arm", ["ne"] = "nep", ["mn"] = "mon",
        ["bs"] = "bos", ["kk"] = "kaz", ["sq"] = "alb", ["sw"] = "swa", ["gl"] = "glg", ["mr"] = "mar", ["pa"] = "pan", ["si"] = "sin",
        ["km"] = "khm", ["sn"] = "sna", ["yo"] = "yor", ["so"] = "som", ["af"] = "afr", ["oc"] = "oci", ["ka"] = "geo", ["be"] = "bel",
        ["tg"] = "tgk", ["sd"] = "snd", ["gu"] = "guj", ["am"] = "amh", ["yi"] = "yid", ["lo"] = "lao", ["uz"] = "uzb", ["fo"] = "fao",
        ["ht"] = "hat", ["ps"] = "pus", ["tk"] = "tuk", ["nn"] = "nno", ["mt"] = "mlt", ["sa"] = "san", ["lb"] = "ltz", ["my"] = "bur",
        ["bo"] = "tib", ["tl"] = "tgl", ["mg"] = "mlg", ["as"] = "asm", ["tt"] = "tat", ["haw"] = "haw", ["ln"] = "lin", ["ha"] = "hau",
        ["ba"] = "bak", ["jw"] = "jav", ["jv"] = "jav", ["su"] = "sun", ["nb"] = "nob", ["fil"] = "fil",
        // Cantonese has no ISO 639-2 code of its own: it is tagged Chinese (and named in the title).
        ["yue"] = "chi",
    };

    /// <summary>Terminology codes (and a few others seen in files) to the app's codes.</summary>
    private static readonly Dictionary<string, string> Extra = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deu"] = "de", ["fra"] = "fr", ["nld"] = "nl", ["ces"] = "cs", ["ron"] = "ro", ["ell"] = "el", ["msa"] = "ms", ["slk"] = "sk",
        ["fas"] = "fa", ["mkd"] = "mk", ["eus"] = "eu", ["isl"] = "is", ["hye"] = "hy", ["kat"] = "ka", ["mya"] = "my", ["bod"] = "bo",
        ["zho"] = "zh", ["cym"] = "cy", ["sqi"] = "sq", ["mri"] = "mi", ["nob"] = "no",
    };

    private static readonly Dictionary<string, string> FromThree = BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (two, three) in ToThree)
            if (two != "yue" && two != "jv" && two != "nb") map.TryAdd(three, two);
        foreach (var (three, two) in Extra) map.TryAdd(three, two);
        return map;
    }

    /// <summary>"ko" (or "ko-KR", "kor", "Korean") → "kor"; null when it isn't a language the app knows.</summary>
    public static string? ToIso6392(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var tag = language.Trim();
        if (ToThree.TryGetValue(tag, out var three)) return three;
        var head = tag.Split('-', '_')[0];
        if (ToThree.TryGetValue(head, out three)) return three;
        if (head.Length == 3 && FromThree.ContainsKey(head)) return ToThree[FromThree[head]];
        var byName = WhisperLanguages.All.FirstOrDefault(l => l.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
        return byName is null ? null : ToThree.GetValueOrDefault(byName.Code);
    }

    /// <summary>A track's tag ("kor", "ger", "deu", "en") → the app's code ("ko"); null for "und" or unknown.</summary>
    public static string? FromTrackTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;
        var head = tag.Trim().Split('-', '_')[0];
        if (FromThree.TryGetValue(head, out var two)) return two;
        return ToThree.ContainsKey(head) ? head.ToLowerInvariant() : null;
    }

    /// <summary>"Korean"; the tag itself when unknown; "No language" when empty.</summary>
    public static string NameOf(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "No language";
        var code = FromTrackTag(language) ?? language;
        return WhisperLanguages.All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? language;
    }
}
