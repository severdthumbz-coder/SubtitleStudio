using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// LaMa through ONNX Runtime, built into the app (no Python, no separate install). Runs on the graphics
/// card with DirectML (AMD, NVIDIA and Intel on Windows 10/11), on the adapter GpuDetector chose
/// (DirectML's device id is the DXGI adapter index; on laptops the default would be the integrated GPU).
/// Falls back to the processor if the graphics card can't be used.
///
/// The model is patched in memory when loaded (see OnnxDmlPatcher): DirectML-compatible matrix
/// multiplies, and a flexible input size, so patches run at 256, 320, 384 or 512 pixels instead of always 512.
/// </summary>
public sealed class OnnxInpaintModel : IInpaintModel
{
    private readonly byte[]? _model;             // patched bytes, for sessions of other sizes (DirectML, fixed setups)
    private readonly int _adapterIndex;
    private readonly DmlSetup? _setup;           // null: processor
    private readonly Dictionary<int, InferenceSession> _sessions = new();
    private readonly InferenceSession? _anySize;  // plain DirectML setup or processor: one session for every size
    private readonly object _gate = new();
    private readonly Action<string>? _log;

    private OnnxInpaintModel(string label, int batch, bool flexible, InferenceSession? anySize, byte[]? model, int adapterIndex, DmlSetup? setup, Action<string>? log)
    {
        DeviceLabel = label;
        BatchSize = batch;
        FlexibleSize = flexible;
        _anySize = anySize;
        _model = model;
        _adapterIndex = adapterIndex;
        _setup = setup;
        _log = log;
    }

    public int Size => 512;
    public bool FlexibleSize { get; }
    public int BatchSize { get; }
    public string DeviceLabel { get; }

    /// <summary>
    /// Ways of setting up the DirectML session. Which is fastest depends on the graphics card and driver,
    /// so the app measures them on this PC and keeps the fastest.
    /// </summary>
    public enum DmlSetup
    {
        /// <summary>As loaded: any number and size of patches per call.</summary>
        Plain,
        /// <summary>Fixed patches per call and patch size: all sizes known up front (one session per size).</summary>
        Fixed,
        /// <summary>Fixed, and the int8 weights expanded to float once at load.</summary>
        FixedFolded,
    }

    private static SessionOptions DmlOptions(int adapterIndex, int batch, int size, DmlSetup setup, string? profilePrefix = null)
    {
        var options = new SessionOptions
        {
            // DirectML requirements.
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        if (setup != DmlSetup.Plain)
        {
            options.AddFreeDimensionOverrideByName("batch", batch);
            options.AddFreeDimensionOverrideByName("height", size);
            options.AddFreeDimensionOverrideByName("width", size);
        }
        if (setup == DmlSetup.FixedFolded) options.AddSessionConfigEntry("session.disable_quant_qdq", "1");
        if (profilePrefix is not null)
        {
            // The prefix must be set before profiling is switched on (it is read at that moment).
            options.ProfileOutputPathPrefix = profilePrefix;
            options.EnableProfiling = true;
        }
        options.AppendExecutionProvider_DML(Math.Max(0, adapterIndex));
        return options;
    }

    private static string SetupName(DmlSetup setup) => setup switch
    {
        DmlSetup.Plain => "plain",
        DmlSetup.Fixed => "fixed sizes",
        _ => "fixed sizes, weights expanded",
    };

    /// <summary>
    /// The DirectML setup measured fastest on this PC, and the patch size it runs at (256 when the card
    /// takes a flexible size, else 512), remembered in the models folder so it is measured once (delete
    /// the file to measure again). Keyed by graphics card and model, and by a version that changes
    /// whenever the setups or the way they are measured change.
    /// </summary>
    private const string SetupFileName = "directml-setup.txt";
    private const string SetupVersion = "4";

    private static string SetupKey(string adapterName) => $"{SetupVersion}|{adapterName}|{InpaintModelStore.ExpectedSize}";

    public sealed record Choice(DmlSetup Setup, int Batch, int Size);

    public static Choice? ReadSetup(string folder, string adapterName)
    {
        try
        {
            var path = Path.Combine(folder, SetupFileName);
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Trim().Split('\t');
            if (parts.Length == 4 && parts[0] == SetupKey(adapterName) && Enum.TryParse<DmlSetup>(parts[1], out var setup)
                && int.TryParse(parts[2], out var batch) && batch >= 1 && int.TryParse(parts[3], out var size) && size is 256 or 512)
                return new Choice(setup, batch, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public static void WriteSetup(string folder, string adapterName, Choice choice)
    {
        try
        {
            File.WriteAllText(Path.Combine(folder, SetupFileName), $"{SetupKey(adapterName)}\t{choice.Setup}\t{choice.Batch}\t{choice.Size}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loads the model; tries DirectML on the chosen graphics card first. <paramref name="gpuBatch"/>:
    /// patches per call on the graphics card (from its memory, see PerformancePlan); 1 on the processor.
    /// The first time on a PC, each setup is timed on blank 256-pixel patches (the first run of each also
    /// compiles the GPU shaders), the fastest is kept and remembered, and one run is profiled into the Log.
    /// If no setup runs, the batch is halved; if the card can't take a flexible size at all, the model is
    /// used at a fixed 512 pixels (as up to 1.0.0.22); only if that fails too, the processor is used.
    /// <paramref name="log"/> receives what happened.
    /// </summary>
    public static OnnxInpaintModel Load(string modelPath, GpuInfo? gpu, int gpuBatch, Action<string>? log = null)
    {
        var original = File.ReadAllBytes(modelPath);
        byte[]? flexible = null;
        try
        {
            flexible = OnnxDmlPatcher.Patch(original, out int rewritten, out int flexibleCount);
            log?.Invoke($"Model prepared: {rewritten} Fourier-transform steps rewritten for DirectML; patch size made flexible ({(flexibleCount == 3 ? "256, 320, 384 or 512 pixels" : "not possible, always 512")}).");
            if (flexibleCount != 3) flexible = null;
        }
        catch (InvalidDataException ex)
        {
            log?.Invoke("The model could not be prepared (" + ex.Message + "); using it as it is, at 512 pixels.");
        }

        string? gpuError = null;
        if (gpu?.Primary is { IsSoftware: false } adapter && gpu.Backend != AiBackend.Cpu)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
                string label = $"DirectML on {adapter.Name}";
                byte[] Bytes(int size) => size == 512 || flexible is null
                    ? OnnxDmlPatcher.Patch(original, out _, out _, flexibleSize: false)
                    : flexible;

                if (ReadSetup(folder, adapter.Name) is { } known && (known.Size == 512 || flexible is not null))
                {
                    try
                    {
                        var bytes = Bytes(known.Size);
                        var (session, ms) = Measure(bytes, adapter.AdapterIndex, known.Batch, known.Size, known.Setup);
                        log?.Invoke($"Using the setup measured fastest on this PC: {SetupName(known.Setup)}, {known.Batch} patches per call, {ms:0} ms per {known.Size}-pixel patch (delete models\\{SetupFileName} to measure again).");
                        return Create(label, known, session, bytes, adapter.AdapterIndex, log);
                    }
                    catch (OnnxRuntimeException ex)
                    {
                        log?.Invoke($"The remembered setup failed ({FirstLine(ex.Message)}); measuring again.");
                    }
                }

                // Flexible size (256-pixel patches) first; the fixed 512 model if the card can't take it.
                foreach (int size in flexible is not null ? new[] { 256, 512 } : new[] { 512 })
                {
                    var bytes = Bytes(size);
                    if (FindFastest(bytes, adapter.AdapterIndex, gpuBatch, size, log, ref gpuError) is { } choice)
                    {
                        WriteSetup(folder, adapter.Name, choice);
                        var chosen = ProfiledSession(bytes, adapter.AdapterIndex, choice.Batch, choice.Size, choice.Setup, log);
                        return Create(label, choice, chosen, bytes, adapter.AdapterIndex, log);
                    }
                    if (size == 256) log?.Invoke("The graphics card can't run the model at a flexible size; trying the fixed 512-pixel model (as up to 1.0.0.22).");
                }
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or InvalidDataException)
            {
                gpuError = ex.Message;
                log?.Invoke("DirectML could not load the model: " + FirstLine(ex.Message));
            }
        }

        using var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        var cpuSession = new InferenceSession(flexible ?? original, cpu);
        var cpuLabel = gpuError is null ? "processor (no usable graphics card)" : $"processor (graphics card unavailable: {FirstLine(gpuError)})";
        return new OnnxInpaintModel(cpuLabel, 1, flexible is not null, cpuSession, null, -1, null, log);
    }

    /// <summary>
    /// Times every setup at <paramref name="size"/> pixels and returns the fastest. Each is released before
    /// the next (one copy of the model on the card at a time). If none runs, the batch is halved; not when
    /// every failure has the same cause as at the larger batch (then it isn't the card's memory).
    /// </summary>
    private static Choice? FindFastest(byte[] model, int adapterIndex, int gpuBatch, int size, Action<string>? log, ref string? gpuError)
    {
        string? previousCauses = null;
        for (int batch = Math.Max(1, gpuBatch); batch >= 1; batch /= 2)
        {
            Choice? best = null;
            double bestMs = double.MaxValue;
            var causes = new List<string>();
            foreach (var setup in new[] { DmlSetup.Plain, DmlSetup.Fixed, DmlSetup.FixedFolded })
            {
                try
                {
                    var (session, ms) = Measure(model, adapterIndex, batch, size, setup);
                    session.Dispose();
                    log?.Invoke($"Setup {SetupName(setup)}, {size}-pixel patches, {batch} per call: {ms:0} ms per patch.");
                    if (ms < bestMs) (bestMs, best) = (ms, new Choice(setup, batch, size));
                }
                catch (OnnxRuntimeException ex)
                {
                    gpuError = ex.Message;
                    causes.Add(Cause(ex.Message));
                    log?.Invoke($"Setup {SetupName(setup)}, {size}-pixel patches, {batch} per call failed: {FirstLine(ex.Message)}");
                }
            }
            if (best is not null)
            {
                // A card big enough for 8 per call may do better still with 16 (kept only if clearly faster:
                // fixed setups pad the last call of each batch, and bigger calls pad more).
                if (best.Batch >= 8 && best.Batch == Math.Max(1, gpuBatch))
                {
                    int doubled = best.Batch * 2;
                    try
                    {
                        var (session, ms) = Measure(model, adapterIndex, doubled, size, best.Setup);
                        session.Dispose();
                        log?.Invoke($"Setup {SetupName(best.Setup)}, {size}-pixel patches, {doubled} per call: {ms:0} ms per patch.");
                        if (ms < bestMs * 0.9) (bestMs, best) = (ms, best with { Batch = doubled });
                    }
                    catch (OnnxRuntimeException ex)
                    {
                        log?.Invoke($"{doubled} per call failed: {FirstLine(ex.Message)}");
                    }
                }
                log?.Invoke($"Fastest on this PC: {SetupName(best.Setup)}, {size}-pixel patches, {best.Batch} per call, {bestMs:0} ms per patch (remembered for next time).");
                return best;
            }
            string all = string.Join("|", causes);
            if (all == previousCauses) break; // same failure at a smaller batch: not a memory problem
            previousCauses = all;
        }
        return null;
    }

    /// <summary>The failing step and error, without the parts that change from run to run (thread, counters, addresses).</summary>
    private static string Cause(string message)
    {
        int cut = message.IndexOf("Status Message", StringComparison.Ordinal);
        var head = cut > 0 ? message[..cut] : FirstLine(message);
        return head + (message.Contains("80070057", StringComparison.Ordinal) ? " 80070057" : string.Empty)
                    + (message.Contains("887A0005", StringComparison.OrdinalIgnoreCase) ? " device removed" : string.Empty);
    }

    /// <summary>The loaded model around its first DirectML session (for the measured size, if fixed).</summary>
    private static OnnxInpaintModel Create(string label, Choice choice, InferenceSession first, byte[] model, int adapterIndex, Action<string>? log)
    {
        bool flexible = choice.Size != 512;
        if (choice.Setup == DmlSetup.Plain) return new OnnxInpaintModel(label, choice.Batch, flexible, first, null, adapterIndex, choice.Setup, log);
        var loaded = new OnnxInpaintModel(label, choice.Batch, flexible, null, model, adapterIndex, choice.Setup, log);
        loaded._sessions[choice.Size] = first;
        if (flexible) loaded.PrebuildInBackground(320, 384);
        return loaded;
    }

    private volatile bool _disposed;

    /// <summary>
    /// Builds the sessions for the other patch sizes in the background (about 7 s each), so the first
    /// two-line subtitle of a run doesn't wait for one. Built outside the lock, so calls at other sizes
    /// carry on meanwhile; a session another call built first is kept and the spare one released.
    /// </summary>
    private void PrebuildInBackground(params int[] sizes) => Task.Run(() =>
    {
        foreach (int size in sizes)
        {
            if (_disposed) return;
            lock (_gate) if (_sessions.ContainsKey(size)) continue;
            try
            {
                var clock = Stopwatch.StartNew();
                InferenceSession session;
                using (var options = DmlOptions(_adapterIndex, BatchSize, size, _setup!.Value))
                    session = new InferenceSession(_model!, options);
                bool kept;
                lock (_gate)
                {
                    kept = !_disposed && _sessions.TryAdd(size, session);
                }
                if (kept) _log?.Invoke($"Session for {size}-pixel patches built in the background in {clock.Elapsed.TotalSeconds:0.0} s.");
                else session.Dispose();
            }
            catch (OnnxRuntimeException ex)
            {
                _log?.Invoke($"Session for {size}-pixel patches not built ahead ({FirstLine(ex.Message)}); it is built when first needed.");
            }
        }
    });

    /// <summary>Builds a session, runs it once (compiles the shaders), then times two runs: ms per patch.</summary>
    private static (InferenceSession Session, double MsPerPatch) Measure(byte[] model, int adapterIndex, int batch, int size, DmlSetup setup)
    {
        InferenceSession? session = null;
        try
        {
            using (var options = DmlOptions(adapterIndex, batch, size, setup))
                session = new InferenceSession(model, options);
            WarmUp(session, batch, size);
            var clock = Stopwatch.StartNew();
            WarmUp(session, batch, size);
            WarmUp(session, batch, size);
            return (session, clock.Elapsed.TotalMilliseconds / (2.0 * batch));
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The chosen setup's session, built with profiling on for its first runs: one run is summarised into
    /// the Log (how many steps ran on DirectML and how many were handed to the processor, and which kinds
    /// of step took the time). Profiling stops after that; the session is the one used for the video.
    /// </summary>
    private static InferenceSession ProfiledSession(byte[] model, int adapterIndex, int batch, int size, DmlSetup setup, Action<string>? log)
    {
        string folder = Path.Combine(Path.GetTempPath(), "SubtitleStudio");
        Directory.CreateDirectory(folder);
        InferenceSession session;
        using (var options = DmlOptions(adapterIndex, batch, size, setup, log is null ? null : Path.Combine(folder, "ai-profile")))
            session = new InferenceSession(model, options);
        try
        {
            WarmUp(session, batch, size);
            WarmUp(session, batch, size);
        }
        catch
        {
            session.Dispose();
            throw;
        }
        if (log is null) return session;
        try
        {
            var file = session.EndProfiling();
            // Normally the full path; a bare name is in the prefix's folder.
            if (!Path.IsPathRooted(file)) file = Path.Combine(folder, Path.GetFileName(file));
            var summary = OnnxProfileSummary.Parse(File.ReadAllText(file));
            log(summary is null ? "Profile: no model run recorded." : "Profile of one call: " + summary.Describe());
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log("Profile failed: " + FirstLine(ex.Message));
        }
        return session;
    }

    /// <summary>One run on blank patches: compiles the GPU shaders and proves the batch fits.</summary>
    private static void WarmUp(InferenceSession session, int batch, int size)
    {
        var image = new DenseTensor<float>(new float[batch * 3 * size * size], new[] { batch, 3, size, size });
        var maskData = new float[batch * size * size];
        for (int i = 0; i < maskData.Length; i += 7) maskData[i] = 1;
        var mask = new DenseTensor<float>(maskData, new[] { batch, 1, size, size });
        using var results = session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image", image),
            NamedOnnxValue.CreateFromTensor("mask", mask),
        });
        _ = results.First().AsEnumerable<float>().First();
    }

    /// <summary>The session for patches of <paramref name="size"/> (built on first use for fixed setups). Call under _gate.</summary>
    private InferenceSession SessionFor(int size)
    {
        if (_anySize is not null) return _anySize;
        if (_sessions.TryGetValue(size, out var session)) return session;
        var clock = Stopwatch.StartNew();
        using (var options = DmlOptions(_adapterIndex, BatchSize, size, _setup!.Value))
            session = new InferenceSession(_model!, options);
        _sessions[size] = session;
        _log?.Invoke($"Session for {size}-pixel patches built in {clock.Elapsed.TotalSeconds:0.0} s.");
        return session;
    }

    public Task<float[]> RunAsync(float[] images, float[] masks, int count, int size, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            // Fixed DirectML setups are built for exactly BatchSize patches: a short last call is padded
            // with blank patches (mask 0, nothing to invent) and only the real results are returned.
            int run = _setup is DmlSetup.Fixed or DmlSetup.FixedFolded ? BatchSize : count;
            int plane = size * size;
            float[] imageData = images, maskData = masks;
            if (run > count)
            {
                imageData = new float[run * 3 * plane];
                maskData = new float[run * plane];
                images.AsSpan(0, count * 3 * plane).CopyTo(imageData);
                masks.AsSpan(0, count * plane).CopyTo(maskData);
            }
            var image = new DenseTensor<float>(imageData, new[] { run, 3, size, size });
            var mask = new DenseTensor<float>(maskData, new[] { run, 1, size, size });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image", image),
                NamedOnnxValue.CreateFromTensor("mask", mask),
            };
            lock (_gate)
            {
                try
                {
                    using var results = SessionFor(size).Run(inputs);
                    var all = results.First().AsTensor<float>();
                    var output = new float[count * 3 * plane];
                    if (all is DenseTensor<float> dense) dense.Buffer.Span[..output.Length].CopyTo(output);
                    else { int i = 0; foreach (var v in all) { if (i == output.Length) break; output[i++] = v; } }
                    return output;
                }
                catch (OnnxRuntimeException ex)
                {
                    // Reported in the status line and the Log like any other failure (full text in the Log).
                    throw new InvalidOperationException($"AI fill failed ({DeviceLabel}): {FirstLine(ex.Message)}", ex);
                }
            }
        }, ct);

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? text;

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            _anySize?.Dispose();
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
