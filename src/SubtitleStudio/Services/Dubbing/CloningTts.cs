using System.Diagnostics;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// Voice cloning as a dubbing engine: drives the bundled IndexTTS sidecar exe (CloningSidecarStore) once per line,
/// like the app drives FFmpeg. Each speaker has a short reference clip (~10 s of their original audio); a line is
/// spoken by running the exe with that speaker's reference and the line's text, and the exe's output wav is read
/// back as samples. Implements IDubbingVoice so the dubbing path uses it like Kokoro/Piper; because cloning is slow,
/// Concurrency is 1 and the caller shows a time warning before starting. Output is 22.05 kHz (BigVGAN); the caller
/// resamples to the track rate (the Resample helper), as the Piper overflow path already does.
///
/// Personal-use only (IndexTTS licence). Opt-in — never the default engine.
/// </summary>
public sealed class CloningTts : IDubbingVoice
{
    /// <summary>IndexTTS / BigVGAN output sample rate.</summary>
    public const int Rate = 22050;

    private readonly string _exePath;
    private readonly IReadOnlyDictionary<string, string> _refClips; // voiceId -> reference wav path
    private readonly string _device;      // "cpu" or "cuda"
    private readonly int _timesteps;      // cfm diffusion steps (lower = faster, slightly rougher); 0 = the model default
    private readonly string _workDir;     // temp folder for per-line in/out wavs
    private readonly int _spacingMs;      // pause between line launches so pressure doesn't accumulate over a long dub
    private readonly Action<string>? _log;

    public CloningTts(string exePath, IReadOnlyDictionary<string, string> refClips, string device, int timesteps, string workDir, Action<string>? log = null, int spacingMs = 400)
    {
        _exePath = exePath;
        _refClips = refClips;
        _device = device;
        _timesteps = timesteps;
        _workDir = workDir;
        _spacingMs = Math.Max(0, spacingMs);
        _log = log;
        Directory.CreateDirectory(workDir);
    }

    public int SampleRate => Rate;
    public string DeviceLabel => _device == "cuda" ? "CUDA (voice cloning)" : "processor (voice cloning)";
    public int Concurrency => 1; // cloning is heavy; one line at a time

    /// <summary>Cloning works from text directly (no phoneme step the app can show); the line's text is what it says.</summary>
    public string Phonemes(string subtitleText) => subtitleText ?? string.Empty;

    public float[] Speak(string subtitleText, string voiceId, float speed = 1f)
    {
        var text = (subtitleText ?? string.Empty).Trim();
        if (text.Length == 0) return Array.Empty<float>();
        if (!_refClips.TryGetValue(voiceId, out var refClip) || !File.Exists(refClip))
        {
            _log?.Invoke($"Cloning: no reference clip for {voiceId}; the line is left silent.");
            return Array.Empty<float>();
        }

        // Each line is its own exe process (the exe loads the model, speaks, exits). Over a long dub that's hundreds of
        // launches of a native-heavy process; one can fail transiently (a bad exit, a brief file lock, memory pressure
        // that shows up as a heap-corruption exit code like 0xC0000374). So a failed line is retried once after a short
        // pause rather than silenced, and a small gap is left between launches so the OS reclaims the last process's
        // memory and handles before the next starts — which is what keeps a 500-line run from dying partway.
        // args layout: 0 synth, 1 --ref, 2 refClip, 3 --text, 4 text, 5 --out, 6 <outPath>, 7 --device, ...
        var args = new List<string> { "synth", "--ref", refClip, "--text", text, "--out", "", "--device", _device, "--lang", "en" };
        const int OutPathIndex = 6;
        if (_timesteps > 0) { args.Add("--n-timesteps"); args.Add(_timesteps.ToString()); }

        Exception? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) Thread.Sleep(RetryPauseMs); // let the failed process's resources be reclaimed before retry
            var outPath = Path.Combine(_workDir, $"clone-{Guid.NewGuid():N}.wav");
            args[OutPathIndex] = outPath;
            try
            {
                RunExe(args);
                if (!File.Exists(outPath)) throw new InvalidOperationException("the sidecar produced no output file");
                var (samples, rate) = WavFile.Read(outPath);
                if (_spacingMs > 0) Thread.Sleep(_spacingMs); // breathe between launches so pressure doesn't accumulate
                return rate == Rate ? samples : Resample.Linear(samples, rate, Rate);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == 0) _log?.Invoke("Cloning: a line failed, retrying once (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim() + ").");
            }
            finally
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        _log?.Invoke("Cloning: the sidecar failed on a line after a retry, leaving it silent (" + (lastError?.Message.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? "unknown") + ").");
        return Array.Empty<float>();
    }

    /// <summary>Pause before retrying a failed line (ms), to let the OS reclaim the failed process's resources.</summary>
    private const int RetryPauseMs = 1500;

    private void RunExe(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_exePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the voice-cloning sidecar.");
        // Drain both streams so the process can't block on a full pipe.
        var err = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException("the sidecar exited with code " + p.ExitCode + (err.Result.Length > 0 ? ": " + err.Result.Trim() : string.Empty));
    }

    public void Dispose() { } // nothing persistent: each line is a fresh process
}
