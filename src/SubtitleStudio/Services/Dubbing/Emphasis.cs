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
        // Heavy smoothing so the shape is slow: emphasis is the arc of a line (which words are pushed), not the
        // syllable-by-syllable flicker of the energy. Several passes of a 3-point average turn the 16 raw points into
        // a gentle curve; without this, big point-to-point swings (a separated voice has pauses and artifacts) would
        // modulate the English voice a few times a second and be heard as a rough ringing rather than emphasis.
        var s = (double[])e.Clone();
        for (int pass = 0; pass < 4; pass++)
        {
            var t = (double[])s.Clone();
            for (int p = 0; p < Points; p++)
                s[p] = (t[Math.Max(0, p - 1)] + 2 * t[p] + t[Math.Min(Points - 1, p + 1)]) / 4;
        }
        double avg = s.Average();
        if (avg <= 1e-6) return null; // essentially silent: nothing to follow
        var env = new float[Points];
        for (int p = 0; p < Points; p++) env[p] = (float)(s[p] / avg);
        return env;
    }

    /// <summary>The gain is slewed with a one-pole smoother of about this long, so it can never move fast enough to be heard as a wobble.</summary>
    public const double SlewMs = 80;

    /// <summary>The sample rate the speech is at (Kokoro's), for the slew time constant.</summary>
    public const int Rate = 24000;

    /// <summary>
    /// Apply a loudness shape to a line's speech: the envelope (gains relative to average) is stretched over the
    /// speech, limited to ±<see cref="MostDb"/> dB, and used as a per-sample gain — but the gain is slewed with a
    /// one-pole smoother (<see cref="SlewMs"/>) so it rises and falls gently and can never modulate the voice fast
    /// enough to be heard as a ringing. The overall loudness is unchanged (the shape averages to 1), so this only
    /// moves energy within the line. A null or flat envelope, or speech too short, returns the same samples.
    /// </summary>
    public static float[] Apply(float[] speech, float[]? envelope)
    {
        if (envelope is null || envelope.Length < 2 || speech.Length < 16) return speech;
        double limit = Math.Pow(10, MostDb / 20); // linear cap, e.g. +4 dB ≈ 1.585
        double a = Math.Exp(-1.0 / (SlewMs / 1000.0 * Rate)); // one-pole coefficient for the slew
        var outp = new float[speech.Length];
        bool moved = false;
        double gslew = 1; // start at unity so the first samples aren't jerked
        for (int i = 0; i < speech.Length; i++)
        {
            // Position 0..1 along the line → a point in the envelope, linearly interpolated.
            double pos = (double)i / (speech.Length - 1) * (envelope.Length - 1);
            int p = (int)pos; double f = pos - p;
            double g = p + 1 < envelope.Length ? envelope[p] * (1 - f) + envelope[p + 1] * f : envelope[p];
            g = Math.Clamp(g, 1 / limit, limit);
            gslew = a * gslew + (1 - a) * g; // slew-limit: the applied gain can only change slowly
            if (Math.Abs(gslew - 1) > 0.02) moved = true;
            outp[i] = (float)(speech[i] * gslew);
        }
        return moved ? outp : speech;
    }
}
