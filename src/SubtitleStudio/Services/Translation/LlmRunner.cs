using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Sampling;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>A local language model: loaded once, then asked one question at a time. Implemented with LLamaSharp (llama.cpp).</summary>
public interface ILlmRunner : IDisposable
{
    /// <summary>What the model runs on, e.g. "Vulkan: AMD Radeon RX 6850M XT (all 49 layers)" or "processor (avx2)".</summary>
    string? DeviceUsed { get; }

    /// <summary>Why a requested graphics card wasn't (fully) used. Null when it was, or wasn't asked for.</summary>
    string? DeviceProblem { get; }

    /// <summary>The loaded model's architecture (e.g. "gemma3", "qwen3"). Null before loading.</summary>
    string? Architecture { get; }

    /// <summary>Loads the model if it isn't already (a different model or device reloads).</summary>
    Task LoadAsync(string modelPath, WhisperDevice device, CancellationToken ct);

    /// <summary>
    /// One answer to a system and a user message, in the model's own chat format, limited by a GBNF
    /// grammar. Deterministic (no sampling randomness). <paramref name="tokens"/> reports each piece as it comes.
    /// </summary>
    Task<string> CompleteAsync(string system, string user, string? grammar, int maxTokens, IProgress<string>? tokens, CancellationToken ct);

    /// <summary>Frees the model (graphics memory) until the next <see cref="LoadAsync"/>.</summary>
    void Release();
}

/// <summary>
/// llama.cpp through LLamaSharp, with the native library chosen and loaded by <see cref="LlamaRuntime"/>.
/// On a graphics card all layers are put on it; if that doesn't fit, fewer, and then the processor.
/// </summary>
public sealed partial class LlamaSharpRunner : ILlmRunner
{
    public const int ContextSize = 4096;

    private readonly object _logGate = new();
    private readonly Action<string, bool>? _log;
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private string? _modelPath;
    private WhisperDevice? _device;
    private string? _gpuName;
    private string? _offload;
    private StatelessExecutor? _executor;
    /// <summary>Warnings already in the Log: llama.cpp repeats the same ones for every question.</summary>
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public LlamaSharpRunner(Action<string, bool>? log = null)
    {
        _log = log;
    }

    public string? DeviceUsed { get; private set; }
    public string? DeviceProblem { get; private set; }
    public string? Architecture { get; private set; }

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadLine();

    [GeneratedRegex(@"ggml_vulkan: \d+ = (.+) \([^()]*\) \| ")]
    private static partial Regex VulkanDeviceLine();

    [GeneratedRegex(@"using device Vulkan\d+ \((.+)\) \([^()]*\) - \d+ MiB free")]
    private static partial Regex UsingDeviceLine();

    public async Task LoadAsync(string modelPath, WhisperDevice device, CancellationToken ct)
    {
        if (_weights is not null && _modelPath == modelPath && _device == device) return;
        Release();

        var info = GgufInfo.Read(modelPath);
        Architecture = info.Architecture;
        LlamaRuntime.Log = OnNativeLog;
        var loaded = LlamaRuntime.Prepare(device);
        DeviceProblem = LlamaRuntime.Problem;
        bool gpu = LlamaRuntime.UsesGpu;

        // All layers on the graphics card; if it runs out of memory, half, then none.
        int[] attempts = gpu ? new[] { 999, Math.Max(1, (info.BlockCount ?? 48) / 2), 0 } : new[] { 0 };
        Exception? last = null;
        foreach (var layers in attempts)
        {
            ct.ThrowIfCancellationRequested();
            lock (_logGate) { _gpuName = null; _offload = null; }
            var p = new ModelParams(modelPath)
            {
                ContextSize = ContextSize,
                GpuLayerCount = layers,
                BatchSize = 512,
                Threads = Math.Clamp(Environment.ProcessorCount, 1, 8),
            };
            LLamaWeights? weights = null;
            try
            {
                weights = await LLamaWeights.LoadFromFileAsync(p, ct).ConfigureAwait(false);
                // The working memory (KV cache) is allocated per question: try it once now so a card that
                // is too small is found here, not halfway through a file.
                await Task.Run(() => { using var context = weights.CreateContext(p); }, ct).ConfigureAwait(false);
                _weights = weights;
                _params = p;
                _modelPath = modelPath;
                _device = device;
                if (gpu && layers != attempts[0])
                    DeviceProblem = layers == 0
                        ? "The model didn't fit in the graphics card's memory: it runs on the processor (much slower). A smaller model will be faster."
                        : "The model didn't fit in the graphics card's memory: part of it runs on the processor (slower). A smaller model will be faster.";
                break;
            }
            catch (Exception ex) when (ex is LoadWeightsFailedException or LLamaDecodeError or RuntimeError or InvalidOperationException && !ct.IsCancellationRequested)
            {
                weights?.Dispose();
                last = ex;
                _log?.Invoke($"Loading with {(layers == 999 ? "all" : layers.ToString())} layers on the graphics card failed: {ex.Message}", true);
            }
        }
        if (_weights is null)
            throw new InvalidOperationException($"The language model couldn't be loaded ({Path.GetFileName(modelPath)}). "
                + (last is LoadWeightsFailedException ? "The file may be damaged, or made for a newer llama.cpp than the one in the app." : last?.Message));

        string? gpuName, offload;
        lock (_logGate) { gpuName = _gpuName; offload = _offload; }
        DeviceUsed = gpu && _params!.GpuLayerCount > 0
            ? $"Vulkan: {gpuName ?? device.Label}" + (offload is not null ? $", {offload}" : string.Empty)
            : $"processor ({loaded}, {_params!.Threads} threads)";
    }

    private void OnNativeLog(string message, bool warning)
    {
        if (OffloadLine().Match(message) is { Success: true } o)
            lock (_logGate) _offload = o.Groups[1].Value == o.Groups[2].Value ? $"all {o.Groups[2].Value} layers" : $"{o.Groups[1].Value} of {o.Groups[2].Value} layers";
        else if (UsingDeviceLine().Match(message) is { Success: true } u)
            lock (_logGate) _gpuName = u.Groups[1].Value.Trim();
        else if (VulkanDeviceLine().Match(message) is { Success: true } v)
            lock (_logGate) _gpuName ??= v.Groups[1].Value.Trim();
        // Only warnings and the lines that say where it runs go to the Log; llama.cpp is very talkative.
        // A warning it repeats for every question (each sets up its working memory again) is logged once.
        if (warning)
        {
            bool first;
            lock (_logGate) first = _warned.Add(message);
            if (first) _log?.Invoke(message, true);
        }
        else if (message.Contains("offloaded", StringComparison.Ordinal) || message.Contains("using device", StringComparison.Ordinal) || message.StartsWith("ggml_vulkan: ", StringComparison.Ordinal))
            _log?.Invoke(message, false);
    }

    public async Task<string> CompleteAsync(string system, string user, string? grammar, int maxTokens, IProgress<string>? tokens, CancellationToken ct)
    {
        var weights = _weights ?? throw new InvalidOperationException("Load the language model first.");
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", system).Add("user", user);
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var pipeline = new DefaultSamplingPipeline { Temperature = 0 };
        if (grammar is not null) pipeline = new DefaultSamplingPipeline { Temperature = 0, Grammar = new Grammar(grammar, "root") };
        var inference = new InferenceParams { MaxTokens = maxTokens, SamplingPipeline = pipeline };
        // One executor per loaded model: making one sets up (and drops) a working memory, on top of the
        // one each question needs; that was half a second wasted per question.
        var executor = _executor ??= new StatelessExecutor(weights, _params!);
        var sb = new StringBuilder();
        // llama.cpp works on this thread until the answer is complete: keep it off the caller's.
        await Task.Run(async () =>
        {
            await foreach (var piece in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            {
                sb.Append(piece);
                tokens?.Report(piece);
            }
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return sb.ToString();
    }

    public void Release()
    {
        _executor = null;
        lock (_logGate) _warned.Clear();
        _weights?.Dispose();
        _weights = null;
        _params = null;
        _modelPath = null;
        _device = null;
        DeviceUsed = null;
    }

    public void Dispose() => Release();
}
