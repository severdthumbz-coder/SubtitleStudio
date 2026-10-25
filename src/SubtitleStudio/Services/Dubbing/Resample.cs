namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Linear resampling of mono audio. Used when a voice engine's output rate differs from the voice track's rate (a
/// Piper overflow voice at 22.05 kHz written into a 24 kHz Kokoro track): each line is resampled to the track rate as
/// it's written, so the track file stays a single rate. Linear is enough here — speech, short lines, and the FFmpeg
/// mix stage resamples again to 48 kHz downstream, so this only has to be clean, not pristine.
/// </summary>
public static class Resample
{
    /// <summary>Resample mono samples from <paramref name="fromRate"/> to <paramref name="toRate"/> (returns the same array when equal).</summary>
    public static float[] Linear(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate || samples.Length == 0) return samples;
        long outLen = (long)samples.Length * toRate / fromRate;
        if (outLen <= 0) return Array.Empty<float>();
        var outp = new float[outLen];
        double step = (double)fromRate / toRate;
        for (long i = 0; i < outLen; i++)
        {
            double pos = i * step;
            int j = (int)pos;
            double f = pos - j;
            float a = samples[j];
            float b = j + 1 < samples.Length ? samples[j + 1] : a;
            outp[i] = (float)(a * (1 - f) + b * f);
        }
        return outp;
    }
}
