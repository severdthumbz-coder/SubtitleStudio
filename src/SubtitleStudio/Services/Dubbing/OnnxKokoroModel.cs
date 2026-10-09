using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>The Kokoro speech model, behind an interface so the rest can be tested without it.</summary>
public interface IKokoroModel : IDisposable
{
    /// <summary>"DirectML on AMD Radeon RX 7800 XT" or "processor".</summary>
    string DeviceLabel { get; }

    /// <summary>
    /// Speech for one piece of at most 510 phonemes: 24 kHz mono samples. <paramref name="tokens"/> without
    /// the padding (added here); <paramref name="style"/> the voice's row for this length.
    /// </summary>
    float[] Speak(long[] tokens, float[] style, float speed);
}

/// <summary>
/// Kokoro v1.0 (82 million parameters, Apache-2.0) through ONNX Runtime, inside the app. Runs on the
/// graphics card with DirectML (the card GpuDetector chose; the model patched in memory by
/// KokoroDmlPatcher) and falls back to the processor if the card can't run it (or its test word comes
/// out wrong). The processor runs the model as it is.
/// </summary>
public sealed class OnnxKokoroModel : IKokoroModel
{
    public const int SampleRate = 24000;

    private readonly InferenceSession _session;
    private readonly string _tokensInput;
    private readonly object _gate = new();

    private OnnxKokoroModel(InferenceSession session, string label)
    {
        _session = session;
        DeviceLabel = label;
        // Older exports name the input "tokens", newer ones "input_ids".
        _tokensInput = session.InputNames.Contains("input_ids") ? "input_ids" : "tokens";
    }

    public string DeviceLabel { get; }

    /// <summary>
    /// Loads the model and says a word (in the voice style <paramref name="testStyle"/>) to check it works (the first run on a graphics card also
    /// prepares its shaders). Graphics card first; the processor if that fails.
    /// </summary>
    public static OnnxKokoroModel Load(string modelPath, float[] testStyle, GpuInfo? gpu, Action<string>? log = null)
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
                // DirectML can't run Kokoro's transposed convolutions: rewritten as plain ones, in memory.
                var patched = KokoroDmlPatcher.Patch(File.ReadAllBytes(modelPath), out int rewritten);
                log?.Invoke($"Voice model prepared for DirectML: {rewritten} transposed convolutions rewritten as plain ones.");
                session = new InferenceSession(patched, options);
                var model = new OnnxKokoroModel(session, $"DirectML on {adapter.Name}");
                model.SelfTest(testStyle);
                log?.Invoke($"Voice model loaded on the graphics card ({adapter.Name}, DirectML).");
                return model;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                session?.Dispose();
                log?.Invoke("The graphics card couldn't run the voice model; using the processor. What DirectML said: " + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim());
            }
        }

        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        var cpuModel = new OnnxKokoroModel(new InferenceSession(modelPath, cpu), "processor");
        log?.Invoke($"Voice model loaded on the processor ({Path.GetFileName(modelPath)}).");
        return cpuModel;
    }

    /// <summary>A short phrase must come out as sound, not silence or invalid numbers.</summary>
    private void SelfTest(float[] style)
    {
        var tokens = KokoroVocab.Tokenize("həlˈO");
        var audio = Speak(tokens, style, 1f);
        if (audio.Length < SampleRate / 20 || audio.Any(s => !float.IsFinite(s) || Math.Abs(s) > 4))
            throw new InvalidOperationException("the test phrase came out empty");
    }

    public float[] Speak(long[] tokens, float[] style, float speed)
    {
        if (tokens.Length == 0) return Array.Empty<float>();
        if (tokens.Length > KokoroVocab.MaxPhonemes) throw new ArgumentException($"At most {KokoroVocab.MaxPhonemes} phonemes at a time.", nameof(tokens));
        var padded = new long[tokens.Length + 2];
        Array.Copy(tokens, 0, padded, 1, tokens.Length);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_tokensInput, new DenseTensor<long>(padded, new[] { 1, padded.Length })),
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, new[] { 1, KokoroVoices.StyleSize })),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { speed }, new[] { 1 })),
        };
        lock (_gate)
        {
            using var results = _session.Run(inputs);
            return results.First().AsEnumerable<float>().ToArray();
        }
    }

    public void Dispose() => _session.Dispose();
}
