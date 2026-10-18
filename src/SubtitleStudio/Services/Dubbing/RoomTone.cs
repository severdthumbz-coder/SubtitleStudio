namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Makes a voice sit back in a room, for a line the original voice was distant on (VoicePlacement.Shape). Two things
/// carry distance to the ear: the highs fade (air and walls absorb them), and a little reverberation arrives after
/// the direct sound (the room answering). Both are applied here, in the app, with no outside tool — a one-pole
/// low-pass for the highs and a small Schroeder reverb (four comb filters into two allpass filters) for the room.
/// Everything is scaled by a distance of 0 to 1, so a voice only slightly back gets a touch and a far one gets more;
/// the overall level is left to VoicePlacement, which already follows the original voice's loudness.
/// </summary>
public static class RoomTone
{
    // Classic Schroeder comb/allpass delays (ms), a small, neutral room. Scaled to the sample rate.
    private static readonly double[] CombMs = { 29.7, 37.1, 41.1, 43.7 };
    private static readonly double[] AllpassMs = { 5.0, 1.7 };

    /// <param name="distance">0 (unchanged) to 1 (a distant voice): more reverb, more of the highs rolled off.</param>
    public static float[] Apply(float[] mono, int rate, double distance)
    {
        distance = Math.Clamp(distance, 0, 1);
        if (distance <= 0 || mono.Length < 16) return mono;

        // Highs: a one-pole low-pass that drops from ~8 kHz (barely touched) to ~2.5 kHz (clearly distant).
        double cutoff = 8000 - 5500 * distance;
        double a = Math.Exp(-2 * Math.PI * cutoff / rate);
        var low = new float[mono.Length];
        double lp = 0;
        for (int i = 0; i < mono.Length; i++) { lp = a * lp + (1 - a) * mono[i]; low[i] = (float)lp; }

        // Room: the reverb of the low-passed voice (the room is darker than the direct sound too).
        var wet = Reverb(low, rate);

        // Mix: the wet share grows with distance (up to ~35%), and the dry voice dims a little as it recedes.
        double wetMix = 0.35 * distance, dryMix = 1 - 0.25 * distance;
        var outp = new float[mono.Length];
        for (int i = 0; i < mono.Length; i++)
            outp[i] = (float)(dryMix * low[i] + wetMix * wet[i]);
        return outp;
    }

    private static float[] Reverb(float[] x, int rate)
    {
        var acc = new float[x.Length];
        foreach (var ms in CombMs)
        {
            var c = Comb(x, (int)(ms * rate / 1000.0), 0.78f);
            for (int i = 0; i < acc.Length; i++) acc[i] += c[i] / CombMs.Length;
        }
        foreach (var ms in AllpassMs)
            acc = Allpass(acc, (int)(ms * rate / 1000.0), 0.7f);
        return acc;
    }

    private static float[] Comb(float[] x, int delay, float feedback)
    {
        delay = Math.Max(1, delay);
        var y = new float[x.Length];
        var buf = new float[delay];
        int p = 0;
        for (int i = 0; i < x.Length; i++)
        {
            float d = buf[p];
            float v = x[i] + feedback * d;
            buf[p] = v;
            y[i] = d;
            if (++p >= delay) p = 0;
        }
        return y;
    }

    private static float[] Allpass(float[] x, int delay, float g)
    {
        delay = Math.Max(1, delay);
        var y = new float[x.Length];
        var buf = new float[delay];
        int p = 0;
        for (int i = 0; i < x.Length; i++)
        {
            float d = buf[p];
            float v = x[i] + (-g) * d;
            buf[p] = v;
            y[i] = d + g * v;
            if (++p >= delay) p = 0;
        }
        return y;
    }
}
