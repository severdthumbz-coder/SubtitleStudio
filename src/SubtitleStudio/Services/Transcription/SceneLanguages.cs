namespace SubtitleStudio.Services.Transcription;

/// <summary>A chunk of speech and the language to transcribe it in (null: the main language).</summary>
public sealed record SceneChunk(SpeechChunk Chunk, string? Language);

/// <summary>
/// Which language each scene is spoken in. Whisper detects the language of each chunk of speech (up to
/// about 28 seconds); a chunk it is sure about is transcribed in that language. A chunk it isn't sure about
/// may hold two languages (a Korean doctor answering in Korean to a question in Japanese): its stretches
/// of speech are judged one by one and the chunk is cut where the language changes. Short stretches are
/// too short to judge and go with their neighbours.
/// </summary>
public static class SceneLanguages
{
    public sealed record Options
    {
        /// <summary>How sure Whisper must be of the main language for a chunk to count as all in it.</summary>
        public double SureChunk { get; init; } = 0.8;

        /// <summary>
        /// How sure Whisper must be of another language to switch a whole chunk (of 5 seconds or more) to it.
        /// Lower than <see cref="SureChunk"/>: on Hyper Knife E03 the Japanese scenes came out at 56–70%
        /// Japanese and the English speech at 74% English, while no Korean chunk was ever heard as another language.
        /// </summary>
        public double SwitchChunk { get; init; } = 0.5;

        /// <summary>
        /// How sure for one stretch of speech in an unclear chunk: its top choice at 50% or more. On E03 the stretches
        /// of Japanese came out at 55–93% Japanese, and Korean ones at 72–100% Korean.
        /// </summary>
        public double SurePiece { get; init; } = 0.5;

        /// <summary>How sure for a whole chunk with under 5 seconds of speech (short, so harder to judge).</summary>
        public double SureShortChunk { get; init; } = 0.9;

        /// <summary>Chunks with less speech than this stay in the main language.</summary>
        public TimeSpan MinChunk { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>Stretches shorter than this aren't judged on their own.</summary>
        public TimeSpan MinPiece { get; init; } = TimeSpan.FromSeconds(1.5);
    }

    public static TimeSpan Speech(SpeechChunk chunk) => Seconds(chunk.Pieces.Sum(p => (long)p.Length));

    /// <summary>The language of most of the speech among the chunks Whisper was sure about (null: none sure).</summary>
    public static string? MainLanguage(IReadOnlyList<SpeechChunk> chunks, IReadOnlyList<(string Code, float Probability)?> detected, Options? options = null)
    {
        options ??= new Options();
        var totals = new Dictionary<string, double>();
        for (int i = 0; i < chunks.Count && i < detected.Count; i++)
            if (detected[i] is { } d && d.Probability >= options.SureChunk)
                totals[d.Code] = totals.GetValueOrDefault(d.Code) + Speech(chunks[i]).TotalSeconds;
        return totals.Count == 0 ? null : totals.MaxBy(t => t.Value).Key;
    }

    /// <summary>Chunks worth judging stretch by stretch: enough speech, more than one stretch, and Whisper not sure of the whole.</summary>
    public static bool IsUnsure(SpeechChunk chunk, (string Code, float Probability)? detected, string main, Options? options = null)
    {
        options ??= new Options();
        if (chunk.Pieces.Count < 2 || Speech(chunk) < options.MinChunk) return false;
        if (detected is not { } d) return true;
        if (d.Code == main) return d.Probability < options.SureChunk;
        return ChunkLanguage(chunk, detected, main, options) == main; // another language, but not clear enough to switch the whole chunk
    }

    /// <summary>The stretches of a chunk long enough to judge on their own.</summary>
    public static IEnumerable<int> JudgedPieces(SpeechChunk chunk, Options? options = null)
    {
        options ??= new Options();
        for (int p = 0; p < chunk.Pieces.Count; p++)
            if (Seconds(chunk.Pieces[p].Length) >= options.MinPiece) yield return p;
    }

    /// <summary>A chunk's language: another language when Whisper hears it clearly enough (<see cref="Options.SwitchChunk"/>; under 5 seconds as sure as a stretch), else the main one.</summary>
    public static string ChunkLanguage(SpeechChunk chunk, (string Code, float Probability)? detected, string main, Options? options = null)
    {
        options ??= new Options();
        var speech = Speech(chunk);
        if (detected is not { } d || d.Code == main || speech < options.MinChunk) return main;
        // A short chunk is harder to judge: as sure as a single stretch has to be.
        double sure = speech < TimeSpan.FromSeconds(5) ? Math.Max(options.SwitchChunk, options.SureShortChunk) : options.SwitchChunk;
        return d.Probability >= sure ? d.Code : main;
    }

    /// <summary>
    /// An unsure chunk cut where the language changes. <paramref name="pieceLanguages"/>: what Whisper heard
    /// in each stretch (null where it wasn't judged or wasn't sure); those go with the stretch before them
    /// (or after, at the start), and the main language when no stretch was sure.
    /// </summary>
    public static IReadOnlyList<SceneChunk> Split(SpeechChunk chunk, IReadOnlyList<(string Code, float Probability)?> pieceLanguages, string main, Options? options = null)
    {
        options ??= new Options();
        var langs = new string?[chunk.Pieces.Count];
        for (int p = 0; p < langs.Length; p++)
            langs[p] = p < pieceLanguages.Count && pieceLanguages[p] is { } d && d.Probability >= options.SurePiece ? d.Code : null;
        if (langs.All(l => l is null)) return new[] { new SceneChunk(chunk, main) };
        for (int p = 1; p < langs.Length; p++) langs[p] ??= langs[p - 1];
        for (int p = langs.Length - 2; p >= 0; p--) langs[p] ??= langs[p + 1];

        var result = new List<SceneChunk>();
        int start = 0;
        for (int p = 1; p <= langs.Length; p++)
        {
            if (p < langs.Length && langs[p] == langs[start]) continue;
            result.Add(new SceneChunk(start == 0 && p == langs.Length ? chunk : Slice(chunk, start, p - 1), langs[start]));
            start = p;
        }
        return result;
    }

    /// <summary>Stretches <paramref name="first"/> to <paramref name="last"/> of a chunk as a chunk of their own.</summary>
    public static SpeechChunk Slice(SpeechChunk chunk, int first, int last)
    {
        int from = chunk.Pieces[first].ChunkOffset;
        int to = chunk.Pieces[last].ChunkOffset + chunk.Pieces[last].Length;
        var pieces = chunk.Pieces.Skip(first).Take(last - first + 1).Select(p => p with { ChunkOffset = p.ChunkOffset - from }).ToList();
        return new SpeechChunk(chunk.Audio.Slice(from, Math.Min(to, chunk.Audio.Length) - from), pieces);
    }

    /// <summary>Where the speech isn't in the main language, as time ranges (neighbouring chunks of the same language joined).</summary>
    public static IReadOnlyList<(string Language, TimeSpan Start, TimeSpan End)> OtherScenes(IReadOnlyList<SceneChunk> chunks, string main)
    {
        var result = new List<(string Language, TimeSpan Start, TimeSpan End)>();
        bool lastWasOther = false;
        foreach (var c in chunks)
        {
            if (c.Language is null || c.Language == main)
            {
                lastWasOther = false;
                continue;
            }
            if (lastWasOther && result[^1].Language == c.Language)
                result[^1] = (c.Language, result[^1].Start, c.Chunk.SourceEnd);
            else
                result.Add((c.Language, c.Chunk.SourceStart, c.Chunk.SourceEnd));
            lastWasOther = true;
        }
        return result;
    }

    /// <summary>
    /// A line that mixes Korean letters with Japanese ones, or with Chinese characters (Latin is left out:
    /// English words are common in any language). The languages it could be: <paramref name="heardIn"/>
    /// and those the letters suggest. Null for a line in one script.
    /// </summary>
    public static IReadOnlyList<string>? MixedScriptCandidates(string text, string heardIn)
    {
        int hangul = 0, kana = 0, han = 0;
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul++;
            else if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿') han++;
        }
        bool korean = hangul > 0, japanese = kana > 0, chinese = han > 0 && kana == 0;
        if ((korean ? 1 : 0) + (japanese || chinese ? 1 : 0) < 2) return null;
        var result = new List<string> { heardIn };
        if (korean) result.Add("ko");
        if (japanese || chinese) result.Add("ja");
        if (chinese) result.Add("zh");
        return result.Distinct().ToList();
    }

    private static TimeSpan Seconds(long samples) => TimeSpan.FromSeconds(samples / (double)AudioExtractor.SampleRate);
}
