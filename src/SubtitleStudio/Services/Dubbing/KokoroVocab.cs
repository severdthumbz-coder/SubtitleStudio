namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Kokoro v1.0's phoneme alphabet: each symbol and the number the model knows it by (from the model's
/// config.json, Apache-2.0). Symbols outside it are dropped.
/// </summary>
public static class KokoroVocab
{
    /// <summary>Longest input the model takes in one pass, in phonemes.</summary>
    public const int MaxPhonemes = 510;

    private static readonly (string Symbol, int Id)[] Table =
    {
        (";", 1), (":", 2), (",", 3), (".", 4), ("!", 5), ("?", 6), ("\u2014", 9), ("\u2026", 10), ("\u0022", 11), ("(", 12),
        (")", 13), ("\u201C", 14), ("\u201D", 15), (" ", 16), ("\u0303", 17), ("\u02A3", 18), ("\u02A5", 19), ("\u02A6", 20), ("\u02A8", 21), ("\u1D5D", 22),
        ("\uAB67", 23), ("A", 24), ("I", 25), ("O", 31), ("Q", 33), ("S", 35), ("T", 36), ("W", 39), ("Y", 41), ("\u1D4A", 42),
        ("a", 43), ("b", 44), ("c", 45), ("d", 46), ("e", 47), ("f", 48), ("h", 50), ("i", 51), ("j", 52), ("k", 53),
        ("l", 54), ("m", 55), ("n", 56), ("o", 57), ("p", 58), ("q", 59), ("r", 60), ("s", 61), ("t", 62), ("u", 63),
        ("v", 64), ("w", 65), ("x", 66), ("y", 67), ("z", 68), ("\u0251", 69), ("\u0250", 70), ("\u0252", 71), ("\u00E6", 72), ("\u03B2", 75),
        ("\u0254", 76), ("\u0255", 77), ("\u00E7", 78), ("\u0256", 80), ("\u00F0", 81), ("\u02A4", 82), ("\u0259", 83), ("\u025A", 85), ("\u025B", 86), ("\u025C", 87),
        ("\u025F", 90), ("\u0261", 92), ("\u0265", 99), ("\u0268", 101), ("\u026A", 102), ("\u029D", 103), ("\u026F", 110), ("\u0270", 111), ("\u014B", 112), ("\u0273", 113),
        ("\u0272", 114), ("\u0274", 115), ("\u00F8", 116), ("\u0278", 118), ("\u03B8", 119), ("\u0153", 120), ("\u0279", 123), ("\u027E", 125), ("\u027B", 126), ("\u0281", 128),
        ("\u027D", 129), ("\u0282", 130), ("\u0283", 131), ("\u0288", 132), ("\u02A7", 133), ("\u028A", 135), ("\u028B", 136), ("\u028C", 138), ("\u0263", 139), ("\u0264", 140),
        ("\u03C7", 142), ("\u028E", 143), ("\u0292", 147), ("\u0294", 148), ("\u02C8", 156), ("\u02CC", 157), ("\u02D0", 158), ("\u02B0", 162), ("\u02B2", 164), ("\u2193", 169),
        ("\u2192", 171), ("\u2197", 172), ("\u2198", 173), ("\u1D7B", 177),
    };

    private static readonly Dictionary<char, int> Ids = Table.ToDictionary(t => t.Symbol[0], t => t.Id);

    public static int Count => Table.Length;

    public static bool Knows(char c) => Ids.ContainsKey(c);

    /// <summary>The model's numbers for a phoneme string (unknown symbols left out; no padding).</summary>
    public static long[] Tokenize(string phonemes)
    {
        var ids = new List<long>(phonemes.Length);
        foreach (var c in phonemes)
            if (Ids.TryGetValue(c, out var id)) ids.Add(id);
        return ids.ToArray();
    }

    /// <summary>
    /// Phonemes cut into pieces the model can say in one pass, at the least disruptive place: after a
    /// sentence, then a clause, then between words (as kokoro-onnx's chunker does).
    /// </summary>
    public static List<string> Split(string phonemes, int max = MaxPhonemes)
    {
        var result = new List<string>();
        SplitInto(phonemes.Trim(), max, 0, result);
        return result;
    }

    private static readonly string[][] Boundaries = { new[] { ".", "!", "?", "…" }, new[] { ",", ";", ":" }, new[] { "" } };

    private static void SplitInto(string text, int max, int level, List<string> into)
    {
        if (text.Length == 0) return;
        if (text.Length <= max) { into.Add(text); return; }
        for (int l = level; l < Boundaries.Length; l++)
        {
            var pieces = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != ' ' || i == 0) continue;
                bool cut = Boundaries[l][0].Length == 0 || Boundaries[l].Contains(text[i - 1].ToString());
                if (!cut) continue;
                pieces.Add(text[start..i].Trim());
                start = i + 1;
            }
            if (pieces.Count == 0) continue;
            pieces.Add(text[start..].Trim());
            // Join neighbours back while they fit, so pieces aren't needlessly short.
            var current = string.Empty;
            foreach (var p in pieces.Where(p => p.Length > 0))
            {
                if (current.Length > 0 && current.Length + 1 + p.Length <= max) { current += " " + p; continue; }
                if (current.Length > 0) SplitInto(current, max, l + 1, into);
                current = p;
            }
            if (current.Length > 0) SplitInto(current, max, l + 1, into);
            return;
        }
        for (int i = 0; i < text.Length; i += max) into.Add(text.Substring(i, Math.Min(max, text.Length - i)));
    }
}
