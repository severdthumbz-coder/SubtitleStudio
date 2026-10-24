using System.Text.Json;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A Piper voice's config (the `.onnx.json` beside the model): the phoneme→id map the model was trained with, its
/// sample rate, and the inference scales. The app reads this to turn IPA phonemes (from PiperPhonemes) into the id
/// sequence the model expects, framed with the BOS/EOS/pad ids and a pad between every phoneme, exactly as Piper does.
/// </summary>
public sealed class PiperConfig
{
    /// <summary>IPA phoneme (one or more chars) → its id(s). Most map to a single id.</summary>
    public required IReadOnlyDictionary<string, int[]> PhonemeIdMap { get; init; }
    public required int SampleRate { get; init; }
    public float NoiseScale { get; init; } = 0.667f;
    public float LengthScale { get; init; } = 1.0f;
    public float NoiseW { get; init; } = 0.8f;

    // Piper's special symbols in the phoneme_id_map.
    public const string Bos = "^";   // beginning of sentence
    public const string Eos = "$";   // end of sentence
    public const string Pad = "_";   // inserted between every phoneme

    public static PiperConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var map = new Dictionary<string, int[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("phoneme_id_map", out var pm) && pm.ValueKind == JsonValueKind.Object)
            foreach (var p in pm.EnumerateObject())
                map[p.Name] = p.Value.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (map.Count == 0) throw new InvalidOperationException("The Piper voice config has no phoneme_id_map.");

        int rate = 22050;
        if (root.TryGetProperty("audio", out var audio) && audio.TryGetProperty("sample_rate", out var sr)) rate = sr.GetInt32();

        float noise = 0.667f, length = 1.0f, noiseW = 0.8f;
        if (root.TryGetProperty("inference", out var inf))
        {
            if (inf.TryGetProperty("noise_scale", out var ns)) noise = ns.GetSingle();
            if (inf.TryGetProperty("length_scale", out var ls)) length = ls.GetSingle();
            if (inf.TryGetProperty("noise_w", out var nw)) noiseW = nw.GetSingle();
        }

        return new PiperConfig { PhonemeIdMap = map, SampleRate = rate, NoiseScale = noise, LengthScale = length, NoiseW = noiseW };
    }

    /// <summary>
    /// The id sequence for a line of IPA phonemes, Piper-style: BOS, then each phoneme's id(s) with a pad id between,
    /// then EOS. Phonemes the voice doesn't know are skipped (and reported by <see cref="Missing"/>). Combining marks
    /// and multi-char symbols in the map are matched longest-first so e.g. a tie-bar affricate maps as one unit.
    /// </summary>
    public long[] ToIds(string ipa)
    {
        var ids = new List<long>(ipa.Length * 2 + 3);
        void Add(string sym) { if (PhonemeIdMap.TryGetValue(sym, out var v)) foreach (var id in v) ids.Add(id); }

        Add(Bos);
        foreach (var sym in Units(ipa))
        {
            if (PhonemeIdMap.ContainsKey(sym)) { Add(sym); if (PhonemeIdMap.ContainsKey(Pad)) Add(Pad); }
        }
        Add(Eos);
        return ids.ToArray();
    }

    /// <summary>IPA symbols in the text that this voice's map doesn't contain (for logging/quality checks).</summary>
    public IReadOnlyList<string> Missing(string ipa)
    {
        var missing = new List<string>();
        foreach (var sym in Units(ipa))
            if (!PhonemeIdMap.ContainsKey(sym)) missing.Add(sym);
        return missing;
    }

    /// <summary>
    /// Split IPA text into the units the map keys on: greedily match the longest map key at each position (so a
    /// multi-char key like a tie-bar affricate or a stressed vowel wins over its first char), else take one char.
    /// </summary>
    private IEnumerable<string> Units(string ipa)
    {
        int i = 0;
        while (i < ipa.Length)
        {
            // Try the longest key that matches here (map keys are short; cap the look-ahead at 3 chars).
            string? match = null;
            for (int len = Math.Min(3, ipa.Length - i); len >= 1; len--)
            {
                var slice = ipa.Substring(i, len);
                if (PhonemeIdMap.ContainsKey(slice)) { match = slice; break; }
            }
            if (match is not null) { yield return match; i += match.Length; }
            else { yield return ipa[i].ToString(); i += 1; }
        }
    }
}
