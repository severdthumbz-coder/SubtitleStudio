using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Korean names in Revised Romanization ("Myeong-jin", "Seo-yeon", "Hyun-woo") read the Korean way rather
/// than by English spelling: the word is split into romanized syllables and each one given its sound.
/// Only used for words the English dictionary doesn't know; a word that can't be split into Korean
/// syllables returns null (and is read by English rules).
/// </summary>
public static class KoreanRomanization
{
    private static readonly (string Spelling, string Sound)[] Onsets =
    {
        ("kk", "k"), ("tt", "t"), ("pp", "p"), ("ss", "s"), ("jj", "ʤ"), ("ch", "ʧ"), ("sh", "ʃ"),
        ("g", "ɡ"), ("k", "k"), ("n", "n"), ("d", "d"), ("t", "t"), ("r", "ɹ"), ("l", "l"), ("m", "m"),
        ("b", "b"), ("p", "p"), ("s", "s"), ("j", "ʤ"), ("h", "h"), ("", ""),
    };

    private static readonly (string Spelling, string Sound)[] VowelSounds =
    {
        ("yeo", "jʌ"), ("yae", "jɛ"), ("wae", "wɛ"), ("eo", "ʌ"), ("eu", "ʊ"), ("ae", "ɛ"), ("oe", "wɛ"), ("wi", "wi"), ("ui", "ɨi"),
        ("ya", "jɑ"), ("yo", "jO"), ("yu", "ju"), ("ye", "jɛ"), ("wa", "wɑ"), ("wo", "wʌ"), ("we", "wɛ"),
        ("oo", "u"), ("oi", "Y"), ("ee", "i"), ("au", "W"), ("ou", "O"),
        ("a", "ɑ"), ("e", "ɛ"), ("i", "i"), ("o", "O"), ("u", "u"),
    };

    private static readonly (string Spelling, string Sound)[] Codas =
    {
        ("ng", "ŋ"), ("k", "k"), ("n", "n"), ("t", "t"), ("l", "l"), ("m", "m"), ("p", "p"), ("g", "k"), ("", ""),
    };

    /// <summary>Surnames an English dub says the English way ("Kim" as in Kimberly, not "Keem").</summary>
    private static readonly Dictionary<string, string> Usual = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kim"] = "kˈɪm", ["lim"] = "lˈɪm", ["shin"] = "ʃˈɪn", ["jin"] = "ʤˈɪn", ["min"] = "mˈɪn", ["bin"] = "bˈɪn",
    };

    /// <summary>Phonemes for a romanized Korean word, stressed on its first syllable; null when it isn't one.</summary>
    public static string? Phonemes(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 2 || !word.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) return null;
        if (Usual.TryGetValue(word, out var usual)) return usual;
        var syllables = Split(word.ToLowerInvariant());
        if (syllables is null) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < syllables.Count; i++)
        {
            var (onset, vowel, coda) = syllables[i];
            sb.Append(onset);
            if (i == 0) sb.Append('ˈ');
            sb.Append(vowel).Append(coda);
        }
        // Ui ("ɨi") has no ɨ in Kokoro's English set: as "ʊi".
        return sb.ToString().Replace("ɨ", "ʊ");
    }

    /// <summary>
    /// The syllables, by dynamic programming: fewest syllables, then the fewest consonants left at the end of
    /// a syllable when the next one could have taken them ("Mina" is mi-na, not min-a). Null if impossible.
    /// </summary>
    public static List<(string Onset, string Vowel, string Coda)>? Split(string s)
    {
        int n = s.Length;
        var best = new (int Count, int Penalty, int From, (string, string, string) Syl)?[n + 1];
        best[0] = (0, 0, -1, ("", "", ""));
        for (int i = 0; i < n; i++)
        {
            if (best[i] is not { } here) continue;
            foreach (var (os, oSound) in Onsets)
            {
                if (string.CompareOrdinal(s, i, os, 0, os.Length) != 0) continue;
                int v = i + os.Length;
                foreach (var (vs, vSound) in VowelSounds)
                {
                    if (string.CompareOrdinal(s, v, vs, 0, vs.Length) != 0) continue;
                    int c = v + vs.Length;
                    foreach (var (cs, cSound) in Codas)
                    {
                        if (string.CompareOrdinal(s, c, cs, 0, cs.Length) != 0) continue;
                        int end = c + cs.Length;
                        if (end > n) continue;
                        // A syllable with no onset after a vowel-final one ("ha-eun") is fine; after a coda it's
                        // usually the coda that belongs to it. A coda before an onset-less syllable costs one.
                        int penalty = here.Penalty + (os.Length == 0 && i > 0 && here.Syl.Item3.Length > 0 ? 1 : 0);
                        var candidate = (here.Count + 1, penalty, i, (oSound, vSound, cSound));
                        if (best[end] is not { } old || candidate.Item1 < old.Count || (candidate.Item1 == old.Count && penalty < old.Penalty))
                            best[end] = candidate;
                    }
                }
            }
        }
        if (best[n] is null) return null;
        var result = new List<(string, string, string)>();
        for (int at = n; at > 0;)
        {
            var b = best[at]!.Value;
            result.Add(b.Syl);
            at = b.From;
        }
        result.Reverse();
        return result;
    }
}
