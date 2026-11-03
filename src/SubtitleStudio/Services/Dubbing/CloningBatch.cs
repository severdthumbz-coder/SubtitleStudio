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
    private readonly int _threads;   // CPU inference threads; 0 = the exe's default (4). Lower eases sustained load.
    private readonly bool _easeCpu;  // run the sidecar below normal priority so the OS schedules around other work.
    private readonly Action<string>? _log;

    public CloningBatch(string exePath, string device, int timesteps, string workDir, Action<string>? log = null, int threads = 0, bool easeCpu = false)
    {
        _exePath = exePath;
        _device = device;
        _timesteps = timesteps;
        _workDir = workDir;
        _threads = Math.Max(0, threads);
        _easeCpu = easeCpu;
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
    public async Task<IReadOnlyDictionary<string, float[]>> SpeakAllAsync(IReadOnlyList<Job> jobs, IProgress<double>? progress, CancellationToken ct, CloningCache? cache = null)
    {
        var result = new Dictionary<string, float[]>();
        if (jobs.Count == 0) return result;

        // Resume: any job whose audio is already cached (same voice, text, quality) is reused and not sent to the exe.
        // So a dub stopped partway, or re-run after an unrelated change, only synthesises the lines it hasn't done.
        var keyById = new Dictionary<string, string>();
        var toRun = new List<Job>();
        int fromCache = 0;
        foreach (var j in jobs)
        {
            if (cache is not null)
            {
                var key = cache.Key(j.RefClip, j.Text, _timesteps, Rate);
                keyById[j.Id] = key;
                if (cache.Get(key, Rate) is { } cached) { result[j.Id] = cached; fromCache++; continue; }
            }
            toRun.Add(j);
        }
        if (fromCache > 0) _log?.Invoke($"Cloning (batch): {fromCache} of {jobs.Count} line(s) reused from an earlier run; {toRun.Count} to synthesise.");
        int total = jobs.Count;
        void ReportProgress(int done) => progress?.Report((fromCache + done) / (double)total);
        ReportProgress(0);
        if (toRun.Count == 0) return result;

        var runDir = Path.Combine(_workDir, "batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDir);
        var jobsPath = Path.Combine(runDir, "jobs.json");
        var outById = new Dictionary<string, string>();
        try
        {
            var payloadJobs = new List<object>(toRun.Count);
            foreach (var j in toRun)
            {
                var outPath = Path.Combine(runDir, $"{j.Id}.wav");
                outById[j.Id] = outPath;
                payloadJobs.Add(new { id = j.Id, @ref = j.RefClip, text = j.Text, @out = outPath });
            }
            var payload = new Dictionary<string, object> { ["jobs"] = payloadJobs };
            if (_timesteps > 0) payload["n_timesteps"] = _timesteps;
            if (_threads > 0) payload["threads"] = _threads; // fewer CPU threads = lower sustained load (the ease-CPU option)
            await File.WriteAllTextAsync(jobsPath, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);

            // Read and cache each line the moment the exe finishes it (on the DONE callback), so a cancel keeps every
            // line already done — the dub resumes from there next time instead of starting over.
            int done = 0;
            void OnLineDone(string id)
            {
                if (outById.TryGetValue(id, out var outPath) && File.Exists(outPath))
                {
                    try
                    {
                        var (samples, rate) = WavFile.Read(outPath);
                        var s = rate == Rate ? samples : Resample.Linear(samples, rate, Rate);
                        result[id] = s;
                        if (cache is not null && keyById.TryGetValue(id, out var key)) cache.Put(key, s, Rate);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        _log?.Invoke($"Cloning (batch): couldn't read line {id} ({ex.Message}); left silent.");
                    }
                }
                done++;
                ReportProgress(done);
            }

            try
            {
                await RunBatchAsync(jobsPath, OnLineDone, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled: the lines already cached above are kept, so this isn't wasted — rethrow to the caller.
                throw;
            }

            // Any line the exe reported but the DONE handler didn't capture (rare), swept up here.
            foreach (var (id, outPath) in outById)
            {
                if (result.ContainsKey(id)) continue;
                if (!File.Exists(outPath)) { _log?.Invoke($"Cloning (batch): line {id} produced no output; left silent."); continue; }
                try
                {
                    var (samples, rate) = WavFile.Read(outPath);
                    var s = rate == Rate ? samples : Resample.Linear(samples, rate, Rate);
                    result[id] = s;
                    if (cache is not null && keyById.TryGetValue(id, out var key)) cache.Put(key, s, Rate);
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
        if (_easeCpu)
        {
            // Below-normal priority so Windows schedules the sidecar around other work rather than pegging the cores,
            // which eases the sustained-load pressure. Best-effort — ignore if the OS won't let us set it.
            try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
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
