using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>How an MDX-Net separation model wants its sound (from the model's own metadata).</summary>
/// <param name="GivesVoices">The model's output is the voices (a "Voc" model); else the music and effects (an "Inst" model).</param>
public sealed record SeparationSettings(int SampleRate, int NFft, int Hop, int DimF, int DimT, bool GivesVoices)
{
    /// <summary>Samples in one piece the model takes: hop × (frames − 1).</summary>
    public int Window => Hop * (DimT - 1);

    /// <summary>Samples at each end of a piece that aren't kept (the transform is unreliable at the edges).</summary>
    public int Trim => NFft / 2;

    /// <summary>Samples kept from each piece (pieces follow each other by this much).</summary>
    public int Step => Window - 2 * Trim;

    public static SeparationSettings FromMetadata(IReadOnlyDictionary<string, string> meta)
    {
        int Int(string key) => meta.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : throw new InvalidDataException($"The separation model doesn't say its {key}.");
        var name = meta.TryGetValue("model_name", out var m) ? m : string.Empty;
        return new SeparationSettings(Int("sample_rate"), Int("n_fft"), Int("hop_length"), Int("dim_f"), Int("dim_t"),
            !name.Contains("Inst", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A voice-separation model, behind an interface so the rest can be tested without it.</summary>
public interface ISeparationModel : IDisposable
{
    SeparationSettings Settings { get; }

    /// <summary>"DirectML on AMD Radeon RX 6850M XT" or "processor".</summary>
    string DeviceLabel { get; }

    /// <summary>One piece's spectrum in, [4, DimF, DimT] (left real, left imaginary, right real, right imaginary); the same shape out.</summary>
    float[] Run(float[] spectrum);
}

/// <summary>
/// An MDX-Net model (Ultimate Vocal Remover's, as published in ONNX by the sherpa-onnx project) through ONNX
/// Runtime, inside the app: on the graphics card with DirectML, else on the processor.
/// </summary>
public sealed class OnnxSeparationModel : ISeparationModel
{
    private readonly InferenceSession _session;
    private readonly string _input;

    private OnnxSeparationModel(InferenceSession session, string label)
    {
        _session = session;
        DeviceLabel = label;
        _input = session.InputNames[0];
        Settings = SeparationSettings.FromMetadata(session.ModelMetadata.CustomMetadataMap);
    }

    public SeparationSettings Settings { get; }

    public string DeviceLabel { get; }

    public static ISeparationModel Load(string path, GpuInfo? gpu, Action<string>? log = null)
    {
        if (gpu?.Primary is { IsSoftware: false } adapter && gpu.Backend != AiBackend.Cpu)
        {
            InferenceSession? session = null;
            try
            {
                var options = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                options.AppendExecutionProvider_DML(Math.Max(0, adapter.AdapterIndex));
                session = new InferenceSession(path, options);
                var model = new OnnxSeparationModel(session, $"DirectML on {adapter.Name}");
                model.SelfTest();
                log?.Invoke($"Voice separation model loaded on the graphics card ({adapter.Name}, DirectML).");
                return model;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                session?.Dispose();
                log?.Invoke("The graphics card couldn't run the voice separation model; using the processor. What DirectML said: "
                    + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim());
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        var onCpu = new OnnxSeparationModel(new InferenceSession(path, cpu), "processor");
        log?.Invoke($"Voice separation model loaded on the processor ({cpu.IntraOpNumThreads} threads).");
        return onCpu;
    }

    /// <summary>A piece of noise must come back as finite numbers of a sane size.</summary>
    private void SelfTest()
    {
        var s = Settings;
        var rnd = new Random(1);
        var input = new float[4 * s.DimF * s.DimT];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(rnd.NextDouble() - 0.5);
        var output = Run(input);
        if (output.Length != input.Length || output.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1e4))
            throw new InvalidOperationException("the test piece came out wrong");
    }

    public float[] Run(float[] spectrum)
    {
        var s = Settings;
        var input = NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(spectrum, new[] { 1, 4, s.DimF, s.DimT }));
        using var results = _session.Run(new[] { input });
        return results.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Takes the voices out of stereo sound with an MDX-Net model: the sound is cut into pieces of about 6 s,
/// each turned into a spectrum (Hann window, centred, as torch.stft), given to the model and turned back
/// (overlap-add); the edges of each piece are dropped, so the kept parts join without seams. The voices
/// come out as the model's answer ("Voc" models) or what's left when its music and effects are taken from
/// the sound ("Inst" models).
/// </summary>
public sealed class VoiceSeparator
{
    private readonly ISeparationModel _model;
    private readonly SeparationSettings _s;
    private readonly Fft _fft;
    private readonly double[] _window;
    private readonly double[] _re, _im;

    public VoiceSeparator(ISeparationModel model)
    {
        _model = model;
        _s = model.Settings;
        _fft = new Fft(_s.NFft);
        _window = Enumerable.Range(0, _s.NFft).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / _s.NFft)).ToArray();
        _re = new double[_s.NFft];
        _im = new double[_s.NFft];
    }

    public SeparationSettings Settings => _s;

    /// <summary>
    /// The voices of one piece: <paramref name="left"/> and <paramref name="right"/> are Window samples, the
    /// first Trim of them before the part wanted. Writes Step samples of voices to the outputs.
    /// </summary>
    public void Piece(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> voicesLeft, Span<float> voicesRight)
    {
        int f = _s.DimF, t = _s.DimT, plane = f * t;
        var spectrum = new float[4 * plane];
        Analyse(left, spectrum.AsSpan(0, plane), spectrum.AsSpan(plane, plane));
        Analyse(right, spectrum.AsSpan(2 * plane, plane), spectrum.AsSpan(3 * plane, plane));
        var output = _model.Run(spectrum);
        var outLeft = Synthesise(output.AsSpan(0, plane), output.AsSpan(plane, plane));
        var outRight = Synthesise(output.AsSpan(2 * plane, plane), output.AsSpan(3 * plane, plane));
        int trim = _s.Trim, step = _s.Step;
        for (int i = 0; i < step; i++)
        {
            float l = outLeft[trim + i], r = outRight[trim + i];
            voicesLeft[i] = _s.GivesVoices ? l : left[trim + i] - l;
            voicesRight[i] = _s.GivesVoices ? r : right[trim + i] - r;
        }
    }

    /// <summary>Spectrum of a piece (frames centred, reflected at the ends), bins 0..DimF−1, laid out [bin, frame].</summary>
    private void Analyse(ReadOnlySpan<float> x, Span<float> real, Span<float> imag)
    {
        int n = _s.NFft, hop = _s.Hop, half = n / 2, len = x.Length;
        for (int frame = 0; frame < _s.DimT; frame++)
        {
            int origin = frame * hop - half;
            for (int k = 0; k < n; k++)
            {
                int i = origin + k;
                if (i < 0) i = -i;
                else if (i >= len) i = 2 * (len - 1) - i;
                i = Math.Clamp(i, 0, len - 1);
                _re[k] = x[i] * _window[k];
                _im[k] = 0;
            }
            _fft.Forward(_re, _im);
            for (int b = 0; b < _s.DimF; b++)
            {
                real[b * _s.DimT + frame] = (float)_re[b];
                imag[b * _s.DimT + frame] = (float)_im[b];
            }
        }
    }

    /// <summary>Sound from a spectrum (bins above DimF silent): windowed overlap-add, divided by the summed squared window, centring removed.</summary>
    private float[] Synthesise(ReadOnlySpan<float> real, ReadOnlySpan<float> imag)
    {
        int n = _s.NFft, hop = _s.Hop, half = n / 2, frames = _s.DimT;
        int total = n + hop * (frames - 1);
        var sum = new double[total];
        var weight = new double[total];
        for (int frame = 0; frame < frames; frame++)
        {
            Array.Clear(_re);
            Array.Clear(_im);
            for (int b = 0; b < _s.DimF; b++)
            {
                _re[b] = real[b * frames + frame];
                _im[b] = imag[b * frames + frame];
            }
            // The other half mirrors the first (the sound is real).
            for (int b = 1; b < half; b++)
            {
                _re[n - b] = _re[b];
                _im[n - b] = -_im[b];
            }
            _im[0] = 0;
            _im[half] = 0;
            _fft.Inverse(_re, _im);
            int at = frame * hop;
            for (int k = 0; k < n; k++)
            {
                sum[at + k] += _re[k] * _window[k];
                weight[at + k] += _window[k] * _window[k];
            }
        }
        var output = new float[hop * (frames - 1)];
        for (int i = 0; i < output.Length; i++)
        {
            double w = weight[i + half];
            output[i] = w > 1e-11 ? (float)(sum[i + half] / w) : 0f;
        }
        return output;
    }

    /// <summary>
    /// The voices of a whole stretch held in memory (a preview): stereo in, stereo voices out, the same length.
    /// </summary>
    public (float[] Left, float[] Right) Separate(float[] left, float[] right, IProgress<double>? progress, CancellationToken ct)
    {
        var voicesLeft = new float[left.Length];
        var voicesRight = new float[right.Length];
        var stream = new Stream(this);
        int pos = 0, outPos = 0, block = _s.Step;
        while (outPos < left.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(block, left.Length - pos);
            if (n > 0) stream.Feed(left.AsSpan(pos, n), right.AsSpan(pos, n));
            else stream.Finish();
            pos += Math.Max(0, n);
            int before = outPos;
            while (stream.TryTake(out var l, out var r))
            {
                int take = Math.Min(l.Length, left.Length - outPos);
                l.AsSpan(0, take).CopyTo(voicesLeft.AsSpan(outPos));
                r.AsSpan(0, take).CopyTo(voicesRight.AsSpan(outPos));
                outPos += take;
                progress?.Report(Math.Min(1, outPos / (double)left.Length));
                if (outPos >= left.Length) break;
            }
            if (n <= 0 && outPos == before) break;
        }
        return (voicesLeft, voicesRight);
    }

    /// <summary>
    /// Separation as the sound arrives: Feed it any amount, take the voices out a piece at a time (Step samples,
    /// in order, from the start of the sound), Finish to pad the end with silence.
    /// </summary>
    public sealed class Stream
    {
        private readonly VoiceSeparator _owner;
        private readonly float[] _left, _right;
        private int _filled;
        private bool _finished;
        private readonly Queue<(float[] L, float[] R)> _ready = new();

        public Stream(VoiceSeparator owner)
        {
            _owner = owner;
            var s = owner._s;
            _left = new float[s.Window];
            _right = new float[s.Window];
            _filled = s.Trim; // silence before the start, so the first kept part is the sound's first sample
        }

        public void Feed(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            while (left.Length > 0)
            {
                int n = Math.Min(left.Length, _left.Length - _filled);
                left[..n].CopyTo(_left.AsSpan(_filled));
                right[..n].CopyTo(_right.AsSpan(_filled));
                _filled += n;
                left = left[n..];
                right = right[n..];
                if (_filled == _left.Length) Run();
            }
        }

        /// <summary>No more sound: what's left is separated with silence after it.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            var s = _owner._s;
            // Pieces until every real sample has been kept (a piece keeps buffer[Trim .. Trim + Step)).
            int realEnd = _filled;
            while (realEnd > s.Trim)
            {
                Array.Clear(_left, realEnd, _left.Length - realEnd);
                Array.Clear(_right, realEnd, _right.Length - realEnd);
                Run();
                realEnd -= s.Step;
            }
        }

        private void Run()
        {
            var s = _owner._s;
            var l = new float[s.Step];
            var r = new float[s.Step];
            _owner.Piece(_left, _right, l, r);
            _ready.Enqueue((l, r));
            // Keep the last 2 × Trim samples: the next piece starts Step later.
            int keep = _left.Length - s.Step;
            Array.Copy(_left, s.Step, _left, 0, keep);
            Array.Copy(_right, s.Step, _right, 0, keep);
            _filled = keep;
        }

        public bool TryTake(out float[] left, out float[] right)
        {
            if (_ready.TryDequeue(out var p))
            {
                left = p.L;
                right = p.R;
                return true;
            }
            left = right = Array.Empty<float>();
            return false;
        }
    }
}
