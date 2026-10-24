using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Bridges the app's own phonemizer (EnglishG2P, which speaks Kokoro's misaki alphabet) to a Piper voice's phoneme
/// set, so Piper can be driven without espeak-ng (an external tool the project doesn't allow). Most of Kokoro's
/// symbols are already plain IPA and pass straight through; a handful are misaki's one-character stand-ins for
/// diphthongs, affricates and taps, which are expanded to the multi-character IPA Piper's espeak-trained voices use.
///
/// This is the make-or-break of the Piper path: if every symbol the app's G2P produces has a clean IPA target here,
/// Piper is viable with no external phonemizer. Any symbol that lands in <see cref="Unmapped"/> is one espeak would
/// have produced differently — a quality risk to weigh, not a silent drop.
/// </summary>
public static class PiperPhonemes
{
    /// <summary>
    /// Kokoro/misaki symbols that are NOT plain IPA, mapped to the IPA an espeak-trained Piper voice expects.
    /// The diphthongs and affricate/tap stand-ins are misaki's; everything else in Kokoro's table is already IPA.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        // Diphthongs (misaki writes each as one capital letter; espeak uses the two-vowel IPA).
        ["A"] = "eɪ",   // "day"
        ["I"] = "aɪ",   // "my"
        ["O"] = "oʊ",   // "go"
        ["W"] = "aʊ",   // "now"
        ["Y"] = "ɔɪ",   // "boy"
        ["Q"] = "ə",    // misaki's reduced vowel variant → schwa
        // Affricates and tap: misaki single chars → IPA ligatures / tap.
        ["T"] = "ɾ",    // the flap in "water"
        ["S"] = "ʃ",    // handled as IPA ʃ (also present directly in the table)
        ["ᵊ"] = "ə",            // ᵊ superscript schwa → schwa
        ["ʣ"] = "dz", ["ʥ"] = "dz", ["ʦ"] = "ts", ["ʨ"] = "ts", // rare affricate ligatures → clusters
        ["ᵝ"] = "b", ["ꭧ"] = "ɾ", ["ᵻ"] = "ɪ",   // other misaki rarities → nearest IPA
    };

    /// <summary>Symbols from a run that had no mapping (for logging/quality checks); empty when all mapped.</summary>
    public static IReadOnlyList<string> Unmapped(string kokoroPhonemes)
    {
        var missing = new List<string>();
        foreach (var r in Runes(kokoroPhonemes))
            if (!Passthrough(r) && !Map.ContainsKey(r)) missing.Add(r);
        return missing;
    }

    /// <summary>Translate a Kokoro/misaki phoneme string to the IPA a Piper voice expects.</summary>
    public static string ToIpa(string kokoroPhonemes)
    {
        var sb = new StringBuilder(kokoroPhonemes.Length);
        foreach (var r in Runes(kokoroPhonemes))
        {
            if (Map.TryGetValue(r, out var ipa)) sb.Append(ipa);
            else if (Passthrough(r)) sb.Append(r);
            // else: no mapping — dropped, and surfaced by Unmapped() so it isn't silent.
        }
        return sb.ToString();
    }

    /// <summary>A symbol that is already IPA (or punctuation/space/stress) and needs no translation.</summary>
    private static bool Passthrough(string symbol)
    {
        if (symbol.Length == 0) return false;
        // Misaki's ASCII-letter stand-ins (A I O Q S T W Y) are NOT passthrough — they're in Map. Lowercase ASCII
        // letters are IPA in Kokoro's table (a b d e f … ) and pass through. Everything non-ASCII in the table is IPA.
        char c = symbol[0];
        if ("AIOQSTWY".IndexOf(c) >= 0) return false;
        return true;
    }

    /// <summary>Split into grapheme-ish units: each char is one symbol (Kokoro's alphabet is single-char per phoneme).</summary>
    private static IEnumerable<string> Runes(string s)
    {
        foreach (var c in s) yield return c.ToString();
    }
}
