using System.Diagnostics;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Video;

/// <summary>An H.264 encoder ffmpeg can use, with the arguments for high quality.</summary>
public sealed record VideoEncoder(string Name, string Label, bool UsesGraphicsCard, IReadOnlyList<string> Arguments);

/// <summary>
/// Picks the fastest working H.264 encoder: the graphics card's own (NVIDIA NVENC, AMD AMF, Intel
/// Quick Sync) when ffmpeg has it and a short test encode succeeds, else the processor (x264).
/// "Listed by ffmpeg" isn't enough: a GPU encoder can be compiled in but fail for lack of a driver.
/// </summary>
public static class VideoEncoders
{
    public static readonly VideoEncoder Nvenc = new("h264_nvenc", "NVIDIA NVENC (graphics card)", true,
        new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "19", "-b:v", "0" });
    public static readonly VideoEncoder Amf = new("h264_amf", "AMD AMF (graphics card)", true,
        new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "18", "-qp_p", "20", "-qp_b", "22" });
    public static readonly VideoEncoder Qsv = new("h264_qsv", "Intel Quick Sync (graphics card)", true,
        new[] { "-c:v", "h264_qsv", "-preset", "slow", "-global_quality", "20" });
    public static readonly VideoEncoder X264 = new("libx264", "x264 (processor)", false,
        new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18" });

    /// <summary>Candidates in order of preference for the detected graphics card.</summary>
    public static IReadOnlyList<VideoEncoder> CandidatesFor(GpuVendor? vendor) => vendor switch
    {
        GpuVendor.Nvidia => new[] { Nvenc, X264 },
        GpuVendor.Amd => new[] { Amf, X264 },
        GpuVendor.Intel => new[] { Qsv, X264 },
        _ => new[] { X264 },
    };

    public static async Task<VideoEncoder> PickAsync(string ffmpeg, GpuVendor? vendor, CancellationToken ct, ActivityLog? log = null)
    {
        foreach (var candidate in CandidatesFor(vendor))
        {
            if (candidate == X264)
            {
                log?.Info("Encoder", "Using x264 on the processor.");
                return candidate;
            }
            bool works = await WorksAsync(ffmpeg, candidate, ct).ConfigureAwait(false);
            log?.Write(works ? LogLevel.Info : LogLevel.Warning, "Encoder",
                works ? $"{candidate.Name} passed the test encode: using {candidate.Label}." : $"{candidate.Name} failed the test encode (not in this ffmpeg build, or the driver refused it); trying the next encoder.");
            if (works) return candidate;
        }
        return X264;
    }

    /// <summary>Encodes a few frames of a test pattern and throws the result away.</summary>
    public static async Task<bool> WorksAsync(string ffmpeg, VideoEncoder encoder, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=gray:s=256x144:r=24:d=0.25" })
            psi.ArgumentList.Add(a);
        foreach (var a in encoder.Arguments) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-pix_fmt", "yuv420p", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
