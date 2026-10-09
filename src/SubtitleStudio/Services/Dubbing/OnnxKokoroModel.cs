using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>The Kokoro speech model, behind an interface so the rest can be tested without it.</summary>
public interface IKokoroModel : IDisposable
{
    /// <summary>"DirectML on AMD Radeon RX 7800 XT" or "processor".</summary>
    string DeviceLabel { get; }

    /// <summary>How many lines are best spoken at the same time (the processor: several; the graphics card: one).</summary>
    int Concurrency => 1;

    /// <summary>
    /// Speech for one piece of at most 510 phonemes: 24 kHz mono samples. <paramref name="tokens"/> without
    /// the padding (added here); <paramref name="style"/> the voice's row for this length. Safe to call
    /// from several threads at once.
    /// </summary>
    float[] Speak(long[] tokens, float[] style, float speed);
}

/// <summary>
/// Kokoro v1.0 (82 million parameters, Apache-2.0) through ONNX Runtime, inside the app, on the graphics
/// card with DirectML (the card GpuDetector chose; the model patched in memory by KokoroDmlPatcher) and
/// the processor together (SharedKokoroModel), or the processor alone when the card is too slow to help.
/// The processor runs the model as it is, several lines at a time.
/// </summary>
public sealed class OnnxKokoroModel : IKokoroModel
{
    public const int SampleRate = 24000;

    /// <summary>Remembers whether the card helps (models\tts\voice-device.txt: "both" or "cpu"; delete it to measure again).</summary>
    public const string ChoiceFileName = "voice-device.txt";
    private const string ChoiceVersion = "2";

    /// <summary>Lines timed to choose the device: different lengths, as in a real episode.</summary>
    private static readonly string[] Benchmark =
    {
        "Where were you last night?",
        "I told you already, I was at the hospital until two in the morning.",
        "Fine. Let's go.",
        "If you don't trust me, then why did you ask me to come here in the first place?",
    };

    private readonly InferenceSession _session;
    private readonly string _tokensInput;

    private OnnxKokoroModel(InferenceSession session, string label, int concurrency)
    {
        _session = session;
        DeviceLabel = label;
        Concurrency = concurrency;
        // Older exports name the input "tokens", newer ones "input_ids".
        _tokensInput = session.InputNames.Contains("input_ids") ? "input_ids" : "tokens";
    }

    public string DeviceLabel { get; }

    public int Concurrency { get; }

    /// <summary>
    /// The processor's threads: about one per core (half the logical processors), at least 2; split into
    /// lines spoken at once of 2 threads each (1 on a small machine). Kokoro's steps are small, so one line
    /// can't keep 8 cores busy, but four lines at a time can.
    /// </summary>
    public static (int Lines, int ThreadsPerLine) CpuPlan(int logicalProcessors)
    {
        int total = Math.Max(2, logicalProcessors / 2);
        int per = total >= 4 ? 2 : 1;
        return (Math.Max(1, total / per), per);
    }

    /// <summary>
    /// Loads the model. With a graphics card, the card and the processor are both used, together, unless
    /// the card is too slow to help: the first time on a PC both are timed on the same four lines and the
    /// choice is remembered. <paramref name="styleFor"/>: a voice's style for a given number of phonemes.
    /// </summary>
    public static IKokoroModel Load(string modelPath, Func<int, float[]> styleFor, GpuInfo? gpu, Action<string>? log = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
        if (gpu?.Primary is not { IsSoftware: false } adapter || gpu.Backend == AiBackend.Cpu) return LoadCpu(modelPath, log);

        var remembered = ReadChoice(folder, adapter.Name);
        if (remembered == "cpu")
        {
            log?.Invoke($"Using the processor only for the voices: the graphics card was measured too slow to help on this PC (delete models\\tts\\{ChoiceFileName} to measure again).");
            return LoadCpu(modelPath, log);
        }

        OnnxKokoroModel dml;
        try
        {
            dml = LoadDml(modelPath, adapter, styleFor, log);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            log?.Invoke("The graphics card couldn't run the voice model; using the processor. What DirectML said: " + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim());
            WriteChoice(folder, adapter.Name, "cpu");
            return LoadCpu(modelPath, log);
        }
        var cpu = LoadCpu(modelPath, log);
        if (remembered == "both")
        {
            log?.Invoke($"Voices on the graphics card and the processor together, one line on the card and {cpu.Concurrency} on the processor (delete models\\tts\\{ChoiceFileName} to measure again).");
            return new SharedKokoroModel(new IKokoroModel[] { dml, cpu }, log);
        }

        // First time on this PC: time both on the same lines. The card runs one line at a time, the processor several.
        double gpuRate = Measure(dml, styleFor), cpuRate = Measure(cpu, styleFor);
        bool together = gpuRate >= MinUsefulRate;
        log?.Invoke($"Voice speed measured: graphics card {gpuRate:0.0}x real time, processor {cpuRate:0.0}x ({cpu.Concurrency} lines at a time). "
            + (together ? $"Using both together (about {gpuRate + cpuRate:0.0}x)." : "The card is too slow to help: using the processor."));
        WriteChoice(folder, adapter.Name, together ? "both" : "cpu");
        if (together) return new SharedKokoroModel(new IKokoroModel[] { dml, cpu }, log);
        dml.Dispose();
        return cpu;
    }

    /// <summary>The graphics card is used alongside the processor when it makes speech at least this fast (times real time).</summary>
    public const double MinUsefulRate = 1.0;

    private static OnnxKokoroModel LoadCpu(string modelPath, Action<string>? log)
    {
        var (lines, threads) = CpuPlan(Environment.ProcessorCount);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            IntraOpNumThreads = threads,
        };
        var model = new OnnxKokoroModel(new InferenceSession(modelPath, options), "processor", lines);
        log?.Invoke($"Voice model loaded on the processor: {lines} line{(lines == 1 ? "" : "s")} at a time, {threads} thread{(threads == 1 ? "" : "s")} each.");
        return model;
    }

    private static OnnxKokoroModel LoadDml(string modelPath, GpuAdapter adapter, Func<int, float[]> styleFor, Action<string>? log)
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
            var model = new OnnxKokoroModel(session, $"DirectML on {adapter.Name}", 1);
            model.SelfTest(styleFor(5));
            log?.Invoke($"Voice model loaded on the graphics card ({adapter.Name}, DirectML).");
            return model;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    /// <summary>Seconds of speech made per second, on the benchmark lines (after one to warm up), as many at a time as the model takes.</summary>
    private static double Measure(OnnxKokoroModel model, Func<int, float[]> styleFor)
    {
        var g2p = EnglishG2P.Shared;
        var tokens = Benchmark.Select(l => KokoroVocab.Tokenize(g2p.Phonemize(l))).ToList();
        model.Speak(KokoroVocab.Tokenize(g2p.Phonemize("Hello there.")), styleFor(12), 1f);
        long samples = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(tokens, new ParallelOptions { MaxDegreeOfParallelism = model.Concurrency },
            t => Interlocked.Add(ref samples, model.Speak(t, styleFor(t.Length), 1f).Length));
        return samples / (double)SampleRate / Math.Max(0.001, watch.Elapsed.TotalSeconds);
    }

    private static string ChoiceKey(string adapterName) => $"{ChoiceVersion}|{adapterName}";

    private static string? ReadChoice(string folder, string adapterName)
    {
        try
        {
            var path = Path.Combine(folder, ChoiceFileName);
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split('\t');
            return parts.Length == 2 && parts[0] == ChoiceKey(adapterName) && parts[1] is "cpu" or "both" ? parts[1] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteChoice(string folder, string adapterName, string choice)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, ChoiceFileName), $"{ChoiceKey(adapterName)}\t{choice}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        // InferenceSession.Run may be called from several threads at once.
        using var results = _session.Run(inputs);
        return results.First().AsEnumerable<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
