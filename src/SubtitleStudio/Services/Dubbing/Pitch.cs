namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// How high a voice is: the median pitch of the voiced parts of a stretch of speech (YIN). Adult male
/// voices mostly speak around 85–155 Hz, female around 165–255 Hz; the split used is 160 Hz. Music
/// under the speech can mislead it, so a speaker's pitch is the median over all of their lines, and it
/// can be corrected in the Speakers list.
/// </summary>
public static class Pitch
{
    public const double MaleBelowHz = 160;
    private const int Window = 320, Hop = 240;    // 40 ms windows every 30 ms, at 8 kHz
    private const int MaxSeconds = 4;              // a line's first 4 s are plenty
    private const double MinHz = 65, MaxHz = 400, Threshold = 0.15, LooseThreshold = 0.35;

    /// <summary>Pitch of each voiced 40 ms window (empty when nothing is voiced). Works at half the rate given.</summary>
    public static List<double> Track(ReadOnlySpan<float> input, int inputRate = 16000)
    {
        // Half the rate (pairs averaged, which also filters out the top): pitch is far below 4 kHz.
        int n = Math.Min(input.Length, inputRate * MaxSeconds) / 2;
        var half = new float[n];
        for (int i = 0; i < n; i++) half[i] = 0.5f * (input[2 * i] + input[2 * i + 1]);
        ReadOnlySpan<float> samples = half;
        int sampleRate = inputRate / 2;
        var result = new List<double>();
        int minLag = sampleRate / (int)MaxHz, maxLag = (int)(sampleRate / MinHz);
        if (samples.Length < Window + maxLag) return result;
        // Quiet windows (well below the loudest) are breaths and pauses, not voice.
        double loudest = 0;
        for (int s = 0; s + Window <= samples.Length; s += Hop) loudest = Math.Max(loudest, Energy(samples.Slice(s, Window)));
        var diff = new double[maxLag + 1];
        for (int s = 0; s + Window + maxLag <= samples.Length; s += Hop)
        {
            var frame = samples.Slice(s, Window + maxLag);
            if (Energy(frame[..Window]) < loudest * 0.05) continue;
            // Cumulative mean normalised difference.
            diff[0] = 1;
            double running = 0;
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double d = 0;
                for (int i = 0; i < Window; i++)
                {
                    double x = frame[i] - frame[i + lag];
                    d += x * x;
                }
                running += d;
                diff[lag] = running > 0 ? d * lag / running : 1;
            }
            int best = -1;
            for (int lag = minLag; lag <= maxLag; lag++)
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
                // No dip below the threshold (noisy or breathy speech): the deepest one, if it's clear enough.
                int deepest = minLag;
                for (int lag = minLag + 1; lag <= maxLag; lag++) if (diff[lag] < diff[deepest]) deepest = lag;
                if (diff[deepest] < LooseThreshold) best = deepest;
                else continue;
            }
            // Parabolic interpolation for a finer lag.
            double lagF = best;
            if (best > 1 && best < maxLag)
            {
                double a = diff[best - 1], b = diff[best], c = diff[best + 1], den = a - 2 * b + c;
                if (Math.Abs(den) > 1e-12) lagF = best + 0.5 * (a - c) / den;
            }
            result.Add(sampleRate / lagF);
        }
        return result;
    }

    /// <summary>The median pitch, or null with fewer than 5 voiced windows.</summary>
    public static double? Median(ReadOnlySpan<float> samples, int sampleRate = 16000)
    {
        var track = Track(samples, sampleRate);
        return Median(track);
    }

    public static double? Median(List<double> values)
    {
        if (values.Count < 5) return null;
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Energy(ReadOnlySpan<float> x)
    {
        double e = 0;
        foreach (var v in x) e += v * v;
        return e / Math.Max(1, x.Length);
    }

    public static string? GenderOf(double? hz) => hz is null ? null : hz < MaleBelowHz ? "male" : "female";
}
