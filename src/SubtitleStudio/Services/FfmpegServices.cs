using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SubtitleStudio.Services;

public sealed record FfmpegStatus(string? FfmpegPath, string? FfprobePath, string Source)
{
    public bool HasFfmpeg => FfmpegPath is not null;
    public bool HasFfprobe => FfprobePath is not null;
    public bool IsComplete => HasFfmpeg && HasFfprobe;

    public string Summary => (HasFfmpeg, HasFfprobe) switch
    {
        (true, true) => $"ffmpeg and ffprobe found ({Source}): {Path.GetDirectoryName(FfmpegPath)}",
        (true, false) => $"ffmpeg found ({Source}) but ffprobe.exe is missing next to it. Durations cannot be read.",
        (false, true) => $"ffprobe found ({Source}) but ffmpeg.exe is missing. Transcription and dubbing will need it.",
        _ => "ffmpeg not found. Durations, audio extraction and dubbing need it. Put ffmpeg.exe and ffprobe.exe next to SubtitleStudio.exe, on PATH, or set the path here.",
    };

    public string ShortText => IsComplete ? "ffmpeg: ready" : HasFfmpeg || HasFfprobe ? "ffmpeg: incomplete" : "ffmpeg: not found";
}

/// <summary>
/// Locates ffmpeg.exe / ffprobe.exe. Order: configured path (file or folder), next to the EXE
/// (also .\ffmpeg, .\ffmpeg\bin, .\tools), then PATH.
/// </summary>
public sealed class FfmpegLocator
{
    public FfmpegStatus Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim().Trim('"');
            string? dir = null, ffmpeg = null;
            if (File.Exists(configured))
            {
                ffmpeg = configured;
                dir = Path.GetDirectoryName(configured);
            }
            else if (Directory.Exists(configured))
            {
                dir = configured;
                ffmpeg = FindIn(dir, "ffmpeg.exe") ?? FindIn(Path.Combine(dir, "bin"), "ffmpeg.exe");
                if (ffmpeg is not null) dir = Path.GetDirectoryName(ffmpeg);
            }

            var ffprobe = FindIn(dir, "ffprobe.exe");
            if (ffmpeg is not null || ffprobe is not null)
                return new FfmpegStatus(ffmpeg, ffprobe, "configured path");

            // Configured path is wrong: fall through to auto-detect so the app still works;
            // Source in the result says where it was actually found.
        }

        foreach (var dir in LocalCandidateDirs())
        {
            var ffmpeg = FindIn(dir, "ffmpeg.exe");
            if (ffmpeg is not null)
                return new FfmpegStatus(ffmpeg, FindIn(dir, "ffprobe.exe"), "next to Subtitle Studio");
        }

        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath is not null)
            return new FfmpegStatus(onPath, FindIn(Path.GetDirectoryName(onPath), "ffprobe.exe") ?? FindOnPath("ffprobe.exe"), "PATH");

        var probeOnly = FindOnPath("ffprobe.exe");
        return new FfmpegStatus(null, probeOnly, probeOnly is null ? "not found" : "PATH");
    }

    private static IEnumerable<string> LocalCandidateDirs()
    {
        var root = AppPaths.ExeDirectory;
        yield return root;
        yield return Path.Combine(root, "ffmpeg");
        yield return Path.Combine(root, "ffmpeg", "bin");
        yield return Path.Combine(root, "tools");
    }

    private static string? FindIn(string? dir, string fileName)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var candidate = Path.Combine(dir, fileName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = FindIn(dir.Trim('"'), fileName);
            if (hit is not null) return hit;
        }
        return null;
    }
}

/// <summary>Reads media duration with ffprobe. Never throws for bad media; returns null instead.</summary>
public sealed class MediaProbeService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<TimeSpan?> ProbeDurationAsync(string ffprobePath, string mediaPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        var psi = new ProcessStartInfo(ffprobePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(mediaPath);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return null; // timed out
        }

        if (process.ExitCode != 0) return null;

        var first = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
               && seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
