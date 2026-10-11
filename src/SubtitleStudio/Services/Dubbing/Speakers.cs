using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>One person speaking in the episode, as found from the voices (or remembered from earlier episodes).</summary>
public sealed class Speaker
{
    public required int Id { get; init; }

    /// <summary>"Speaker 1", or the name given in the Speakers list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"female", "male", or null when the pitch couldn't tell.</summary>
    public string? Gender { get; set; }

    /// <summary>Gender set by hand (kept over what the pitch says).</summary>
    public bool GenderSetByHand { get; set; }

    /// <summary>Median pitch of the speaker's lines, in Hz.</summary>
    public double? PitchHz { get; set; }

    /// <summary>The Kokoro voice the speaker is dubbed with.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>The speaker's lines (the subtitles' own cue objects).</summary>
    public List<SubtitleCue> Lines { get; } = new();

    /// <summary>Average voiceprint of the lines (for recognising them in the next episode).</summary>
    public float[]? Voiceprint { get; set; }

    /// <summary>Recognised from an earlier episode of the show.</summary>
    public bool Remembered { get; set; }

    public TimeSpan SpeechTime => TimeSpan.FromTicks(Lines.Sum(l => (l.End - l.Start).Ticks));
}

/// <summary>What Find speakers did.</summary>
public sealed record SpeakerResult(List<Speaker> Speakers, int Measured, int ByNeighbour, int Remembered);

/// <summary>
/// Who speaks each line: a voiceprint of each line's audio (WeSpeaker), lines grouped by how alike they
/// sound (average-linkage clustering on cosine similarity), the pitch of each group for male or female,
/// and a distinct Kokoro voice of that gender for each person, the most talkative first.
/// Lines too short for a voiceprint (under 0.5 s) go to the speaker of the nearest line around them.
/// </summary>
public static class SpeakerFinder
{
    /// <summary>Two groups of lines this alike (cosine) are taken for the same person.</summary>
    public const double SameSpeaker = 0.36;

    /// <summary>A group of fewer than 3 lines joins the most alike bigger group when at least this alike.</summary>
    public const double SmallGroupJoins = 0.33;

    public static readonly TimeSpan ShortestLine = TimeSpan.FromSeconds(0.5);
    public static readonly TimeSpan LongestPiece = TimeSpan.FromSeconds(10);

    /// <summary>Voices for women and for men, best first (KokoroVoices grades).</summary>
    public static readonly string[] FemaleVoices = { "af_heart", "af_bella", "bf_emma", "af_nicole", "af_aoede", "af_kore", "af_sarah", "bf_isabella", "af_nova", "af_alloy", "af_sky", "bf_alice", "bf_lily", "af_jessica", "af_river" };
    public static readonly string[] MaleVoices = { "am_michael", "am_fenrir", "bm_george", "am_puck", "bm_fable", "am_echo", "am_eric", "am_liam", "bm_lewis", "am_onyx", "bm_daniel", "am_adam" };

    /// <param name="audio">The episode's sound, 16 kHz mono, from the start of the video.</param>
    /// <param name="cues">The subtitles (time order not required).</param>
    /// <param name="book">Speakers remembered from earlier episodes of the show (null: none).</param>
    public static SpeakerResult Find(float[] audio, IReadOnlyList<SubtitleCue> cues, IVoiceprintModel model, SpeakerBook? book,
        IProgress<double>? progress, CancellationToken ct)
    {
        const int rate = Fbank.SampleRate;
        var lines = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start).ToList();
        var prints = new float[]?[lines.Count];
        var pitches = new List<double>?[lines.Count];
        int done = 0;
        Parallel.For(0, lines.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct }, i =>
        {
            var c = lines[i];
            var length = c.End - c.Start;
            if (length > LongestPiece) length = LongestPiece;
            int start = (int)(c.Start.TotalSeconds * rate), count = (int)(length.TotalSeconds * rate);
            if (start < audio.Length && count > 0)
            {
                count = Math.Min(count, audio.Length - start);
                var piece = audio.AsSpan(start, count);
                if (length >= ShortestLine && SubtitleSpeech(c.Text))
                {
                    var p = model.Embed(piece);
                    if (p.Length > 0) prints[i] = p;
                }
                pitches[i] = Pitch.Track(piece, rate);
            }
            progress?.Report(Interlocked.Increment(ref done) / (double)lines.Count * 0.9);
        });

        // Group the measured lines.
        var measured = Enumerable.Range(0, lines.Count).Where(i => prints[i] is not null).ToList();
        var groups = Cluster(measured.Select(i => prints[i]!).ToList(), SameSpeaker)
            .Select(g => g.Select(k => measured[k]).ToList()).ToList();
        groups = JoinSmall(groups, prints);
        progress?.Report(0.95);

        // Lines without a voiceprint: the speaker of the nearest measured line in time.
        var owner = new int[lines.Count];
        Array.Fill(owner, -1);
        for (int g = 0; g < groups.Count; g++) foreach (var i in groups[g]) owner[i] = g;
        int byNeighbour = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (owner[i] >= 0) continue;
            int best = -1;
            var bestGap = TimeSpan.MaxValue;
            for (int d = 1; d < lines.Count && (best < 0 || d < 4); d++)
            {
                foreach (var j in new[] { i - d, i + d })
                {
                    if (j < 0 || j >= lines.Count || prints[j] is null) continue;
                    var gap = j < i ? lines[i].Start - lines[j].End : lines[j].Start - lines[i].End;
                    if (gap < bestGap) { bestGap = gap; best = j; }
                }
            }
            if (best >= 0) { owner[i] = owner[best]; byNeighbour++; }
        }
        if (groups.Count == 0 && lines.Count > 0) { groups.Add(new List<int>()); for (int i = 0; i < lines.Count; i++) owner[i] = 0; }

        var speakers = new List<Speaker>();
        for (int g = 0; g < groups.Count; g++)
        {
            var s = new Speaker { Id = g + 1 };
            for (int i = 0; i < lines.Count; i++) if (owner[i] == g) s.Lines.Add(lines[i]);
            var allPitch = groups[g].Concat(Enumerable.Range(0, lines.Count).Where(i => owner[i] == g && prints[i] is null))
                .Where(i => pitches[i] is not null).SelectMany(i => pitches[i]!).ToList();
            s.PitchHz = Pitch.Median(allPitch);
            s.Gender = Pitch.GenderOf(s.PitchHz);
            s.Voiceprint = groups[g].Count > 0 ? Voiceprints.Mean(groups[g].Select(i => prints[i]!)) : null;
            speakers.Add(s);
        }
        speakers = speakers.Where(s => s.Lines.Count > 0).OrderByDescending(s => s.Lines.Count).ToList();
        for (int k = 0; k < speakers.Count; k++) speakers[k] = Renumber(speakers[k], k + 1);

        int remembered = book?.Recognise(speakers) ?? 0;
        AssignVoices(speakers);
        progress?.Report(1);
        return new SpeakerResult(speakers, measured.Count, byNeighbour, remembered);
    }

    private static Speaker Renumber(Speaker s, int id)
    {
        var r = new Speaker { Id = id, Name = $"Speaker {id}", Gender = s.Gender, PitchHz = s.PitchHz, Voiceprint = s.Voiceprint };
        r.Lines.AddRange(s.Lines);
        return r;
    }

    /// <summary>Lines that are spoken words (not "[music]", "♪ ... ♪" or empty).</summary>
    private static bool SubtitleSpeech(string text)
    {
        var plain = Subtitles.SubtitleText.StripTags(text ?? string.Empty);
        if (plain.Contains('♪')) return false;
        var noLabels = System.Text.RegularExpressions.Regex.Replace(plain, @"\[[^\]]*\]|\([^)]*\)", " ");
        return noLabels.Any(char.IsLetter);
    }

    /// <summary>
    /// Average-linkage agglomerative clustering: the two most alike groups are joined while their average
    /// similarity is at least <paramref name="threshold"/>. Returns groups of indices.
    /// </summary>
    public static List<List<int>> Cluster(IReadOnlyList<float[]> prints, double threshold)
    {
        int n = prints.Count;
        var groups = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        if (n < 2) return groups;
        var sim = new double[n, n];
        Parallel.For(0, n, i =>
        {
            for (int j = i + 1; j < n; j++) sim[i, j] = sim[j, i] = Voiceprints.Similarity(prints[i], prints[j]);
        });
        var alive = Enumerable.Range(0, n).ToList();
        // Best partner of each group, kept up to date (so each join is O(n), not O(n²)).
        var bestOf = new int[n];
        void Refresh(int i)
        {
            int best = -1;
            double b = double.NegativeInfinity;
            foreach (var j in alive) if (j != i && sim[i, j] > b) { b = sim[i, j]; best = j; }
            bestOf[i] = best;
        }
        foreach (var i in alive) Refresh(i);
        while (alive.Count > 1)
        {
            int a = -1;
            double top = double.NegativeInfinity;
            foreach (var i in alive) if (bestOf[i] >= 0 && sim[i, bestOf[i]] > top) { top = sim[i, bestOf[i]]; a = i; }
            if (a < 0 || top < threshold) break;
            int b = bestOf[a];
            int na = groups[a].Count, nb = groups[b].Count;
            foreach (var k in alive)
            {
                if (k == a || k == b) continue;
                sim[a, k] = sim[k, a] = (na * sim[a, k] + nb * sim[b, k]) / (na + nb);
            }
            groups[a].AddRange(groups[b]);
            alive.Remove(b);
            foreach (var k in alive)
                if (k == a || bestOf[k] == a || bestOf[k] == b) Refresh(k);
                else if (sim[k, a] > sim[k, bestOf[k]]) bestOf[k] = a;
        }
        return alive.Select(i => groups[i]).ToList();
    }

    /// <summary>Groups of fewer than 3 lines join the most alike group of 3 or more, when alike enough.</summary>
    private static List<List<int>> JoinSmall(List<List<int>> groups, float[]?[] prints)
    {
        var big = groups.Where(g => g.Count >= 3).ToList();
        if (big.Count == 0) return groups;
        var centroids = big.Select(g => Voiceprints.Mean(g.Select(i => prints[i]!))).ToList();
        var result = big.Select(g => new List<int>(g)).ToList();
        foreach (var small in groups.Where(g => g.Count < 3))
        {
            var c = Voiceprints.Mean(small.Select(i => prints[i]!));
            int best = -1;
            double b = double.NegativeInfinity;
            for (int k = 0; k < centroids.Count; k++)
            {
                var s = Voiceprints.Similarity(c, centroids[k]);
                if (s > b) { b = s; best = k; }
            }
            if (best >= 0 && b >= SmallGroupJoins) result[best].AddRange(small);
            else result.Add(new List<int>(small));
        }
        return result;
    }

    /// <summary>
    /// A voice for each speaker without one: of their gender (female when unknown and the pitch is missing),
    /// best voices to the most talkative, no voice used twice until all of that gender are used.
    /// </summary>
    public static void AssignVoices(IReadOnlyList<Speaker> speakers)
    {
        var used = new HashSet<string>(speakers.Where(s => s.Voice.Length > 0).Select(s => s.Voice));
        foreach (var s in speakers.Where(s => s.Voice.Length == 0).OrderByDescending(s => s.Lines.Count))
        {
            var pool = s.Gender == "male" ? MaleVoices : FemaleVoices;
            var voice = pool.FirstOrDefault(v => !used.Contains(v));
            if (voice is null)
            {
                // All used: the least used one of that gender.
                var counts = pool.ToDictionary(v => v, v => speakers.Count(o => o.Voice == v));
                voice = pool.OrderBy(v => counts[v]).First();
            }
            s.Voice = voice;
            used.Add(voice);
        }
    }

    /// <summary>A voice of the other gender when the gender is corrected (the next unused one).</summary>
    public static string VoiceForGender(string gender, IReadOnlyList<Speaker> speakers, Speaker except)
    {
        var pool = gender == "male" ? MaleVoices : FemaleVoices;
        var used = speakers.Where(s => s != except).Select(s => s.Voice).ToHashSet();
        return pool.FirstOrDefault(v => !used.Contains(v)) ?? pool[0];
    }
}
