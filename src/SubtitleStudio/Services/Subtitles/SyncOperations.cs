using SubtitleStudio.Models;
using SubtitleStudio.Services.Audio;

namespace SubtitleStudio.Services.Subtitles;

public sealed record FrameRatePreset(string Label, double SubtitleFps, double VideoFps)
{
    /// <summary>Times are multiplied by this: a subtitle timed for 25 fps on a 23.976 video must run slower.</summary>
    public double Factor => SubtitleFps / VideoFps;
}

public sealed record SnapResult(int StartsSnapped, int EndsSnapped, int Unchanged);

public sealed record OffsetEstimate(TimeSpan Offset, double Confidence);

/// <summary>
/// Timing corrections. All operations mutate the given cues in place and never produce negative
/// times; callers snapshot for undo first.
/// </summary>
public static class SyncOperations
{
    public static IReadOnlyList<FrameRatePreset> FrameRatePresets { get; } = new[]
    {
        new FrameRatePreset("Timed for 25 fps (PAL) → video 23.976 fps", 25, 24000.0 / 1001),
        new FrameRatePreset("Timed for 23.976 fps → video 25 fps (PAL)", 24000.0 / 1001, 25),
        new FrameRatePreset("Timed for 25 fps (PAL) → video 24 fps", 25, 24),
        new FrameRatePreset("Timed for 24 fps → video 25 fps (PAL)", 24, 25),
        new FrameRatePreset("Timed for 24 fps → video 23.976 fps", 24, 24000.0 / 1001),
        new FrameRatePreset("Timed for 23.976 fps → video 24 fps", 24000.0 / 1001, 24),
        new FrameRatePreset("Timed for 29.97 fps → video 25 fps (PAL)", 30000.0 / 1001, 25),
        new FrameRatePreset("Timed for 25 fps (PAL) → video 29.97 fps", 25, 30000.0 / 1001),
    };

    public static void Shift(IEnumerable<SubtitleCue> cues, TimeSpan offset)
    {
        foreach (var cue in cues)
        {
            var duration = cue.End - cue.Start;
            var start = cue.Start + offset;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            cue.Start = start;
            cue.End = start + (duration < TimeSpan.Zero ? TimeSpan.Zero : duration);
        }
    }

    /// <summary>Linear time-stretch around 0:00 (frame-rate drift fix): t' = t * factor.</summary>
    public static void Stretch(IEnumerable<SubtitleCue> cues, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) throw new ArgumentOutOfRangeException(nameof(factor));
        foreach (var cue in cues)
        {
            cue.Start = Scale(cue.Start, factor, TimeSpan.Zero);
            cue.End = Scale(cue.End, factor, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Two-point sync: maps <paramref name="sourceA"/> to <paramref name="targetA"/> and
    /// <paramref name="sourceB"/> to <paramref name="targetB"/>, and every other time linearly.
    /// Fixes offset and drift together.
    /// </summary>
    public static (double Scale, TimeSpan Offset) TwoPointSync(IEnumerable<SubtitleCue> cues, TimeSpan sourceA, TimeSpan targetA, TimeSpan sourceB, TimeSpan targetB)
    {
        var span = (sourceB - sourceA).TotalMilliseconds;
        if (Math.Abs(span) < 1000)
            throw new ArgumentException("The two anchor cues must be at least a second apart (ideally one near the start and one near the end).");

        double scale = (targetB - targetA).TotalMilliseconds / span;
        if (scale <= 0.5 || scale >= 2.0)
            throw new ArgumentException($"The anchors imply a speed change of {scale:0.###}x, which is not plausible. Check the anchor times.");

        var offset = targetA - TimeSpan.FromMilliseconds(sourceA.TotalMilliseconds * scale);
        foreach (var cue in cues)
        {
            cue.Start = Map(cue.Start);
            cue.End = Map(cue.End);
        }
        return (scale, offset);

        TimeSpan Map(TimeSpan t)
        {
            var mapped = TimeSpan.FromMilliseconds(t.TotalMilliseconds * scale) + offset;
            return mapped < TimeSpan.Zero ? TimeSpan.Zero : mapped;
        }
    }

    /// <summary>
    /// Moves each cue's start to the nearest speech onset and its end to the nearest speech offset
    /// within <paramref name="window"/>. Cues keep at least 300 ms, and an end never runs into the
    /// next cue's (new) start. Cues with no speech edge nearby are left alone.
    /// </summary>
    public static SnapResult SnapToSpeech(IReadOnlyList<SubtitleCue> cues, SpeechActivity activity, TimeSpan window)
    {
        int w = Math.Max(1, (int)(window.TotalMilliseconds / AudioEnvelope.FrameMilliseconds));
        var minLength = TimeSpan.FromMilliseconds(300);
        var gap = TimeSpan.FromMilliseconds(AudioEnvelope.FrameMilliseconds * 4);
        int starts = 0, ends = 0, unchanged = 0;

        var newStarts = cues.Select(c => Nearest(AudioEnvelope.FrameOf(c.Start), activity.IsOnset) is { } f ? AudioEnvelope.TimeOf(f) : c.Start).ToList();

        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            bool changed = false;

            if (newStarts[i] != cue.Start && newStarts[i] < cue.End - minLength)
            {
                cue.Start = newStarts[i];
                starts++;
                changed = true;
            }

            if (Nearest(AudioEnvelope.FrameOf(cue.End), activity.IsOffset) is { } endFrame)
            {
                var end = AudioEnvelope.TimeOf(endFrame);
                if (i + 1 < cues.Count && end > newStarts[i + 1] - gap && cues[i + 1].Start >= cue.Start)
                    end = newStarts[i + 1] - gap;
                if (end != cue.End && end >= cue.Start + minLength)
                {
                    cue.End = end;
                    ends++;
                    changed = true;
                }
            }

            if (!changed) unchanged++;
        }

        return new SnapResult(starts, ends, unchanged);

        int? Nearest(int center, Func<int, bool> isEdge)
        {
            for (int d = 0; d <= w; d++)
            {
                if (isEdge(center - d)) return center - d;
                if (d > 0 && isEdge(center + d)) return center + d;
            }
            return null;
        }
    }

    /// <summary>
    /// Finds the global shift that best lines up "subtitle on screen" with "speech in the audio",
    /// searching ±<paramref name="maxOffset"/>: a coarse 100 ms pass, then a 10 ms refinement.
    /// Confidence is how much better the best shift scores than a typical shift (0 = no signal).
    /// </summary>
    public static OffsetEstimate DetectOffset(IReadOnlyList<SubtitleCue> cues, SpeechActivity activity, TimeSpan maxOffset)
    {
        int n = activity.FrameCount;
        if (n == 0 || cues.Count == 0) return new OffsetEstimate(TimeSpan.Zero, 0);

        // +1 where speech, -0.35 where silence: rewards subtitles over speech, penalises them over silence.
        var speech = new double[n];
        for (int i = 0; i < n; i++) speech[i] = activity.Active[i] ? 1.0 : -0.35;

        var intervals = cues
            .Where(c => c.End > c.Start)
            .Select(c => (Start: AudioEnvelope.FrameOf(c.Start), End: AudioEnvelope.FrameOf(c.End)))
            .ToList();

        // Prefix sums make each shift O(cues) instead of O(frames).
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + speech[i];

        double Score(int shift)
        {
            double total = 0;
            foreach (var (s, e) in intervals)
            {
                int a = Math.Clamp(s + shift, 0, n), b = Math.Clamp(e + shift, 0, n);
                if (b > a) total += prefix[b] - prefix[a];
            }
            return total;
        }

        int maxFrames = (int)(maxOffset.TotalMilliseconds / AudioEnvelope.FrameMilliseconds);
        int best = 0;
        double bestScore = double.MinValue;
        var coarse = new List<double>();
        for (int shift = -maxFrames; shift <= maxFrames; shift += 10)
        {
            var score = Score(shift);
            coarse.Add(score);
            if (score > bestScore) { bestScore = score; best = shift; }
        }
        for (int shift = best - 10; shift <= best + 10; shift++)
        {
            var score = Score(shift);
            if (score > bestScore) { bestScore = score; best = shift; }
        }

        coarse.Sort();
        double median = coarse[coarse.Count / 2];
        double spread = Math.Max(1e-9, coarse[^1] - coarse[0]);
        double confidence = Math.Clamp((bestScore - median) / spread, 0, 1);
        return new OffsetEstimate(AudioEnvelope.TimeOf(best), confidence);
    }

    private static TimeSpan Scale(TimeSpan t, double factor, TimeSpan pivot)
    {
        var scaled = pivot + TimeSpan.FromMilliseconds((t - pivot).TotalMilliseconds * factor);
        return scaled < TimeSpan.Zero ? TimeSpan.Zero : scaled;
    }
}
