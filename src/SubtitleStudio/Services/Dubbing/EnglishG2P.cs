using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// English text to the phonemes Kokoro was trained on (misaki's US set), inside the app: no espeak or other
/// program. A C# port of the parts of misaki's English G2P (Apache-2.0, hexgrad) that work without a
/// part-of-speech tagger: its pronunciation dictionaries (183,000 words, embedded), plural, past and -ing
/// forms of known words, the special words ("the" before a vowel, "a", "to"...), numbers, years, money and
/// times. Words not in the dictionary: Korean names in romanized spelling ("Myeong-jin", "Hau-young") by
/// syllable, anything else by English spelling rules.
/// </summary>
public sealed partial class EnglishG2P
{
    private const char Primary = 'ˈ';
    private const char Secondary = 'ˌ';
    private const string Vowels = "AIOQWYaiuæɑɒɔəɛɜɪʊʌᵻ";
    private const string Consonants = "bdfhjklmnpstvwzðŋɡɹɾʃʒʤʧθ";
    private const string NonQuotePuncts = ";:,.!?—…";
    private const string Puncts = ";:,.!?—…\"“”";
    private const string UsTaus = "AIOWYiuæɑəɛɪɹʊʌ";
    private const string Diphthongs = "AIOQWYʤʧ";

    private readonly Dictionary<string, string> _gold;
    private readonly Dictionary<string, string> _silver;

    private EnglishG2P(Dictionary<string, string> gold, Dictionary<string, string> silver)
    {
        _gold = gold;
        _silver = silver;
    }

    private static EnglishG2P? _shared;
    private static readonly object Gate = new();

    /// <summary>The dictionary embedded in the app (loaded once, about 20 MB in memory).</summary>
    public static EnglishG2P Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared is not null) return _shared;
                using var stream = typeof(EnglishG2P).Assembly.GetManifestResourceStream("SubtitleStudio.en-us-lexicon.tsv.gz")
                                   ?? throw new InvalidOperationException("The English pronunciation dictionary is missing from the app.");
                return _shared = Load(stream);
            }
        }
    }

    /// <summary>Reads the packed dictionary: "g|s TAB word TAB phonemes" lines, gzip-compressed.</summary>
    public static EnglishG2P Load(Stream gzip)
    {
        var gold = new Dictionary<string, string>(200_000, StringComparer.Ordinal);
        var silver = new Dictionary<string, string>(200_000, StringComparer.Ordinal);
        using var reader = new StreamReader(new GZipStream(gzip, CompressionMode.Decompress), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3) continue;
            (parts[0] == "g" ? gold : silver)[parts[1]] = parts[2];
        }
        return new EnglishG2P(Grow(gold), Grow(silver));
    }

    /// <summary>misaki's grow_dictionary: "word" also answers "Word", and "Word" answers "word".</summary>
    private static Dictionary<string, string> Grow(Dictionary<string, string> d)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in d)
        {
            if (k.Length < 2) continue;
            var lower = k.ToLowerInvariant();
            var cap = Capitalize(k);
            if (k == lower) { if (k != cap) extra.TryAdd(cap, v); }
            else if (k == Capitalize(lower)) extra.TryAdd(lower, v);
        }
        foreach (var (k, v) in extra) d.TryAdd(k, v);
        return d;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    // ------------------------------------------------------------------ text to phonemes

    private static readonly char[] Notes = { '♪', '♫', '♬' };

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex NotSpoken();

    [GeneratedRegex(@"(?<word>[A-Za-z]+(?:['’][A-Za-z]+)*['’]?)|(?<num>[$£€]?\d+(?:[,.:]\d+)*(?:st|nd|rd|th|s|'s|%)?)|(?<dash>-+)|(?<punct>[;:,.!?—…""“”])|(?<sym>[%&+@/])|(?<space>\s+)|(?<other>.)")]
    private static partial Regex Tokens();

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["Dr"] = "Doctor", ["Mr"] = "Mister", ["Mrs"] = "Missus", ["Ms"] = "Miz", ["St"] = "Saint", ["Prof"] = "Professor", ["Jr"] = "Junior",
        ["Sr"] = "Senior", ["vs"] = "versus", ["Vs"] = "versus", ["Lt"] = "Lieutenant", ["Sgt"] = "Sergeant", ["Capt"] = "Captain",
    };

    private sealed record Token(string Text, string Kind, bool SpaceAfter)
    {
        public string? Phonemes { get; set; }
    }

    /// <summary>
    /// Phonemes for a subtitle line: sound labels ([door closes], (sighs)), song lines (♪) and speaker dashes left out,
    /// punctuation kept (it gives the pauses and the question intonation).
    /// </summary>
    public string Phonemize(string text)
    {
        // Song lines (with ♪) are sung, not spoken: left to the original audio.
        var spoken = string.Join("\n", (text ?? string.Empty).Replace("\r", string.Empty).Split('\n').Where(l => l.IndexOfAny(Notes) < 0));
        var clean = NotSpoken().Replace(spoken, " ");
        clean = clean.Replace('’', '\'').Replace('‘', '\'').Replace("...", "…").Replace("--", "—").Replace('\n', ' ');
        clean = Regex.Replace(clean, @"(^|\s)-\s*", "$1"); // "- Where?" speaker dashes
        var tokens = new List<Token>();
        foreach (Match m in Tokens().Matches(clean))
        {
            string kind = m.Groups["word"].Success ? "word" : m.Groups["num"].Success ? "num" : m.Groups["dash"].Success ? "dash"
                : m.Groups["punct"].Success ? "punct" : m.Groups["sym"].Success ? "sym" : m.Groups["space"].Success ? "space" : "other";
            if (kind == "space")
            {
                if (tokens.Count > 0) tokens[^1] = tokens[^1] with { SpaceAfter = true };
                continue;
            }
            if (kind == "other") continue;
            // "Dr." and "Mrs." end no sentence: the dot is dropped and the word read in full.
            if (kind == "punct" && m.Value == "." && tokens.Count > 0 && tokens[^1].Kind == "word" && !tokens[^1].SpaceAfter
                && Abbreviations.TryGetValue(tokens[^1].Text, out var full))
            {
                tokens[^1] = tokens[^1] with { Text = full };
                continue;
            }
            tokens.Add(new Token(m.Value, kind, false));
        }

        // Right to left, so "the", "a" and "to" know whether a vowel follows.
        bool? futureVowel = null;
        bool futureTo = false;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            t.Phonemes = t.Kind switch
            {
                "punct" => t.Text,
                "dash" => i > 0 && !tokens[i - 1].SpaceAfter && i + 1 < tokens.Count && !t.SpaceAfter ? string.Empty : "—",
                "sym" => Symbol(t.Text, futureVowel),
                "num" => Number(t.Text),
                _ => Word(t.Text, futureVowel, futureTo),
            };
            if (!string.IsNullOrEmpty(t.Phonemes))
            {
                foreach (var c in t.Phonemes)
                {
                    if (NonQuotePuncts.Contains(c)) { futureVowel = null; break; }
                    if (Vowels.Contains(c)) { futureVowel = true; break; }
                    if (Consonants.Contains(c)) { futureVowel = false; break; }
                }
            }
            futureTo = t.Kind == "word" && t.Text is "to" or "To";
        }

        // Pieces written together ("Myeong-jin", "self-aware") are one word: their main stresses reduced as misaki does.
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            int j = i;
            while (j + 1 < tokens.Count && !tokens[j].SpaceAfter && tokens[j + 1].Kind is "word" or "dash" && tokens[j].Kind is "word" or "dash") j++;
            if (j > i) ResolveGroup(tokens.GetRange(i, j - i + 1));
            for (int k = i; k <= j; k++)
            {
                var t = tokens[k];
                if (t.Kind == "punct" && sb.Length > 0 && sb[^1] == ' ' && t.Text is not ("“" or "\"")) sb.Length--;
                sb.Append(t.Phonemes);
                if (t.SpaceAfter && !string.IsNullOrEmpty(t.Phonemes)) sb.Append(' ');
            }
            i = j;
        }
        // Kokoro v1.0 spells the flap and glottal stop with T and t.
        return sb.ToString().Trim().Replace('ɾ', 'T').Replace('ʔ', 't');
    }

    private static void ResolveGroup(List<Token> group)
    {
        var words = group.Where(t => !string.IsNullOrEmpty(t.Phonemes)).ToList();
        if (words.Count < 2) return;
        var indices = words.Select((t, i) => (Primary: t.Phonemes!.Contains(Primary), Weight: StressWeight(t.Phonemes!), Index: i)).ToList();
        if (indices.Count(x => x.Primary) <= (indices.Count + 1) / 2) return;
        foreach (var x in indices.OrderBy(x => x.Primary).ThenBy(x => x.Weight).ThenBy(x => x.Index).Take(indices.Count / 2))
            words[x.Index].Phonemes = ApplyStress(words[x.Index].Phonemes!, -0.5);
    }

    private static int StressWeight(string ps) => ps.Sum(c => Diphthongs.Contains(c) ? 2 : 1);

    private string? Symbol(string s, bool? futureVowel) => s switch
    {
        "%" => Lookup("percent", null),
        "&" => Lookup("and", null),
        "+" => Lookup("plus", null),
        "@" => Lookup("at", null),
        _ => string.Empty,
    };

    // ------------------------------------------------------------------ words

    /// <summary>One word: dictionary, then its -s/-ed/-ing forms, then Korean romanization, then spelling rules.</summary>
    public string Word(string word, bool? futureVowel = null, bool futureTo = false)
    {
        word = word.Replace('’', '\'');
        if (Special(word, futureVowel, futureTo) is { } special) return special;
        double? stress = word == word.ToLowerInvariant() ? null : word == word.ToUpperInvariant() ? 2 : 0.5;
        var wl = word.ToLowerInvariant();
        if (word.Length > 1 && word.Replace("'", "").All(char.IsLetter) && word != wl && !_gold.ContainsKey(word) && !_silver.ContainsKey(word)
            && (word == word.ToUpperInvariant() || word[1..] == word[1..].ToLowerInvariant())
            && (_gold.ContainsKey(wl) || _silver.ContainsKey(wl) || StemS(wl, stress) is not null || StemEd(wl, stress) is not null || StemIng(wl, stress) is not null))
            word = wl;
        string? ps = null;
        if (IsKnown(word)) ps = Lookup(word, stress);
        else if (word.EndsWith("s'") && IsKnown(word[..^2] + "'s")) ps = Lookup(word[..^2] + "'s", stress);
        else if (word.EndsWith('\'') && IsKnown(word[..^1])) ps = Lookup(word[..^1], stress);
        ps ??= StemS(word, stress) ?? StemEd(word, stress) ?? StemIng(word, stress ?? 0.5);
        if (ps is not null) return ps;
        // Not in the dictionary: a romanized Korean name, else English spelling rules.
        return KoreanRomanization.Phonemes(word) ?? SpellingRules(word);
    }

    private string? Special(string word, bool? futureVowel, bool futureTo)
    {
        switch (word)
        {
            case "a" or "A": return "ɐ";
            case "am" or "Am": return futureVowel is null ? Gold("am") : "ɐm";
            case "an" or "An": return "ɐn";
            case "I": return "ˌI";
            case "to" or "To": return futureVowel switch { null => Gold("to"), false => "tə", true => "tʊ" };
            case "in" or "In": return futureVowel is null ? "ˈɪn" : "ɪn";
            case "the" or "The": return futureVowel == true ? "ði" : "ðə";
            case "vs" or "vs.": return Lookup("versus", null);
            case "used" or "Used": return futureTo ? "jˈust" : Gold("used");
            // The dictionary's default is the adjective (a live show); in dialogue it's mostly the verb.
            case "live" or "Live": return "lˈɪv";
        }
        return null;
    }

    private string? Gold(string word) => _gold.TryGetValue(word, out var v) ? v : null;

    private bool IsKnown(string word)
    {
        if (_gold.ContainsKey(word) || _silver.ContainsKey(word)) return true;
        if (!word.All(c => c is '\'' or '-' or >= 'A' and <= 'Z' or >= 'a' and <= 'z') || !word.Any(char.IsLetter)) return false;
        if (word.Length == 1) return true;
        if (word == word.ToUpperInvariant() && _gold.ContainsKey(word.ToLowerInvariant())) return true;
        // Initials and acronyms ("FFP", "ICU") are spelled letter by letter.
        return word.Length <= 5 && word[1..] == word[1..].ToUpperInvariant() && word.All(char.IsLetter);
    }

    private string? Lookup(string word, double? stress)
    {
        if (word == word.ToUpperInvariant() && !_gold.ContainsKey(word))
        {
            if (word.Length is >= 2 and <= 5 && word.All(char.IsLetter) && !_gold.ContainsKey(word.ToLowerInvariant()) && !_silver.ContainsKey(word.ToLowerInvariant()))
                return Letters(word);
            word = word.ToLowerInvariant();
        }
        var ps = Gold(word) ?? (_silver.TryGetValue(word, out var s) ? s : null);
        ps ??= Letters(word);
        return ps is null ? null : ApplyStress(ps, stress);
    }

    /// <summary>A word read letter by letter ("ICU": ˌIsˌiˈju).</summary>
    private string? Letters(string word)
    {
        var parts = new List<string>();
        foreach (var c in word.Where(char.IsLetter))
        {
            if (Gold(char.ToUpperInvariant(c).ToString()) is not { } p) return null;
            parts.Add(p);
        }
        if (parts.Count == 0) return null;
        var ps = ApplyStress(string.Concat(parts), 0);
        int last = ps.LastIndexOf(Secondary);
        return last < 0 ? ps : ps[..last] + Primary + ps[(last + 1)..];
    }

    // ------------------------------------------------------------------ endings (misaki's _s, _ed, _ing)

    private static string? S(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        char last = stem[^1];
        if ("ptkfθ".Contains(last)) return stem + "s";
        if ("szʃʒʧʤ".Contains(last)) return stem + "ᵻz";
        return stem + "z";
    }

    private string? StemS(string word, double? stress)
    {
        if (word.Length < 3 || !word.EndsWith('s')) return null;
        string stem;
        if (!word.EndsWith("ss") && IsKnown(word[..^1])) stem = word[..^1];
        else if ((word.EndsWith("'s") || (word.Length > 4 && word.EndsWith("es") && !word.EndsWith("ies"))) && IsKnown(word[..^2])) stem = word[..^2];
        else if (word.Length > 4 && word.EndsWith("ies") && IsKnown(word[..^3] + "y")) stem = word[..^3] + "y";
        else return null;
        return S(Lookup(stem, stress));
    }

    private static string? Ed(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        char last = stem[^1];
        if ("pkfθʃsʧ".Contains(last)) return stem + "t";
        if (last == 'd') return stem + "ᵻd";
        if (last != 't') return stem + "d";
        if (stem.Length < 2) return stem + "ɪd";
        if (UsTaus.Contains(stem[^2])) return stem[..^1] + "ɾᵻd";
        return stem + "ᵻd";
    }

    private string? StemEd(string word, double? stress)
    {
        if (word.Length < 4 || !word.EndsWith('d')) return null;
        string stem;
        if (!word.EndsWith("dd") && IsKnown(word[..^1])) stem = word[..^1];
        else if (word.Length > 4 && word.EndsWith("ed") && !word.EndsWith("eed") && IsKnown(word[..^2])) stem = word[..^2];
        else return null;
        return Ed(Lookup(stem, stress));
    }

    private static string? Ing(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        if (stem.Length > 1 && stem[^1] == 't' && UsTaus.Contains(stem[^2])) return stem[..^1] + "ɾɪŋ";
        return stem + "ɪŋ";
    }

    [GeneratedRegex(@"([bcdgklmnprstvxz])\1ing$|cking$")]
    private static partial Regex DoubledIng();

    private string? StemIng(string word, double? stress)
    {
        if (word.Length < 5 || !word.EndsWith("ing")) return null;
        string stem;
        if (word.Length > 5 && IsKnown(word[..^3])) stem = word[..^3];
        else if (IsKnown(word[..^3] + "e")) stem = word[..^3] + "e";
        else if (word.Length > 5 && DoubledIng().IsMatch(word) && IsKnown(word[..^4])) stem = word[..^4];
        else return null;
        return Ing(Lookup(stem, stress));
    }

    /// <summary>misaki's apply_stress: none (null), reduce (-1, -0.5, 0 with a main stress), add (0.5/1/2).</summary>
    internal static string ApplyStress(string ps, double? stress)
    {
        if (stress is not { } s) return ps;
        bool hasPrimary = ps.Contains(Primary), hasAny = hasPrimary || ps.Contains(Secondary);
        if (s < -1) return ps.Replace(Primary.ToString(), "").Replace(Secondary.ToString(), "");
        if (s == -1 || (s is 0 or -0.5 && hasPrimary)) return ps.Replace(Secondary.ToString(), "").Replace(Primary, Secondary);
        if (s is 0 or 0.5 or 1 && !hasAny) return ps.Any(c => Vowels.Contains(c)) ? Restress(Secondary + ps) : ps;
        if (s >= 1 && !hasPrimary && ps.Contains(Secondary)) return ps.Replace(Secondary, Primary);
        if (s > 1 && !hasAny) return ps.Any(c => Vowels.Contains(c)) ? Restress(Primary + ps) : ps;
        return ps;
    }

    /// <summary>Moves each stress mark to just before the vowel it stresses.</summary>
    private static string Restress(string ps)
    {
        var items = ps.Select((c, i) => (Pos: (double)i, C: c)).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].C != Primary && items[i].C != Secondary) continue;
            int j = i;
            while (j < items.Count && !Vowels.Contains(items[j].C)) j++;
            if (j < items.Count) items[i] = (j - 0.5, items[i].C);
        }
        return new string(items.OrderBy(x => x.Pos).Select(x => x.C).ToArray());
    }

    // ------------------------------------------------------------------ numbers

    [GeneratedRegex(@"^(?<cur>[$£€])?(?<n>\d+(?:[,.:]\d+)*)(?<suf>st|nd|rd|th|s|'s|%)?$")]
    private static partial Regex NumberParts();

    /// <summary>Numbers as English speaks them: cardinals, ordinals (1st), years (1984), times (5:30), money ($20), decimals, percent.</summary>
    public string Number(string text)
    {
        var m = NumberParts().Match(text);
        if (!m.Success) return string.Empty;
        string cur = m.Groups["cur"].Value, n = m.Groups["n"].Value, suf = m.Groups["suf"].Value;
        var words = new List<string>();
        if (n.Contains(':'))
        {
            var hm = n.Split(':');
            words.AddRange(Cardinal(long.Parse(hm[0], CultureInfo.InvariantCulture)));
            if (hm.Length > 1 && long.TryParse(hm[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                if (minutes == 0) words.Add("o'clock");
                else
                {
                    if (minutes < 10) words.Add("oh");
                    words.AddRange(Cardinal(minutes));
                }
            }
        }
        else if (cur.Length > 0)
        {
            var parts = n.Replace(",", "").Split('.');
            long whole = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long cents = parts.Length > 1 && parts[1].Length is 1 or 2 ? long.Parse(parts[1].PadRight(2, '0'), CultureInfo.InvariantCulture) : 0;
            var (unit, small) = cur switch { "£" => ("pound", "pence"), "€" => ("euro", "cent"), _ => ("dollar", "cent") };
            if (whole > 0 || cents == 0)
            {
                words.AddRange(Cardinal(whole));
                words.Add(whole == 1 ? unit : unit + "s");
            }
            if (cents > 0)
            {
                if (whole > 0) words.Add("and");
                words.AddRange(Cardinal(cents));
                words.Add(small == "pence" || cents == 1 ? small : small + "s");
            }
        }
        else if (n.Contains('.') && !n.Contains(','))
        {
            var parts = n.Split('.');
            words.AddRange(Cardinal(long.Parse(parts[0], CultureInfo.InvariantCulture)));
            words.Add("point");
            foreach (var d in parts[1]) words.AddRange(Cardinal(d - '0'));
        }
        else
        {
            var digits = n.Replace(",", "");
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return string.Empty;
            if (suf is "st" or "nd" or "rd" or "th") words.AddRange(Ordinal(value));
            else if (digits.Length == 4 && !n.Contains(',') && value is >= 1100 and <= 2099 && suf != "%") words.AddRange(Year(value));
            else words.AddRange(Cardinal(value));
        }
        if (suf == "%") words.Add("percent");
        var ps = string.Join(" ", words.Select(w => Lookup(w, null) ?? SpellingRules(w)));
        return suf is "s" or "'s" ? S(ps) ?? ps : ps;
    }

    private static readonly string[] Ones = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };

    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    public static List<string> Cardinal(long n)
    {
        var words = new List<string>();
        if (n < 0) { words.Add("minus"); n = -n; }
        if (n < 20) { words.Add(Ones[n]); return words; }
        foreach (var (value, name) in new[] { (1_000_000_000_000L, "trillion"), (1_000_000_000L, "billion"), (1_000_000L, "million"), (1000L, "thousand") })
        {
            if (n >= value)
            {
                words.AddRange(Cardinal(n / value));
                words.Add(name);
                n %= value;
                if (n == 0) return words;
            }
        }
        if (n >= 100)
        {
            words.Add(Ones[n / 100]);
            words.Add("hundred");
            n %= 100;
            if (n == 0) return words;
        }
        if (n < 20) words.Add(Ones[n]);
        else
        {
            words.Add(Tens[n / 10]);
            if (n % 10 > 0) words.Add(Ones[n % 10]);
        }
        return words;
    }

    public static List<string> Ordinal(long n)
    {
        var words = Cardinal(n);
        var last = words[^1];
        words[^1] = last switch
        {
            "one" => "first", "two" => "second", "three" => "third", "five" => "fifth", "eight" => "eighth", "nine" => "ninth", "twelve" => "twelfth",
            _ when last.EndsWith('y') => last[..^1] + "ieth",
            _ => last + "th",
        };
        return words;
    }

    /// <summary>1984: nineteen eighty-four; 2005: two thousand five; 1900: nineteen hundred.</summary>
    public static List<string> Year(long y)
    {
        long hi = y / 100, lo = y % 100;
        if (y is >= 2000 and < 2010) return Cardinal(y);
        var words = Cardinal(hi);
        if (lo == 0) words.Add("hundred");
        else
        {
            if (lo < 10) words.Add("oh");
            words.AddRange(Cardinal(lo));
        }
        return words;
    }

    // ------------------------------------------------------------------ spelling rules (last resort)

    private static readonly (string Letters, string Sound)[] Rules =
    {
        ("tion", "ʃən"), ("sion", "ʒən"), ("ough", "ʌf"), ("igh", "I"), ("tch", "ʧ"), ("dge", "ʤ"),
        ("ph", "f"), ("th", "θ"), ("sh", "ʃ"), ("ch", "ʧ"), ("ck", "k"), ("ng", "ŋ"), ("qu", "kw"), ("wh", "w"), ("kn", "n"), ("wr", "ɹ"),
        ("ee", "i"), ("ea", "i"), ("oo", "u"), ("ou", "W"), ("ow", "O"), ("ai", "A"), ("ay", "A"), ("oi", "Y"), ("oy", "Y"), ("au", "ɔ"), ("aw", "ɔ"),
        ("ey", "A"), ("ie", "i"), ("ue", "u"), ("ew", "ju"), ("er", "əɹ"), ("ir", "ɜɹ"), ("ur", "ɜɹ"), ("ar", "ɑɹ"), ("or", "ɔɹ"),
        ("a", "æ"), ("e", "ɛ"), ("i", "ɪ"), ("o", "ɑ"), ("u", "ʌ"), ("b", "b"), ("d", "d"), ("f", "f"), ("g", "ɡ"), ("h", "h"), ("j", "ʤ"),
        ("k", "k"), ("l", "l"), ("m", "m"), ("n", "n"), ("p", "p"), ("r", "ɹ"), ("s", "s"), ("t", "t"), ("v", "v"), ("w", "w"), ("x", "ks"), ("z", "z"),
    };

    /// <summary>A rough reading from the spelling, stressed on the first vowel, for words found nowhere else.</summary>
    public static string SpellingRules(string word)
    {
        var w = word.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z').ToArray();
        var s = new string(w);
        if (s.Length > 2 && s.EndsWith('e') && !"aeiou".Contains(s[^2])) s = s[..^1]; // silent final e
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length;)
        {
            if (i + 1 < s.Length && s[i] == s[i + 1] && !"aeiou".Contains(s[i])) { i++; continue; } // doubled consonant
            if (s[i] == 'c') { sb.Append(i + 1 < s.Length && "eiy".Contains(s[i + 1]) ? "s" : "k"); i++; continue; }
            if (s[i] == 'y') { sb.Append(i == 0 ? "j" : "i"); i++; continue; }
            var rule = Rules.FirstOrDefault(r => string.CompareOrdinal(s, i, r.Letters, 0, r.Letters.Length) == 0);
            if (rule.Letters is null) { i++; continue; }
            sb.Append(rule.Sound);
            i += rule.Letters.Length;
        }
        var ps = sb.ToString();
        int v = ps.IndexOfAny(Vowels.ToCharArray());
        return v < 0 ? ps : ps.Insert(v, Primary.ToString());
    }
}
