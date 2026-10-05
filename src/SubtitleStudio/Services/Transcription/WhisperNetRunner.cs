using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace SubtitleStudio.Services.Transcription;

/// <summary>
/// whisper.cpp through Whisper.net, inside the app (no outside program). The native library comes in
/// two builds: Vulkan (graphics cards from AMD, NVIDIA and Intel) and processor-only; Whisper.net loads
/// the Vulkan one when the Vulkan driver is present, else the processor one. The library is loaded once
/// per run of the app, so the device can't change until the app restarts; this class says so when it matters.
/// </summary>
public sealed partial class WhisperNetRunner : IWhisperRunner
{
    private const string VisibleDevicesVariable = "GGML_VK_VISIBLE_DEVICES";

    private static readonly object LoadGate = new();
    private static bool _libraryPrepared;
    private static bool _libraryAllowsGpu;
    private static int? _libraryVulkanIndex;
    private static Action<string, bool>? _logSink;
    private static IDisposable? _logHook;

    private readonly Action<string, bool>? _log;
    private readonly object _deviceLinesGate = new();
    private readonly List<string> _deviceLines = new();
    private WhisperFactory? _factory;
    private string? _factoryKey;

    private readonly Action<string, bool> _sink;

    private readonly Func<string?>? _vadModel;
    private WhisperVadFactory? _vad;

    /// <param name="log">Receives whisper.cpp's own messages: (text, isWarning).</param>
    /// <param name="vadModel">Path of the Silero speech detector model (null: not available).</param>
    public WhisperNetRunner(Action<string, bool>? log = null, Func<string?>? vadModel = null)
    {
        _log = log;
        _sink = OnNativeLog;
        _vadModel = vadModel;
    }

    /// <summary>Silero's speech probability threshold: whisper.cpp's default. Lower (0.4) let music blips through as speech.</summary>
    public float VadThreshold { get; set; } = 0.5f;

    public async Task<IReadOnlyList<SpeechRegion>?> DetectSpeechAsync(float[] samples, WhisperRunOptions options, CancellationToken ct)
    {
        if (_vadModel?.Invoke() is not { } path || !File.Exists(path)) return null;
        return await Task.Run(() =>
        {
            PrepareLibrary(options.Device);
            _logSink = _sink;
            try
            {
                _vad ??= WhisperVadFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false });
                // One thread: Silero is a tiny network run ~2,000 times a minute, one step after another. Spreading
                // each step over more threads costs more than the step (measured: 8 threads 18x slower than 1; on a
                // 16-thread laptop a 65-minute episode took 167 s instead of about 15).
                using var processor = _vad.CreateBuilder()
                    .WithThreads(1)
                    .WithThreshold(VadThreshold)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(200))
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(300))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(100))
                    .Build();
                ct.ThrowIfCancellationRequested();
                return (IReadOnlyList<SpeechRegion>?)processor.DetectSpeech(samples).Select(r => new SpeechRegion(r.Start, r.End)).ToList();
            }
            catch (Exception ex) when (ex is WhisperModelLoadException or WhisperProcessingException or InvalidOperationException
                                           or FileNotFoundException or DllNotFoundException)
            {
                _log?.Invoke("The speech detector couldn't run (" + ex.Message + "); silences are found by loudness instead.", true);
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    public string? DeviceUsed { get; private set; }
    public string? DeviceProblem { get; private set; }

    /// <summary>Which native library Whisper.net loaded ("Vulkan", "Cpu"), once loaded.</summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    [GeneratedRegex(@"ggml_vulkan: (\d+) = (.+?)(?: \(|\s*\||$)")]
    private static partial Regex VulkanDeviceLine();

    public Task LoadAsync(WhisperRunOptions options, CancellationToken ct) => Task.Run(() => FactoryFor(options), ct);

    public (string Code, float Probability)? DetectedLanguage { get; private set; }

    public async IAsyncEnumerable<WhisperSegment> RunAsync(IReadOnlyList<ReadOnlyMemory<float>> chunks, WhisperRunOptions options,
        IProgress<(int Chunk, int Percent)>? progress, [EnumeratorCancellation] CancellationToken ct)
    {
        var factory = await Task.Run(() => FactoryFor(options), ct).ConfigureAwait(false);
        DetectedLanguage = null;
        int current = 0;

        var builder = factory.CreateBuilder()
            .WithThreads(Math.Max(1, options.Threads))
            .WithProbabilities()
            .WithTokenTimestamps();
        builder = options.Language is { Length: > 0 } language ? builder.WithLanguage(language) : builder.WithLanguageDetection();
        if (options.Translate) builder = builder.WithTranslate();
        if (!options.CarryContext) builder = builder.WithNoContext();
        if (progress is not null) builder = builder.WithProgressHandler(p => progress.Report((Volatile.Read(ref current), p)));

        await using var processor = builder.Build();
        string? currentLanguage = options.Language;
        if (options.Language is not { Length: > 0 } && chunks.Count > 0)
        {
            var first = chunks[0];
            var (code, probability) = await Task.Run(() => processor.DetectLanguageWithProbability(first.Span), ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(code))
            {
                DetectedLanguage = (code, probability);
                processor.ChangeLanguage(code);
                currentLanguage = code;
            }
        }

        for (int c = 0; c < chunks.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref current, c);
            // A scene in another language: that language for this chunk, then back.
            var wanted = options.ChunkLanguages is { } langs && c < langs.Count && langs[c] is { Length: > 0 } l ? l : options.Language is { Length: > 0 } main ? main : currentLanguage;
            if (wanted is { Length: > 0 } && wanted != currentLanguage)
            {
                processor.ChangeLanguage(wanted);
                currentLanguage = wanted;
            }
            IAsyncEnumerator<SegmentData>? segments = null;
            try
            {
                segments = processor.ProcessAsync(chunks[c], ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    SegmentData segment;
                    try
                    {
                        if (!await segments.MoveNextAsync().ConfigureAwait(false)) break;
                        segment = segments.Current;
                    }
                    catch (Exception ex) when (Translate(ex) is { } plain)
                    {
                        throw plain;
                    }
                    yield return Map(segment, c);
                }
            }
            finally
            {
                if (segments is not null) await segments.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<(string Code, float Probability)?>?> DetectLanguagesAsync(IReadOnlyList<ReadOnlyMemory<float>> audio, WhisperRunOptions options,
        IProgress<int>? progress, CancellationToken ct)
    {
        var factory = await Task.Run(() => FactoryFor(options), ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var result = new (string Code, float Probability)?[audio.Count];
            using var processor = factory.CreateBuilder().WithThreads(Math.Max(1, options.Threads)).WithLanguageDetection().Build();
            for (int i = 0; i < audio.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (code, probability) = processor.DetectLanguageWithProbability(audio[i].Span);
                    if (!string.IsNullOrEmpty(code)) result[i] = (code, probability);
                }
                catch (Exception ex) when (ex is WhisperProcessingException or InvalidOperationException)
                {
                    // Too short or unreadable: no answer for this piece.
                }
                progress?.Report(i + 1);
            }
            return (IReadOnlyList<(string Code, float Probability)?>?)result;
        }, ct).ConfigureAwait(false);
    }

    private static WhisperSegment Map(SegmentData s, int chunk)
    {
        var tokens = new WhisperTokenInfo[s.Tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = s.Tokens[i];
            var text = t.Text ?? string.Empty;
            bool special = text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal);
            tokens[i] = new WhisperTokenInfo(text, TimeSpan.FromMilliseconds(t.Start * 10.0), TimeSpan.FromMilliseconds(t.End * 10.0), t.Probability, special);
        }
        return new WhisperSegment(s.Start, s.End, s.Text, s.Probability, s.NoSpeechProbability, s.Language, tokens, chunk);
    }

    /// <summary>Plain-language errors for the Log and status bar (null: let it through, e.g. cancellation).</summary>
    private static Exception? Translate(Exception ex) => ex switch
    {
        OperationCanceledException => null,
        WhisperModelLoadException => new InvalidOperationException("whisper.cpp couldn't load the model file. It may be damaged or too new for this version: download it again from the list.", ex),
        WhisperProcessingException => new InvalidOperationException("whisper.cpp stopped with an error while transcribing: " + ex.Message, ex),
        _ => null,
    };

    private WhisperFactory FactoryFor(WhisperRunOptions options)
    {
        PrepareLibrary(options.Device);
        string key = options.ModelPath + "|" + options.Device.UseGpu;
        if (_factory is not null && _factoryKey == key) return _factory;

        _factory?.Dispose();
        _factory = null;
        lock (_deviceLinesGate) _deviceLines.Clear();
        _logSink = _sink;
        try
        {
            _factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseGpu = options.Device.UseGpu && _libraryAllowsGpu, GpuDevice = 0 });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("The whisper.cpp library couldn't be loaded: " + ex.Message
                + (OperatingSystem.IsWindows() ? " If this keeps happening, installing the Microsoft Visual C++ Redistributable (x64) usually fixes it." : string.Empty), ex);
        }
        catch (WhisperModelLoadException ex)
        {
            throw new InvalidOperationException("whisper.cpp couldn't load the model file. It may be damaged or too new for this version: download it again from the list.", ex);
        }
        _factoryKey = key;
        DescribeDevice(options);
        return _factory;
    }

    /// <summary>
    /// Before the native library loads: which builds to try, and which Vulkan device ggml may see
    /// (only the chosen one, so it can't pick a built-in GPU instead of the graphics card).
    /// </summary>
    private void PrepareLibrary(WhisperDevice device)
    {
        lock (LoadGate)
        {
            if (_libraryPrepared)
            {
                if (device.UseGpu && !_libraryAllowsGpu)
                    DeviceProblem = "Whisper was started on the processor in this session. Restart Subtitle Studio to use the graphics card.";
                else if (device.UseGpu && device.VulkanIndex != _libraryVulkanIndex)
                    DeviceProblem = "A different graphics device was chosen after Whisper started. Restart Subtitle Studio to switch.";
                else
                    DeviceProblem = null;
                return;
            }

            if (device.UseGpu && device.VulkanIndex is { } index)
                NativeEnvironment.Set(VisibleDevicesVariable, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RuntimeOptions.RuntimeLibraryOrder = device.UseGpu
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
            _logHook ??= LogProvider.AddLogger((level, message) =>
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                // Called from whisper.cpp's own code: an exception escaping here would end the app.
                try
                {
                    _logSink?.Invoke(message.TrimEnd(), level <= WhisperLogLevel.Warning);
                }
                catch (Exception)
                {
                }
            });
            _libraryPrepared = true;
            _libraryAllowsGpu = device.UseGpu;
            _libraryVulkanIndex = device.UseGpu ? device.VulkanIndex : null;
            DeviceProblem = null;
        }
    }

    private void OnNativeLog(string message, bool warning)
    {
        // The speech detector lists every stretch it found (hundreds per episode): the summary line is enough.
        if (!warning && message.Contains("whisper_vad_segments_from_probs: VAD segment ", StringComparison.Ordinal)) return;
        if (message.Contains("vulkan", StringComparison.OrdinalIgnoreCase) || message.Contains("backend", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gpu", StringComparison.OrdinalIgnoreCase))
            lock (_deviceLinesGate) _deviceLines.Add(message);
        _log?.Invoke(message, warning);
    }

    private void DescribeDevice(WhisperRunOptions options)
    {
        List<string> lines;
        lock (_deviceLinesGate) lines = _deviceLines.ToList();
        string runtime = LoadedRuntime ?? "unknown";
        bool usedGpu = lines.Any(l => l.Contains("whisper_backend_init_gpu: using", StringComparison.Ordinal))
                       || (runtime == nameof(RuntimeLibrary.Vulkan) && options.Device.UseGpu && !lines.Any(l => l.Contains("no GPU found", StringComparison.Ordinal)));
        string? name = lines.Select(l => VulkanDeviceLine().Match(l)).Where(m => m.Success).Select(m => m.Groups[2].Value.Trim()).FirstOrDefault();

        if (options.Device.UseGpu && usedGpu)
        {
            DeviceUsed = "Vulkan: " + (name ?? options.Device.Label);
        }
        else
        {
            DeviceUsed = $"processor, {Math.Max(1, options.Threads)} threads";
            if (options.Device.UseGpu && DeviceProblem is null)
                DeviceProblem = runtime == nameof(RuntimeLibrary.Vulkan)
                    ? "The Vulkan build of whisper.cpp loaded but found no usable graphics device, so it runs on the processor."
                    : "The Vulkan build of whisper.cpp couldn't be loaded (the graphics driver's Vulkan support may be missing or too old), so it runs on the processor.";
        }
    }

    public void ReleaseModel()
    {
        _factory?.Dispose();
        _factory = null;
        _factoryKey = null;
    }

    public void Dispose()
    {
        _vad?.Dispose();
        _vad = null;
        _factory?.Dispose();
        _factory = null;
        if (ReferenceEquals(_logSink, _sink)) _logSink = null;
    }
}
