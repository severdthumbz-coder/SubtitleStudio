namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A complex FFT for sizes that are a power of two times 1, 3 or 5 (the separation models use 6144 = 3 × 2048
/// and 5120 = 5 × 1024): radix-2 transforms of the p interleaved parts, joined with twiddle factors.
/// Not thread-safe (scratch buffers): one per thread.
/// </summary>
public sealed class Fft
{
    private readonly int _n, _m, _p;
    private readonly double[] _cos, _sin;
    private readonly int[] _rev;
    private readonly double[][] _subRe, _subIm;

    public Fft(int n)
    {
        _n = n;
        _p = new[] { 1, 3, 5 }.FirstOrDefault(p => n % p == 0 && IsPowerOfTwo(n / p));
        if (_p == 0) throw new ArgumentException($"FFT size {n} isn't a power of two times 1, 3 or 5.", nameof(n));
        _m = n / _p;
        _cos = new double[n];
        _sin = new double[n];
        for (int k = 0; k < n; k++)
        {
            _cos[k] = Math.Cos(2 * Math.PI * k / n);
            _sin[k] = Math.Sin(2 * Math.PI * k / n);
        }
        int bits = (int)Math.Log2(_m);
        _rev = new int[_m];
        for (int i = 0; i < _m; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        _subRe = Enumerable.Range(0, _p).Select(_ => new double[_m]).ToArray();
        _subIm = Enumerable.Range(0, _p).Select(_ => new double[_m]).ToArray();
    }

    public int Size => _n;

    private static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;

    /// <summary>In place: X[k] = Σ x[j]·e^(−2πijk/N).</summary>
    public void Forward(double[] re, double[] im)
    {
        if (_p == 1)
        {
            Radix2(re, im);
            return;
        }
        for (int r = 0; r < _p; r++)
        {
            var sr = _subRe[r];
            var si = _subIm[r];
            for (int j = 0; j < _m; j++)
            {
                sr[j] = re[j * _p + r];
                si[j] = im[j * _p + r];
            }
            Radix2(sr, si);
        }
        for (int k = 0; k < _n; k++)
        {
            int km = k % _m;
            double xr = _subRe[0][km], xi = _subIm[0][km];
            for (int r = 1; r < _p; r++)
            {
                int t = (int)((long)r * k % _n);
                double c = _cos[t], s = -_sin[t];
                double ar = _subRe[r][km], ai = _subIm[r][km];
                xr += ar * c - ai * s;
                xi += ar * s + ai * c;
            }
            re[k] = xr;
            im[k] = xi;
        }
    }

    /// <summary>In place: x[j] = (1/N) Σ X[k]·e^(2πijk/N).</summary>
    public void Inverse(double[] re, double[] im)
    {
        for (int i = 0; i < _n; i++) im[i] = -im[i];
        Forward(re, im);
        double scale = 1.0 / _n;
        for (int i = 0; i < _n; i++)
        {
            re[i] *= scale;
            im[i] = -im[i] * scale;
        }
    }

    private void Radix2(double[] re, double[] im)
    {
        int m = _m;
        for (int i = 0; i < m; i++)
        {
            int j = _rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= m; len <<= 1)
        {
            int half = len >> 1, step = _n / len;
            for (int start = 0; start < m; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double c = _cos[k * step], s = -_sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
