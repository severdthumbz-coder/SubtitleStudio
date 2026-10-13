using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Transcription;

/// <summary>The voices alone, separated from the music and effects (Dubbing's VoiceSeparator), for Whisper to listen to.</summary>
/// <param name="Path">A sound file FFmpeg can read.</param>
/// <param name="CoversWholeFile">The file starts where the video does (the episode's, kept for dubbing); else it starts at the part asked for.</param>
/// <param name="Temporary">Delete it once read.</param>
public sealed record VoicesSource(string Path, bool CoversWholeFile, bool Temporary);

/// <param name="Progress">0..1 of the separation (not reported when the voices were kept from before).</param>
public sealed record VoicesRequest(string MediaPath, int Track, TimeSpan? Start, TimeSpan? Length, TimeSpan? MediaDuration, IProgress<double>? Progress);

/// <summary>
/// Hesitations in Whisper's prompt: written the way they should come out, so Whisper writes them down
/// ("음... 어, 그게") instead of leaving them out as it usually does. Given to every 30 s window, only for the
/// language it's in.
/// </summary>
public static class Hesitations
{
    /// <remarks>
    /// Short, with full stops: on the test scene (English, Whisper base) a longer prompt in running sentences made
    /// Whisper join lines and put a word in the wrong line; this one kept the lines apart better, though it still
    /// joined two speakers' lines once. So it's a choice, off unless asked for.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Prompts { get; } = new Dictionary<string, string>
    {
        ["ko"] = "음. 어, 네. 아.",
        ["en"] = "Um. Uh, yeah. Hmm.",
        ["ja"] = "えーと。あの、うん。",
        ["zh"] = "嗯。呃，对。",
    };

    /// <summary>
    /// Whisper sometimes writes its prompt back out over silence: a segment whose letters are most of the prompt
    /// (at least 3 of them, and 60% of the prompt's) is that echo. A single "음" or "uh" is a real line and kept.
    /// </summary>
    public static bool IsEcho(string text, IReadOnlyDictionary<string, string>? prompts)
    {
        if (prompts is null) return false;
        var key = TranscriptShaper.Key(text);
        return prompts.Values.Select(TranscriptShaper.Key).Any(p => key.Length >= Math.Max(3, p.Length * 0.6) && p.Contains(key, StringComparison.Ordinal));
    }
}

/// <summary>
/// After Whisper's first listen: speech the speech detector heard that no line covers (people in the background,
/// a line Whisper skipped, a hesitation before a line) is listened to again on its own, each stretch by itself so
/// louder speech nearby can't drown it out. Singing is left alone (SingingCheck). A line heard again that
/// Whisper had already written somewhere else nearby, over a stretch with no speech, is moved to where it's
/// really spoken (Whisper's times can jump to the wrong stretch of speech when stretches are joined for it).
/// </summary>
public static class ListenAgain
{
    /// <summary>Uncovered speech at least this long is listened to again.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(0.6);

    /// <summary>Lines are taken to cover this much more on each side than Whisper's times say.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(0.15);

    /// <summary>Uncovered stretches closer than this are listened to together; longer ones are cut to at most Longest.</summary>
    public static readonly TimeSpan Join = TimeSpan.FromSeconds(0.5), Longest = TimeSpan.FromSeconds(25);

    /// <summary>Sound added on each side of a stretch for Whisper.</summary>
    public static readonly TimeSpan Pad = TimeSpan.FromSeconds(0.25);

    /// <summary>A line already written this far away, or nearer, may be the same one heard in the wrong place.</summary>
    public static readonly TimeSpan MoveWithin = TimeSpan.FromSeconds(120);

    /// <summary>Speech the detector heard that no segment covers: stretches of at least Shortest, joined and cut for Whisper.</summary>
    public static List<(TimeSpan Start, TimeSpan End)> Uncovered(IReadOnlyList<SpeechRegion> speech, IEnumerable<(TimeSpan Start, TimeSpan End)> lines)
    {
        var covered = lines.Select(l => (Start: l.Start - Slack, End: l.End + Slack)).OrderBy(l => l.Start).ToList();
        var left = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var r in speech.OrderBy(r => r.Start))
        {
            var from = r.Start;
            foreach (var c in covered)
            {
                if (c.End <= from) continue;
                if (c.Start >= r.End) break;
                if (c.Start > from) left.Add((from, c.Start));
                from = c.End > from ? c.End : from;
                if (from >= r.End) break;
            }
            if (from < r.End) left.Add((from, r.End));
        }
        var joined = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var piece in left.Where(p => p.End - p.Start >= TimeSpan.FromSeconds(0.2)).OrderBy(p => p.Start))
        {
            if (joined.Count > 0 && piece.Start - joined[^1].End < Join && piece.End - joined[^1].Start <= Longest)
                joined[^1] = (joined[^1].Start, piece.End);
            else joined.Add(piece);
        }
        var result = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var (s, e) in joined.Where(p => p.End - p.Start >= Shortest))
            for (var at = s; at < e; at += Longest)
                result.Add((at, at + Longest < e ? at + Longest : e));
        return result.Where(p => p.End - p.Start >= Shortest).ToList();
    }

    /// <summary>What the second listen did with what it heard.</summary>
    public sealed record MergeResult(int Added, int Moved, int Same, IReadOnlyList<string> Notes);

    /// <summary>
    /// Folds what was heard again into the segments: a line not written anywhere near is added; one already written
    /// within MoveWithin is the same line, moved here when where it was written has (almost) no speech, else left.
    /// </summary>
    public static MergeResult Merge(List<WhisperSegment> segments, IEnumerable<WhisperSegment> heard, IReadOnlyList<SpeechRegion> speech)
    {
        int added = 0, moved = 0, same = 0;
        var notes = new List<string>();
        foreach (var h in heard)
        {
            var text = TranscriptShaper.Clean(h.Text);
            int twin = segments.FindIndex(x => (x.Start - h.Start).Duration() <= MoveWithin && Alike(x.Text, text) >= 0.8);
            if (twin < 0)
            {
                segments.Add(h);
                added++;
                notes.Add($"added \"{text}\" at {h.Start.TotalSeconds:0.0} s");
                continue;
            }
            var old = segments[twin];
            if (SpeechShare(speech, old.Start, old.End) < 0.3)
            {
                segments[twin] = h with { Chunk = old.Chunk };
                moved++;
                notes.Add($"moved \"{text}\" from {old.Start.TotalSeconds:0.0} s, where nobody speaks, to {h.Start.TotalSeconds:0.0} s");
            }
            else same++;
        }
        segments.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new MergeResult(added, moved, same, notes);
    }

    /// <summary>The share of a stretch the speech detector heard as speech.</summary>
    public static double SpeechShare(IReadOnlyList<SpeechRegion> speech, TimeSpan start, TimeSpan end)
    {
        if (end <= start) return 0;
        double inside = 0;
        foreach (var r in speech)
        {
            var a = r.Start > start ? r.Start : start;
            var b = r.End < end ? r.End : end;
            if (b > a) inside += (b - a).TotalSeconds;
        }
        return inside / (end - start).TotalSeconds;
    }

    /// <summary>How alike two lines' letters are (0..1, edit distance), for "the same line heard twice".</summary>
    public static double Alike(string a, string b)
    {
        a = TranscriptShaper.Key(a);
        b = TranscriptShaper.Key(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1 - prev[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    /// <summary>
    /// Cue starts moved back to where the voice starts, when the speech detector heard it begin a little earlier (a
    /// breath, an "uh", the first quiet syllable): at most <paramref name="most"/> back, never into the previous cue.
    /// </summary>
    public static int SnapStarts(IList<SubtitleCue> cues, IReadOnlyList<SpeechRegion> speech, TimeSpan offset, TimeSpan most)
    {
        int moved = 0;
        var regions = speech.Select(r => (Start: r.Start + offset, End: r.End + offset)).OrderBy(r => r.Start).ToList();
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            var region = regions.LastOrDefault(r => r.Start <= c.Start && r.End > c.Start);
            if (region == default || region.Start >= c.Start) continue;
            var onset = region.Start;
            if (c.Start - onset > most) continue;
            // The voice must start after the previous cue: one stretch of speech running on from it is that line still.
            var previousEnd = i > 0 ? cues[i - 1].End : TimeSpan.Zero;
            if (onset < previousEnd) continue;
            if (c.Start - onset < TimeSpan.FromMilliseconds(80)) continue;
            c.Start = onset;
            moved++;
        }
        return moved;
    }
}
