using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A loaded Piper voice (a VITS model in ONNX). Takes a phoneme-id sequence (from PiperConfig.ToIds) and returns
/// speech samples at the voice's sample rate. Runs on the graphics card with DirectML when one is available, else on
/// the processor — the same pattern as the Kokoro model, but Piper's graph needs no patching (it has no transposed
/// convolutions), so loading is a plain InferenceSession.
///
/// Piper's ONNX signature (standard VITS): inputs "input" (int64 ids [1,N]), "input_lengths" (int64 [1]),
/// "scales" (float32 [3] = noise_scale, length_scale, noise_w); one float output of audio samples in [-1, 1].
///
/// NOTE: the inference path below follows Piper's documented I/O but has not been run against a real Piper .onnx in
/// the dev environment (no model file there). First run is on a machine that has downloaded a voice; the SelfTest
/// guards against a silent/garbage load, and input/output names are discovered from the session rather than assumed.
/// </summary>
public sealed class PiperModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName, _lengthsName, _scalesName;

    public string DeviceLabel { get; }
    public int SampleRate { get; }
    public PiperConfig Config { get; }

    private PiperModel(InferenceSession session, PiperConfig config, string label)
    {
        _session = session;
        Config = config;
        SampleRate = config.SampleRate;
        DeviceLabel = label;
        // Discover the input names (VITS exports vary: "input"/"input_lengths"/"scales", occasionally "text"/"...").
        var ins = session.InputNames.ToList();
        _inputName = ins.FirstOrDefault(n => n.Contains("input") && !n.Contains("length")) ?? ins.FirstOrDefault(n => n is "text" or "x") ?? ins[0];
        _lengthsName = ins.FirstOrDefault(n => n.Contains("length")) ?? "input_lengths";
        _scalesName = ins.FirstOrDefault(n => n.Contains("scale")) ?? "scales";
    }

    public static PiperModel Load(string modelPath, PiperConfig config, GpuAdapter? adapter, Action<string>? log = null)
    {
        if (adapter is { } a)
        {
            try
            {
                var options = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                options.AppendExecutionProvider_DML(Math.Max(0, a.AdapterIndex));
                var model = new PiperModel(new InferenceSession(modelPath, options), config, $"DirectML on {a.Name}");
                model.SelfTest();
                log?.Invoke($"Piper voice loaded on the graphics card ({a.Name}, DirectML).");
                return model;
            }
            catch (Exception ex)
            {
                log?.Invoke("The graphics card couldn't run the Piper voice; using the processor. " + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim());
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        var m = new PiperModel(new InferenceSession(modelPath, cpu), config, "processor");
        log?.Invoke("Piper voice loaded on the processor.");
        return m;
    }

    /// <summary>Speech samples for a phoneme-id sequence (as PiperConfig.ToIds produces), mono at SampleRate.</summary>
    public float[] Speak(long[] ids)
    {
        if (ids.Length == 0) return Array.Empty<float>();
        var scales = new[] { Config.NoiseScale, Config.LengthScale, Config.NoiseW };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<long>(ids, new[] { 1, ids.Length })),
            NamedOnnxValue.CreateFromTensor(_lengthsName, new DenseTensor<long>(new long[] { ids.Length }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor(_scalesName, new DenseTensor<float>(scales, new[] { scales.Length })),
        };
        using var results = _session.Run(inputs);
        // Piper's output is [1, 1, samples] or [samples]; flatten whatever shape to the sample vector.
        return results.First().AsEnumerable<float>().ToArray();
    }

    private void SelfTest()
    {
        // A short, language-neutral id sequence (BOS/EOS plus whatever the map has) must produce finite sound.
        var ids = Config.ToIds("həˈloʊ");
        var audio = Speak(ids);
        if (audio.Length < SampleRate / 50 || audio.Any(s => !float.IsFinite(s) || Math.Abs(s) > 4))
            throw new InvalidOperationException("the Piper test phrase came out empty or invalid");
    }

    public void Dispose() => _session.Dispose();
}
