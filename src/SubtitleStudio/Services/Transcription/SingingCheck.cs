namespace SubtitleStudio.Services.Transcription;

/// <summary>What the pitch of a stretch of voice says about it.</summary>
/// <param name="Voiced">Share of the sounding 30 ms steps that have a pitch.</param>
/// <param name="Held">Share of the voiced time spent on notes held steady for 250 ms or more (within half a semitone).</param>
/// <param name="LongRuns">Share of the voiced time in unbroken voiced runs of 600 ms or more.</param>
public sealed record VoiceShape(double Voiced, double Held, int Steps, double LongRuns = 0);

/// <summary>
/// Tells singing from speech by the pitch (YIN, 30 ms steps): singing holds notes, at a steady pitch for a
/// quarter of a second and more, for much of its voiced time; speech glides from syllable to syllable and
/// rarely holds a pitch that long. Used on the separated voices, where the music no longer muddles the pitch,
/// so the second listen doesn't write song lyrics as dialogue.
/// </summary>
public static class SingingCheck
{
    private const int Window = 320, Hop = 240; // 40 ms windows every 30 ms, at 8 kHz
    private const double MinHz = 70, MaxHz = 1000, Threshold = 0.15;

    /// <summary>
    /// Singing: mostly voiced (at least SingingVoiced of the sounding steps), and either this share of the voiced time
    /// on held notes or this share in long unbroken voiced runs. Measured: a sung passage (sherpa-onnx's test song, its
    /// separated voice, 4 s windows) 5 of 6 windows; speech none: Kokoro lines (quiet and loud) and a real four-person
    /// recording held at most 16% with no long runs, and a slow, even audiobook reading, which holds its pitch up to
    /// 52% of the time, is voiced only 29–60% of the time (singing 75–97%).
    /// </summary>
    public const double SingingVoiced = 0.7, SingingHeld = 0.3, SingingRuns = 0.2;

    /// <summary>A stretch shorter than this (or with fewer voiced steps) isn't judged.</summary>
    public const int MinVoicedSteps = 20;

    public static bool IsSinging(ReadOnlySpan<float> samples, int rate = AudioExtractor.SampleRate)
    {
        var shape = Measure(samples, rate);
        return shape.Steps >= MinVoicedSteps && shape.Voiced >= SingingVoiced && (shape.Held >= SingingHeld || shape.LongRuns >= SingingRuns);
    }

    public static VoiceShape Measure(ReadOnlySpan<float> input, int rate = AudioExtractor.SampleRate)
    {
        var pitch = Contour(input, rate);
        int voiced = pitch.Count(p => p is > 0);
        int nonQuiet = pitch.Count(p => p is not null);
        if (voiced == 0) return new VoiceShape(0, 0, 0);
        // Runs of steps whose pitch stays within a semitone of the run's average so far (a held note, vibrato allowed).
        int held = 0, longRuns = 0;
        for (int i = 0; i < pitch.Length;)
        {
            if (pitch[i] is not > 0) { i++; continue; }
            double sum = Math.Log2(pitch[i]!.Value);
            int j = i + 1;
            while (j < pitch.Length && pitch[j] is > 0 && Math.Abs(12 * (Math.Log2(pitch[j]!.Value) - sum / (j - i))) < 1.0) { sum += Math.Log2(pitch[j]!.Value); j++; }
            if ((j - i) * Hop >= 0.25 * 8000) held += j - i;
            i = j;
        }
        for (int i = 0; i < pitch.Length;)
        {
            if (pitch[i] is not > 0) { i++; continue; }
            int j = i;
            while (j < pitch.Length && pitch[j] is > 0) j++;
            if ((j - i) * Hop >= 0.6 * 8000) longRuns += j - i;
            i = j;
        }
        return new VoiceShape(voiced / (double)Math.Max(1, nonQuiet), held / (double)voiced, voiced, longRuns / (double)voiced);
    }

    /// <summary>
    /// Pitch of each 30 ms step: a frequency where voiced, 0 where sounding but unvoiced, null where quiet
    /// (well below the loudest step).
    /// </summary>
    public static double?[] Contour(ReadOnlySpan<float> input, int inputRate = AudioExtractor.SampleRate)
    {
        int factor = Math.Max(1, inputRate / 8000);
        int n = input.Length / factor;
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int k = 0; k < factor; k++) sum += input[i * factor + k];
            x[i] = sum / factor;
        }
        int rate = inputRate / factor;
        int minLag = (int)(rate / MaxHz), maxLag = (int)(rate / MinHz);
        int steps = Math.Max(0, (n - Window - maxLag) / Hop + 1);
        var result = new double?[steps];
        if (steps == 0) return result;
        var energy = new double[steps];
        double loudest = 0;
        for (int s = 0; s < steps; s++)
        {
            double e = 0;
            for (int i = 0; i < Window; i++) e += x[s * Hop + i] * (double)x[s * Hop + i];
            energy[s] = e / Window;
            loudest = Math.Max(loudest, energy[s]);
        }
        var diff = new double[maxLag + 1];
        for (int s = 0; s < steps; s++)
        {
            if (energy[s] < loudest * 0.03) continue;
            int at = s * Hop;
            diff[0] = 1;
            double running = 0;
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double d = 0;
                for (int i = 0; i < Window; i++)
                {
                    double v = x[at + i] - x[at + i + lag];
                    d += v * v;
                }
                running += d;
                diff[lag] = running > 0 ? d * lag / running : 1;
            }
            int best = -1;
            for (int lag = Math.Max(2, minLag); lag <= maxLag; lag++)
            {
                if (diff[lag] < Threshold)
                {
                    while (lag + 1 <= maxLag && diff[lag + 1] < diff[lag]) lag++;
                    best = lag;
                    break;
                }
            }
            if (best < 0)
            {
                result[s] = 0;
                continue;
            }
            double lagF = best;
            if (best > 1 && best < maxLag)
            {
                double a = diff[best - 1], b = diff[best], c = diff[best + 1], den = a - 2 * b + c;
                if (Math.Abs(den) > 1e-12) lagF = best + 0.5 * (a - c) / den;
            }
            result[s] = rate / lagF;
        }
        return result;
    }
}
