using SubtitleStudio.Services.Audio;

namespace SubtitleStudio.Services.Transcription;

/// <summary>A stretch of the source audio placed in a chunk (offsets and length in samples).</summary>
public sealed record AudioPiece(int ChunkOffset, long SourceOffset, int Length);

/// <summary>Audio handed to Whisper in one go: stretches of sound from the source, joined by short pauses.</summary>
public sealed class SpeechChunk
{
    public SpeechChunk(ReadOnlyMemory<float> audio, IReadOnlyList<AudioPiece> pieces)
    {
        Audio = audio;
        Pieces = pieces;
    }

    public ReadOnlyMemory<float> Audio { get; }
    public IReadOnlyList<AudioPiece> Pieces { get; }

    public TimeSpan SourceStart => Samples(Pieces[0].SourceOffset);
    public TimeSpan SourceEnd => Samples(Pieces[^1].SourceOffset + Pieces[^1].Length);

    /// <summary>
    /// A time in this chunk as a time in the source. In a joining pause, a start moves to the next stretch
    /// and an end to the previous one, so nothing is placed in the silence that was cut out.
    /// </summary>
    public TimeSpan ToSource(TimeSpan t, bool isEnd)
    {
        long s = (long)Math.Round(t.TotalSeconds * AudioExtractor.SampleRate);
        for (int i = 0; i < Pieces.Count; i++)
        {
            var p = Pieces[i];
            if (s < p.ChunkOffset)
                return isEnd && i > 0 ? Samples(Pieces[i - 1].SourceOffset + Pieces[i - 1].Length) : Samples(p.SourceOffset);
            if (s <= p.ChunkOffset + p.Length)
                return Samples(p.SourceOffset + (s - p.ChunkOffset));
        }
        return SourceEnd;
    }

    private static TimeSpan Samples(long n) => TimeSpan.FromSeconds(n / (double)AudioExtractor.SampleRate);
}

/// <param name="Active">Sound per 10 ms of the source (null when the audio had no dynamics to judge).</param>
public sealed record ChunkPlan(IReadOnlyList<SpeechChunk> Chunks, int Stretches, TimeSpan SilenceSkipped, bool[]? Active = null)
{
    public long SpeechSamples => Chunks.Sum(c => (long)c.Pieces.Sum(p => p.Length));
}

/// <summary>
/// Splits long audio into what Whisper handles best. Whisper works in 30-second windows; a long silence
/// inside one makes its timestamps drift across the gap and can make it skip or repeat the next sentence.
/// So quiet stretches of 2 seconds or more are cut out (found from the loudness, adapting to each file,
/// with a margin on both sides so soft speech next to louder speech is kept), and the rest is packed
/// into chunks of up to about 28 seconds. Times are mapped back to the source afterwards.
/// </summary>
public static class SpeechChunker
{
    private const int FrameSamples = AudioExtractor.SampleRate / 100; // 10 ms

    public sealed record Options
    {
        public TimeSpan MinSilence { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan Margin { get; init; } = TimeSpan.FromSeconds(0.4);
        public TimeSpan MaxChunk { get; init; } = TimeSpan.FromSeconds(28);
        public TimeSpan JoinPause { get; init; } = TimeSpan.FromSeconds(0.3);

        /// <summary>Where "sound" starts between the noise floor and loud speech (lower keeps more).</summary>
        public double Sensitivity { get; init; } = 0.2;

        /// <summary>With the speech detector: margin kept around its speech, and gaps shorter than this kept.</summary>
        public TimeSpan SpeechMargin { get; init; } = TimeSpan.FromSeconds(0.3);
        public TimeSpan SpeechGapKept { get; init; } = TimeSpan.FromSeconds(1.0);
    }

    /// <summary>
    /// Plans from the speech detector's regions: only speech goes to Whisper (music and effects
    /// between lines are left out). Without regions, or when it found none, falls back to loudness.
    /// </summary>
    public static ChunkPlan Plan(float[] samples, IReadOnlyList<SpeechRegion>? speech, Options? options = null)
    {
        var o = options ?? new Options();
        int frames = samples.Length / FrameSamples;
        if (speech is not { Count: > 0 } || frames < 100) return Plan(samples, o);

        var active = new bool[frames];
        var stretches = new List<(int Start, int End)>();
        int margin = Frames(o.SpeechMargin), keep = Frames(o.SpeechGapKept);
        foreach (var r in speech.OrderBy(r => r.Start))
        {
            int s0 = Math.Clamp(Frames(r.Start), 0, frames), s1 = Math.Clamp(Frames(r.End), 0, frames);
            if (s1 <= s0) continue;
            for (int f = s0; f < s1; f++) active[f] = true;
            int a = Math.Max(0, s0 - margin), b = Math.Min(frames, s1 + margin);
            if (stretches.Count > 0 && a - stretches[^1].End < keep)
                stretches[^1] = (stretches[^1].Start, Math.Max(stretches[^1].End, b));
            else
                stretches.Add((a, b));
        }
        if (stretches.Count == 0) return Plan(samples, o);
        return Pack(samples, stretches, frames, active, o);
    }

    public static ChunkPlan Plan(float[] samples, Options? options = null)
    {
        var o = options ?? new Options();
        int frames = samples.Length / FrameSamples;
        var whole = new ChunkPlan(new[] { new SpeechChunk(samples, new[] { new AudioPiece(0, 0, samples.Length) }) }, 1, TimeSpan.Zero);
        if (frames < 100) return whole;

        var db = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * FrameSamples, end = i + FrameSamples; i < end; i++) sum += samples[i] * samples[i];
            db[f] = (float)(10 * Math.Log10(Math.Max(sum / FrameSamples, 1e-10)));
        }
        var active = SpeechActivity.FromEnvelope(new AudioEnvelope(db), o.Sensitivity).Active;
        whole = whole with { Active = active.Any(a => a) ? active : null };

        // Stretches of sound, widened by the margin; gaps shorter than MinSilence are kept.
        int margin = Frames(o.Margin), minGap = Frames(o.MinSilence);
        var stretches = new List<(int Start, int End)>();
        for (int f = 0; f < frames;)
        {
            if (!active[f]) { f++; continue; }
            int s = f;
            while (f < frames && active[f]) f++;
            int a = Math.Max(0, s - margin), b = Math.Min(frames, f + margin);
            if (stretches.Count > 0 && a - stretches[^1].End < minGap)
                stretches[^1] = (stretches[^1].Start, Math.Max(stretches[^1].End, b));
            else
                stretches.Add((a, b));
        }
        if (stretches.Count == 0) return whole; // no dynamics at all: let Whisper judge

        long covered = stretches.Sum(s => (long)(s.End - s.Start));
        if (stretches.Count == 1 && covered >= frames - 1) return whole;
        return Pack(samples, stretches, frames, active, o);
    }

    /// <summary>Packs stretches (in 10 ms frames) into chunks of up to MaxChunk, joined by short pauses.</summary>
    private static ChunkPlan Pack(float[] samples, List<(int Start, int End)> stretches, int frames, bool[] active, Options o)
    {
        // The sample tail after the last whole frame belongs to the last stretch if it reaches the end.
        long Source(int frame) => (long)frame * FrameSamples;

        int join = (int)(o.JoinPause.TotalSeconds * AudioExtractor.SampleRate);
        long maxChunk = (long)(o.MaxChunk.TotalSeconds * AudioExtractor.SampleRate);
        var chunks = new List<SpeechChunk>();
        var group = new List<(long Start, int Length)>();
        long groupLength = 0;

        void Flush()
        {
            if (group.Count == 0) return;
            var audio = new float[groupLength];
            var pieces = new List<AudioPiece>(group.Count);
            int at = 0;
            foreach (var (start, length) in group)
            {
                Array.Copy(samples, start, audio, at, length);
                pieces.Add(new AudioPiece(at, start, length));
                at += length + join;
            }
            chunks.Add(new SpeechChunk(audio, pieces));
            group.Clear();
            groupLength = 0;
        }

        foreach (var (start, end) in stretches)
        {
            long from = Source(start);
            long to = end >= frames ? samples.Length : Source(end);
            int length = (int)(to - from);
            long added = (group.Count > 0 ? join : 0) + length;
            if (group.Count > 0 && groupLength + added > maxChunk) Flush();
            groupLength += (group.Count > 0 ? join : 0) + length;
            group.Add((from, length));
        }
        Flush();

        long speech = chunks.Sum(c => (long)c.Pieces.Sum(p => p.Length));
        return new ChunkPlan(chunks, stretches.Count, TimeSpan.FromSeconds((samples.Length - speech) / (double)AudioExtractor.SampleRate), active);
    }

    private static int Frames(TimeSpan t) => (int)Math.Round(t.TotalMilliseconds / 10);
}
