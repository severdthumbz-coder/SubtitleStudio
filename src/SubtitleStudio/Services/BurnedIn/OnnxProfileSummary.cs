using System.Globalization;
using System.Text.Json;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// Summary of an ONNX Runtime profile (the JSON written with SessionOptions.EnableProfiling): for the last
/// model run, how many steps ran on each device ("provider") and which kinds of step took the time.
/// Steps DirectML can't run are handed to the processor, and each hand-over makes the graphics card
/// wait; this shows whether that is where the time goes.
/// </summary>
public sealed class OnnxProfileSummary
{
    public sealed record ProviderTime(string Provider, int Steps, double Milliseconds);
    public sealed record OpTime(string Op, string Provider, int Steps, double Milliseconds);

    public double RunMilliseconds { get; private init; }
    public IReadOnlyList<ProviderTime> Providers { get; private init; } = Array.Empty<ProviderTime>();
    public IReadOnlyList<OpTime> Ops { get; private init; } = Array.Empty<OpTime>();

    /// <summary>Parses the profile; null if it has no model run.</summary>
    public static OnnxProfileSummary? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        // The last "model_run" event bounds the steps of the last run (earlier runs include compiling).
        long runStart = -1, runEnd = -1;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (Str(e, "cat") == "Session" && Str(e, "name") == "model_run" && e.TryGetProperty("ts", out var ts) && e.TryGetProperty("dur", out var dur))
            {
                runStart = ts.GetInt64();
                runEnd = runStart + dur.GetInt64();
            }
        }
        if (runStart < 0) return null;

        var providers = new Dictionary<string, (int Steps, double Us)>();
        var ops = new Dictionary<(string, string), (int Steps, double Us)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (Str(e, "cat") != "Node" || !(Str(e, "name")?.EndsWith("_kernel_time", StringComparison.Ordinal) ?? false)) continue;
            long t = e.TryGetProperty("ts", out var ts) ? ts.GetInt64() : -1;
            if (t < runStart || t > runEnd) continue;
            double us = e.TryGetProperty("dur", out var d) ? d.GetInt64() : 0;
            string op = "?", provider = "?";
            if (e.TryGetProperty("args", out var args))
            {
                op = Str(args, "op_name") ?? op;
                provider = Short(Str(args, "provider") ?? provider);
            }
            var p = providers.GetValueOrDefault(provider);
            providers[provider] = (p.Steps + 1, p.Us + us);
            var o = ops.GetValueOrDefault((op, provider));
            ops[(op, provider)] = (o.Steps + 1, o.Us + us);
        }

        return new OnnxProfileSummary
        {
            RunMilliseconds = (runEnd - runStart) / 1000.0,
            Providers = providers.Select(kv => new ProviderTime(kv.Key, kv.Value.Steps, kv.Value.Us / 1000)).OrderByDescending(p => p.Milliseconds).ToList(),
            Ops = ops.Select(kv => new OpTime(kv.Key.Item1, kv.Key.Item2, kv.Value.Steps, kv.Value.Us / 1000)).OrderByDescending(o => o.Milliseconds).ToList(),
        };
    }

    /// <summary>One line for the Log, e.g. "run 2300 ms; DirectML 180 steps 40 ms, processor 12 steps 2100 ms; slowest: ...".</summary>
    public string Describe(int top = 6)
    {
        var ic = CultureInfo.InvariantCulture;
        var providers = string.Join(", ", Providers.Select(p => string.Create(ic, $"{p.Provider} {p.Steps} steps {p.Milliseconds:0} ms")));
        var slowest = string.Join(", ", Ops.Take(top).Select(o => string.Create(ic, $"{o.Op} on {o.Provider} ×{o.Steps} {o.Milliseconds:0} ms")));
        return string.Create(ic, $"run {RunMilliseconds:0} ms; {providers}; slowest: {slowest}.");
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string provider) => provider switch
    {
        "DmlExecutionProvider" => "DirectML",
        "CPUExecutionProvider" => "processor",
        _ => provider.Replace("ExecutionProvider", string.Empty, StringComparison.Ordinal),
    };
}
