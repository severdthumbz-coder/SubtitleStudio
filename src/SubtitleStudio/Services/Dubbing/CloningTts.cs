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
    private readonly Action<string>? _log;

    public CloningTts(string exePath, IReadOnlyDictionary<string, string> refClips, string device, int timesteps, string workDir, Action<string>? log = null)
    {
        _exePath = exePath;
        _refClips = refClips;
        _device = device;
        _timesteps = timesteps;
        _workDir = workDir;
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

        var outPath = Path.Combine(_workDir, $"clone-{Guid.NewGuid():N}.wav");
        var args = new List<string> { "synth", "--ref", refClip, "--text", text, "--out", outPath, "--device", _device, "--lang", "en" };
        if (_timesteps > 0) { args.Add("--n-timesteps"); args.Add(_timesteps.ToString()); }

        try
        {
            RunExe(args);
            if (!File.Exists(outPath)) { _log?.Invoke("Cloning: the sidecar produced no output for a line."); return Array.Empty<float>(); }
            var (samples, rate) = WavFile.Read(outPath);
            // The engine should emit Rate; if a build differs, resample so SampleRate stays the engine's declared rate.
            return rate == Rate ? samples : Resample.Linear(samples, rate, Rate);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Cloning: the sidecar failed on a line (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim() + ").");
            return Array.Empty<float>();
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

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
