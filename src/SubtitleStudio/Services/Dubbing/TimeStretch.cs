namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Speech made shorter (or longer) without changing its pitch: WSOLA (waveform-similarity overlap-add).
/// The sound is cut into overlapping 43 ms pieces; each next piece is taken from near where the faster
/// clock says it should be, at the spot whose waveform best continues the previous piece, so the joins
/// don't click or warble. Used for the last few percent when a line still doesn't fit after Kokoro has
/// spoken it faster.
/// </summary>
public static class TimeStretch
{
    private const int Window = 1024;          // 43 ms at 24 kHz
    private const int SynthesisHop = Window / 2;
    private const int Tolerance = 256;         // ±11 ms search

    private static readonly float[] Hann = Enumerable.Range(0, Window).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Window))).ToArray();

    /// <param name="factor">How much faster: 1.1 makes it 10% shorter (output length = input / factor).</param>
    public static float[] Faster(float[] input, double factor)
    {
        if (factor is <= 0 or double.NaN) throw new ArgumentOutOfRangeException(nameof(factor));
        if (Math.Abs(factor - 1) < 0.005 || input.Length < Window * 2) return Resample(input, factor);
        int outLength = (int)Math.Round(input.Length / factor);
        var output = new float[outLength + Window];
        var weight = new float[outLength + Window];
        double analysisHop = SynthesisHop * factor;

        int previous = 0; // where the last piece was taken from
        for (int k = 0; ; k++)
        {
            int outPos = k * SynthesisHop;
            if (outPos >= outLength) break;
            int ideal = (int)Math.Round(k * analysisHop);
            int pos = ideal;
            if (k > 0)
            {
                // The natural continuation of the previous piece, compared over the overlap.
                int natural = previous + SynthesisHop;
                pos = BestMatch(input, natural, ideal);
            }
            pos = Math.Clamp(pos, 0, Math.Max(0, input.Length - 1));
            for (int i = 0; i < Window && outPos + i < output.Length; i++)
            {
                int src = pos + i;
                float s = src < input.Length ? input[src] : 0f;
                output[outPos + i] += s * Hann[i];
                weight[outPos + i] += Hann[i];
            }
            previous = pos;
        }
        var result = new float[outLength];
        for (int i = 0; i < outLength; i++) result[i] = weight[i] > 1e-3f ? output[i] / weight[i] : output[i];
        return result;
    }

    /// <summary>The start near <paramref name="ideal"/> whose first half-window is most like the one at <paramref name="natural"/>.</summary>
    private static int BestMatch(float[] x, int natural, int ideal)
    {
        int overlap = Window - SynthesisHop;
        int best = ideal;
        double bestScore = double.NegativeInfinity;
        for (int d = -Tolerance; d <= Tolerance; d += 2)
        {
            int cand = ideal + d;
            if (cand < 0 || cand + overlap > x.Length || natural + overlap > x.Length) continue;
            double dot = 0, energy = 1e-9;
            for (int i = 0; i < overlap; i += 2)
            {
                double a = x[natural + i], b = x[cand + i];
                dot += a * b;
                energy += b * b;
            }
            double score = dot / Math.Sqrt(energy);
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }

    /// <summary>Plain resampling (changes pitch slightly): only for tiny factors or very short sounds.</summary>
    private static float[] Resample(float[] input, double factor)
    {
        int n = (int)Math.Round(input.Length / factor);
        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            double src = i * factor;
            int a = (int)src;
            double t = src - a;
            float x0 = a < input.Length ? input[a] : 0f, x1 = a + 1 < input.Length ? input[a + 1] : x0;
            result[i] = (float)(x0 + (x1 - x0) * t);
        }
        return result;
    }
}
