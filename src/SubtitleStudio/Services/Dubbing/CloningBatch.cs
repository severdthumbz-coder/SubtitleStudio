using System.Diagnostics;
using System.Text.Json;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Drives the cloning sidecar in BATCH mode: the exe loads the ~11.7 GB model once, speaks a whole list of lines, and
/// exits once — instead of one process (and one model load) per line, which is what makes the per-line path slow
/// (~130 s/line, almost all of it model load) and fragile over a long run (hundreds of launches build up memory
/// pressure until one crashes with a heap-corruption exit code).
///
/// Contract with the frozen exe's batch entry point:
///   index-tts.exe batch --jobs &lt;jobs.json&gt; --device &lt;cpu|cuda&gt; --lang en
/// where jobs.json is { "n_timesteps": N?, "jobs": [ { "id": "0", "ref": "ref.wav", "text": "...", "out": "0.wav" }, ... ] }.
/// The exe loads the model once, writes each job's "out" wav (22.05 kHz), and prints one line of progress per job to
/// stdout: "DONE &lt;id&gt;" after each (so the app can advance the bar), then exits 0. A job it can't speak it reports as
/// "FAIL &lt;id&gt; &lt;reason&gt;" and leaves that out missing; the app treats a missing out as a silent line, as before.
///
/// This type is only used when the installed sidecar supports "batch" (CloningSidecarStore.SupportsBatch). When it
/// doesn't (an older bundle), the app falls back to the per-line CloningTts. Output is read back per id and resampled
/// to the track rate by the caller, exactly as the per-line path does.
/// </summary>
public sealed class CloningBatch
{
    public const int Rate = 22050;

    private readonly string _exePath;
    private readonly string _device;
    private readonly int _timesteps;
    private readonly string _workDir;
    private readonly Action<string>? _log;

    public CloningBatch(string exePath, string device, int timesteps, string workDir, Action<string>? log = null)
    {
        _exePath = exePath;
        _device = device;
        _timesteps = timesteps;
        _workDir = workDir;
        _log = log;
        Directory.CreateDirectory(workDir);
    }

    public int SampleRate => Rate;
    public string DeviceLabel => _device == "cuda" ? "CUDA (voice cloning, batch)" : "processor (voice cloning, batch)";

    /// <summary>One line to speak: its reference clip, text, and a stable id used for the output filename and progress.</summary>
    public readonly record struct Job(string Id, string RefClip, string Text);

    /// <summary>
    /// Speaks every job in one exe run (one model load). Returns each job id's samples (22.05 kHz), or an empty array
    /// for a line the exe couldn't speak. Reports progress as a fraction 0..1 as each line finishes. Cancellation kills
    /// the exe (a long batch is stopped by ending the one process).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, float[]>> SpeakAllAsync(IReadOnlyList<Job> jobs, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, float[]>();
        if (jobs.Count == 0) return result;

        var runDir = Path.Combine(_workDir, "batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        var jobsPath = Path.Combine(runDir, "jobs.json");
        var outById = new Dictionary<string, string>();
        try
        {
            var payloadJobs = new List<object>(jobs.Count);
            foreach (var j in jobs)
            {
                var outPath = Path.Combine(runDir, $"{j.Id}.wav");
                outById[j.Id] = outPath;
                payloadJobs.Add(new { id = j.Id, @ref = j.RefClip, text = j.Text, @out = outPath });
            }
            var payload = _timesteps > 0
                ? (object)new { n_timesteps = _timesteps, jobs = payloadJobs }
                : new { jobs = payloadJobs };
            await File.WriteAllTextAsync(jobsPath, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);

            int done = 0;
            await RunBatchAsync(jobsPath, id =>
            {
                done++;
                progress?.Report(done / (double)jobs.Count);
            }, ct).ConfigureAwait(false);

            foreach (var (id, outPath) in outById)
            {
                if (!File.Exists(outPath)) { _log?.Invoke($"Cloning (batch): line {id} produced no output; left silent."); continue; }
                try
                {
                    var (samples, rate) = WavFile.Read(outPath);
                    result[id] = rate == Rate ? samples : Resample.Linear(samples, rate, Rate);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    _log?.Invoke($"Cloning (batch): couldn't read line {id} ({ex.Message}); left silent.");
                }
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(runDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task RunBatchAsync(string jobsPath, Action<string> onDone, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_exePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        foreach (var a in new[] { "batch", "--jobs", jobsPath, "--device", _device, "--lang", "en" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the voice-cloning sidecar (batch).");
        var errTask = p.StandardError.ReadToEndAsync(ct);

        // Read stdout line by line so "DONE <id>" advances the bar as each line finishes.
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DONE ", StringComparison.Ordinal))
                onDone(trimmed[5..].Trim());
            else if (trimmed.StartsWith("FAIL ", StringComparison.Ordinal))
                _log?.Invoke("Cloning (batch): " + trimmed);
        }

        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            throw;
        }

        if (p.ExitCode != 0)
        {
            var err = (await errTask.ConfigureAwait(false))?.Trim() ?? string.Empty;
            throw new InvalidOperationException("the cloning sidecar (batch) exited with code " + p.ExitCode + (err.Length > 0 ? ": " + err : string.Empty));
        }
    }
}
