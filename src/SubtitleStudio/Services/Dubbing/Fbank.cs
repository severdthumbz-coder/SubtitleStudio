using System.Numerics;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Kaldi-style log mel filter banks, as WeSpeaker's voiceprint models were trained on (torchaudio's
/// kaldi.fbank: 80 bins, 25 ms frames every 10 ms, Hamming window, pre-emphasis 0.97, DC removed, power
/// spectrum, samples scaled to the 16-bit range), then each bin's mean over the clip removed (CMN).
/// </summary>
public static class Fbank
{
    public const int SampleRate = 16000;
    public const int Bins = 80;
    private const int FrameLength = 400, FrameShift = 160, FftSize = 512;
    private const double LowFreq = 20, HighFreq = SampleRate / 2.0, PreEmphasis = 0.97;

    private static readonly double[] Window = Enumerable.Range(0, FrameLength).Select(i => 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameLength - 1))).ToArray();
    private static readonly (int First, double[] Weights)[] MelBanks = BuildMelBanks();

    private static double Mel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);

    private static (int, double[])[] BuildMelBanks()
    {
        int fftBins = FftSize / 2;
        double binHz = (double)SampleRate / FftSize;
        double melLow = Mel(LowFreq), melHigh = Mel(HighFreq), delta = (melHigh - melLow) / (Bins + 1);
        var banks = new (int, double[])[Bins];
        for (int b = 0; b < Bins; b++)
        {
            double left = melLow + b * delta, center = left + delta, right = center + delta;
            int first = -1;
            var weights = new List<double>();
            for (int k = 0; k < fftBins; k++)
            {
                double mel = Mel(binHz * k);
                if (mel > left && mel < right)
                {
                    if (first < 0) first = k;
                    weights.Add(mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center));
                }
                else if (first >= 0) break;
            }
            banks[b] = (Math.Max(0, first), weights.ToArray());
        }
        return banks;
    }

    /// <summary>Frames x 80 features of 16 kHz mono samples in [-1, 1] (empty when shorter than one frame).</summary>
    public static float[,] Compute(ReadOnlySpan<float> samples, bool meanNormalize = true)
    {
        int frames = samples.Length < FrameLength ? 0 : 1 + (samples.Length - FrameLength) / FrameShift;
        var features = new float[frames, Bins];
        var frame = new double[FrameLength];
        var fft = new Complex[FftSize];
        var power = new double[FftSize / 2 + 1];
        for (int f = 0; f < frames; f++)
        {
            int start = f * FrameShift;
            double mean = 0;
            for (int i = 0; i < FrameLength; i++) { frame[i] = samples[start + i] * 32768.0; mean += frame[i]; }
            mean /= FrameLength;
            for (int i = 0; i < FrameLength; i++) frame[i] -= mean;
            for (int i = FrameLength - 1; i > 0; i--) frame[i] -= PreEmphasis * frame[i - 1];
            frame[0] -= PreEmphasis * frame[0];
            for (int i = 0; i < FftSize; i++) fft[i] = i < FrameLength ? new Complex(frame[i] * Window[i], 0) : Complex.Zero;
            Fft(fft);
            for (int k = 0; k <= FftSize / 2; k++) power[k] = fft[k].Real * fft[k].Real + fft[k].Imaginary * fft[k].Imaginary;
            for (int b = 0; b < Bins; b++)
            {
                var (first, weights) = MelBanks[b];
                double energy = 0;
                for (int j = 0; j < weights.Length; j++) energy += weights[j] * power[first + j];
                features[f, b] = (float)Math.Log(Math.Max(energy, 1.1920928955078125e-07));
            }
        }
        if (meanNormalize && frames > 0)
        {
            for (int b = 0; b < Bins; b++)
            {
                double m = 0;
                for (int f = 0; f < frames; f++) m += features[f, b];
                m /= frames;
                for (int f = 0; f < frames; f++) features[f, b] -= (float)m;
            }
        }
        return features;
    }

    /// <summary>In-place radix-2 FFT.</summary>
    private static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var v = a[i + j + len / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }
}
