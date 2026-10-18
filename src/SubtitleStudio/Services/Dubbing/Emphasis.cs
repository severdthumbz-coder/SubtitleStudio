namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Within-line emphasis: the shape of the original voice's loudness across a line — where the actor leans in and where
/// they ease off — carried onto the English voice that replaces it. It is measured from the separated voice as a short
/// curve of gains relative to the line's own average (so it's the shape, not the overall level, which VoicePlacement
/// already follows), then applied to the English speech as a gentle, capped, slowly-varying gain, stretched to the
/// English line's length. Kept subtle on purpose: a small swing adds life, a large one would pump or sound unnatural.
/// </summary>
public static class Emphasis
{
    /// <summary>Points in the stored envelope (start to end of the line).</summary>
    public const int Points = 16;

    /// <summary>The most the gain moves from the line's average, in dB (±): subtle, so emphasis reads as life, not pumping.</summary>
    public const double MostDb = 4;

    /// <summary>
    /// The loudness shape of a line, as <see cref="Points"/> gains relative to the line's average (1 = average). Taken
    /// from the voice energy in equal slices, lightly smoothed, then divided by the average. A line too short or too
    /// quiet to have a shape worth following returns null (the speech is left as Kokoro made it).
    /// </summary>
    public static float[]? Measure(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int rate)
    {
        if (left.Length < rate / 4) return null; // under ~0.25 s: too short to shape
        // Energy per slice (both sides together).
        var e = new double[Points];
        for (int p = 0; p < Points; p++)
        {
            int a = (int)((long)p * left.Length / Points), b = (int)((long)(p + 1) * left.Length / Points);
            double sum = 0;
            for (int i = a; i < b; i++) sum += (left[i] * (double)left[i] + right[i] * (double)right[i]) * 0.5;
            e[p] = Math.Sqrt(sum / Math.Max(1, b - a));
        }
        // Light 3-point smoothing so the shape is slow (emphasis, not syllable flicker).
        var s = new double[Points];
        for (int p = 0; p < Points; p++)
        {
            double a = e[Math.Max(0, p - 1)], c = e[Math.Min(Points - 1, p + 1)];
            s[p] = (a + 2 * e[p] + c) / 4;
        }
        double avg = s.Average();
        if (avg <= 1e-6) return null; // essentially silent: nothing to follow
        var env = new float[Points];
        for (int p = 0; p < Points; p++) env[p] = (float)(s[p] / avg);
        return env;
    }

    /// <summary>
    /// Apply a loudness shape to a line's speech: the envelope (gains relative to average) is stretched over the
    /// speech, limited to ±<see cref="MostDb"/> dB, and used as a smooth per-sample gain. The overall loudness is
    /// unchanged (the shape averages to 1), so this only moves energy within the line. A null or flat envelope, or
    /// speech too short, returns the same samples.
    /// </summary>
    public static float[] Apply(float[] speech, float[]? envelope)
    {
        if (envelope is null || envelope.Length < 2 || speech.Length < 16) return speech;
        double limit = Math.Pow(10, MostDb / 20); // linear cap, e.g. +4 dB ≈ 1.585
        var outp = new float[speech.Length];
        bool moved = false;
        for (int i = 0; i < speech.Length; i++)
        {
            // Position 0..1 along the line → a point in the envelope, linearly interpolated.
            double pos = (double)i / (speech.Length - 1) * (envelope.Length - 1);
            int p = (int)pos; double f = pos - p;
            double g = p + 1 < envelope.Length ? envelope[p] * (1 - f) + envelope[p + 1] * f : envelope[p];
            g = Math.Clamp(g, 1 / limit, limit);
            if (Math.Abs(g - 1) > 0.02) moved = true;
            outp[i] = (float)(speech[i] * g);
        }
        return moved ? outp : speech;
    }
}
