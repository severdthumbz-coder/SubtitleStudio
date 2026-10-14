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

    /// <summary>Voice chosen by hand (kept even when it's of the other gender or another speaker has it).</summary>
    public bool VoiceSetByHand { get; set; }

    /// <summary>The speaker's lines (the subtitles' own cue objects).</summary>
    public List<SubtitleCue> Lines { get; } = new();

    /// <summary>Average voiceprint of the lines (for recognising them in the next episode).</summary>
    public float[]? Voiceprint { get; set; }

    /// <summary>Recognised from an earlier episode of the show.</summary>
    public bool Remembered { get; set; }

    /// <summary>
    /// The pitch sits in the band where male and female overlap (a higher-voiced man, a lower-voiced woman), so the
    /// gender guessed from it could be wrong. Worth the user confirming. Cleared once the gender is set by hand.
    /// </summary>
    public bool GenderUncertain { get; set; }

    public TimeSpan SpeechTime => TimeSpan.FromTicks(Lines.Sum(l => (l.End - l.Start).Ticks));
}

/// <summary>What Find speakers did.</summary>
/// <param name="Steps">How the grouping went, for the Log: "212 reliable lines → 31 groups → 24 after joining alike → 12 characters".</param>
public sealed record SpeakerResult(List<Speaker> Speakers, int Measured, int ByNeighbour, int Remembered, string Steps = "");

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

    /// <summary>Lines at least this long have voiceprints reliable enough to form the groups.</summary>
    public static readonly TimeSpan ReliableLine = TimeSpan.FromSeconds(1.2);

    /// <summary>Two groups whose average voiceprints are this alike, with pitch this close, are joined.</summary>
    public const double SameGroup = 0.5;
    public const double SamePitchHz = 40;

    /// <summary>
    /// A group is a character from this many lines, or this much speech (less in short videos: 1% of the lines,
    /// 2% of the speech).
    /// </summary>
    public const int CharacterLines = 5;

    /// <summary>A smaller group this alike a character (average voiceprints) is that character.</summary>
    public const double SameCharacter = 0.4;

    /// <summary>
    /// A smaller group like no character, with at least this many lines and this much speech, is a minor speaker
    /// of its own (the driver with two lines); smaller still, it goes to the most alike character of its gender.
    /// </summary>
    public const int MinorLines = 2;
    public static readonly TimeSpan MinorSpeech = TimeSpan.FromSeconds(3);

    /// <summary>At most this many speakers: the ones who speak least are given to the most alike of these.</summary>
    public const int MostSpeakers = 24;
    public static readonly TimeSpan CharacterSpeech = TimeSpan.FromSeconds(12);

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

        // Group the lines whose voiceprints are reliable (1.2 s or more), join groups that are clearly the same
        // person, keep the groups big enough to be a character, and give every other measured line to the most
        // alike of those. (On E03, grouping every line alone gave 47 speakers, 15 of them with a single line.)
        var measured = Enumerable.Range(0, lines.Count).Where(i => prints[i] is not null).ToList();
        var reliable = measured.Where(i => lines[i].End - lines[i].Start >= ReliableLine).ToList();
        if (reliable.Count < 3) reliable = measured;
        var groups = Cluster(reliable.Select(i => prints[i]!).ToList(), SameSpeaker)
            .Select(g => g.Select(k => reliable[k]).ToList()).ToList();
        int clustered = groups.Count;
        groups = MergeAlike(groups, prints, pitches);
        int merged = groups.Count;
        groups = KeepCharacters(groups, measured, lines, prints, pitches, out int main, out int minor);
        var steps = $"{reliable.Count} lines of 1.2 s or more grouped into {clustered}; {merged} after joining groups that sound alike; "
            + $"{main} big enough to be characters, {minor} minor speaker{(minor == 1 ? "" : "s")} (at most {MostSpeakers} in all), "
            + "the other lines given to the speaker of their gender they sound most like";
        progress?.Report(0.95);

        // Lines without a voiceprint (too short, under 1.2 s): the speaker of the nearest line in time, but a short line
        // with its own clear pitch prefers the nearest neighbour whose speaker is the same gender. Without this a short
        // line in a fast man/woman exchange took whichever neighbour was closest in time and so the wrong gender:
        // on E03 a man's "My son." and "Nice to meet you." (each under a second) got the woman's voice.
        var owner = new int[lines.Count];
        Array.Fill(owner, -1);
        for (int g = 0; g < groups.Count; g++) foreach (var i in groups[g]) owner[i] = g;
        var groupGender = groups.Select(g => Pitch.GenderOf(Pitch.Median(
            g.Where(i => pitches[i] is not null).SelectMany(i => pitches[i]!).ToList()))).ToList();
        int byNeighbour = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (owner[i] >= 0) continue;
            // The line's own gender, when it has enough pitch to tell (a quick exclamation over music often doesn't).
            var ownGender = pitches[i] is { } p && p.Count >= MinPitchFrames ? Pitch.GenderOf(Pitch.Median(new List<double>(p))) : null;
            int best = -1, bestSameGender = -1;
            TimeSpan bestGap = TimeSpan.MaxValue, bestSameGap = TimeSpan.MaxValue;
            for (int d = 1; d < lines.Count && (best < 0 || d < 4); d++)
            {
                foreach (var j in new[] { i - d, i + d })
                {
                    if (j < 0 || j >= lines.Count || prints[j] is null) continue;
                    var gap = j < i ? lines[i].Start - lines[j].End : lines[j].Start - lines[i].End;
                    if (gap < bestGap) { bestGap = gap; best = j; }
                    if (ownGender is not null && groupGender[owner[j]] == ownGender && gap < bestSameGap) { bestSameGap = gap; bestSameGender = j; }
                }
            }
            // The same-gender neighbour when there is one within a reasonable gap; else the nearest.
            int pick = bestSameGender >= 0 && bestSameGap <= TimeSpan.FromSeconds(3) ? bestSameGender : best;
            if (pick >= 0) { owner[i] = owner[pick]; byNeighbour++; }
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
            s.GenderUncertain = Pitch.GenderUncertain(s.PitchHz);
            s.Voiceprint = groups[g].Count > 0 ? Voiceprints.Mean(groups[g].Select(i => prints[i]!)) : null;
            speakers.Add(s);
        }
        speakers = speakers.Where(s => s.Lines.Count > 0).OrderByDescending(s => s.Lines.Count).ToList();
        for (int k = 0; k < speakers.Count; k++) speakers[k] = Renumber(speakers[k], k + 1);

        int remembered = book?.Recognise(speakers) ?? 0;
        AssignVoices(speakers);
        progress?.Report(1);
        return new SpeakerResult(speakers, measured.Count, byNeighbour, remembered, steps);
    }

    private static Speaker Renumber(Speaker s, int id)
    {
        var r = new Speaker { Id = id, Name = $"Speaker {id}", Gender = s.Gender, PitchHz = s.PitchHz, Voiceprint = s.Voiceprint, GenderUncertain = s.GenderUncertain };
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

    /// <summary>
    /// Joins groups whose average voiceprints are this alike and whose pitch is close: the same person split
    /// in two (their voice in a quiet scene and over music, say).
    /// </summary>
    private static List<List<int>> MergeAlike(List<List<int>> groups, float[]?[] prints, List<double>?[] pitches)
    {
        var result = groups.Select(g => new List<int>(g)).ToList();
        while (true)
        {
            var centroids = result.Select(g => Voiceprints.Mean(g.Select(i => prints[i]!))).ToList();
            var hz = result.Select(g => Pitch.Median(g.Where(i => pitches[i] is not null).SelectMany(i => pitches[i]!).ToList())).ToList();
            int a = -1, b = -1;
            double best = SameGroup;
            for (int x = 0; x < result.Count; x++)
                for (int y = x + 1; y < result.Count; y++)
                {
                    if (hz[x] is { } px && hz[y] is { } py && Math.Abs(px - py) > SamePitchHz) continue;
                    var sim = Voiceprints.Similarity(centroids[x], centroids[y]);
                    if (sim >= best) { best = sim; a = x; b = y; }
                }
            if (a < 0) return result;
            result[a].AddRange(result[b]);
            result.RemoveAt(b);
        }
    }

    /// <summary>
    /// Groups with enough lines or speech are the characters. A smaller group joins the character it sounds like
    /// (of the same gender); one like nobody, with a few lines, is a minor speaker of its own; the rest, and the
    /// short lines, go to the most alike speaker of their gender. (Build 65 gave every small group to the most
    /// alike character whatever the pitch: E03's driver, a man, was spoken by the lead woman.)
    /// </summary>
    private static List<List<int>> KeepCharacters(List<List<int>> groups, List<int> measured, List<SubtitleCue> lines, float[]?[] prints,
        List<double>?[] pitches, out int main, out int minor)
    {
        TimeSpan Speech(List<int> g) => TimeSpan.FromTicks(g.Sum(i => (lines[i].End - lines[i].Start).Ticks));
        string? GenderOf(IEnumerable<int> g)
        {
            var hz = g.Where(i => pitches[i] is not null).SelectMany(i => pitches[i]!).ToList();
            return hz.Count >= MinPitchFrames ? Pitch.GenderOf(Pitch.Median(hz)) : null;
        }
        // Short videos have fewer lines per person: the bar is 1% of the lines (2 to 5), or 2% of the speech (4 to 12 s).
        int minLines = Math.Clamp((int)Math.Round(measured.Count * 0.01), 2, CharacterLines);
        var minSpeech = TimeSpan.FromTicks(Math.Clamp((long)(Speech(measured).Ticks * 0.02), TimeSpan.FromSeconds(4).Ticks, CharacterSpeech.Ticks));
        var ordered = groups.OrderByDescending(Speech).ToList();
        var characters = ordered.Where(g => g.Count >= minLines || Speech(g) >= minSpeech).Take(MostSpeakers).Select(g => new List<int>(g)).ToList();
        main = characters.Count;
        minor = 0;
        if (characters.Count == 0)
        {
            main = groups.Count;
            return groups.Select(g => new List<int>(g)).ToList();
        }
        var centroids = characters.Select(g => Voiceprints.Mean(g.Select(i => prints[i]!))).ToList();
        var genders = characters.Select(GenderOf).ToList();

        // The most alike speaker of the same gender (any gender when none matches or the pitch can't tell).
        (int Best, double Sim) Nearest(float[] print, string? gender)
        {
            int best = -1, anyBest = -1;
            double b = double.NegativeInfinity, anyB = double.NegativeInfinity;
            for (int k = 0; k < centroids.Count; k++)
            {
                var sim = Voiceprints.Similarity(print, centroids[k]);
                if (sim > anyB) { anyB = sim; anyBest = k; }
                if ((gender is null || genders[k] is null || genders[k] == gender) && sim > b) { b = sim; best = k; }
            }
            return best >= 0 ? (best, b) : (anyBest, anyB);
        }

        var placed = new HashSet<int>(characters.SelectMany(g => g));
        foreach (var g in ordered.Where(g => !g.Any(placed.Contains)))
        {
            var centroid = Voiceprints.Mean(g.Select(i => prints[i]!));
            var gender = GenderOf(g);
            var (best, sim) = Nearest(centroid, gender);
            if (sim < SameCharacter && characters.Count < MostSpeakers && g.Count >= MinorLines && Speech(g) >= MinorSpeech)
            {
                characters.Add(new List<int>(g));
                centroids.Add(centroid);
                genders.Add(gender);
                minor++;
            }
            else characters[best].AddRange(g);
            foreach (var i in g) placed.Add(i);
        }
        foreach (var i in measured)
        {
            if (placed.Contains(i)) continue;
            characters[Nearest(prints[i]!, GenderOf(new[] { i })).Best].Add(i);
        }
        return characters;
    }

    /// <summary>Pitch readings (30 ms apart) needed before a line or group is taken as male or female.</summary>
    private const int MinPitchFrames = 5;

    /// <summary>"male" for am_/bm_ voices, "female" for af_/bf_.</summary>
    public static string VoiceGender(string voice) => voice.Length > 1 && voice[1] == 'm' ? "male" : "female";

    /// <summary>
    /// A voice for each speaker without one: of their gender (female when unknown and the pitch is missing),
    /// best voices to the most talkative, no voice used twice until all of that gender are used. A remembered
    /// voice of the other gender, or one a more talkative speaker has, is replaced (unless chosen by hand).
    /// </summary>
    public static void AssignVoices(IReadOnlyList<Speaker> speakers)
    {
        var kept = new HashSet<string>();
        foreach (var s in speakers.Where(s => s.Voice.Length > 0).OrderByDescending(s => s.VoiceSetByHand).ThenByDescending(s => s.Lines.Count))
        {
            if (s.VoiceSetByHand) { kept.Add(s.Voice); continue; }
            var pool = s.Gender == "male" ? MaleVoices : FemaleVoices;
            bool wrongGender = s.Gender is not null && VoiceGender(s.Voice) != s.Gender;
            bool taken = kept.Contains(s.Voice) && pool.Any(v => !kept.Contains(v));
            if (wrongGender || taken) s.Voice = string.Empty;
            else kept.Add(s.Voice);
        }
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
