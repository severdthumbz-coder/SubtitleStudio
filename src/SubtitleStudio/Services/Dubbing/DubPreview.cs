using SubtitleStudio.Models;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// A short stretch of the dubbed video, to hear before making the whole thing: only the lines in the
/// stretch are spoken, mixed over the original sound exactly as the dubbed video will be, and rendered
/// with the picture (at most 720 lines high, quickly) to a temporary clip that plays in the Dubbing tab.
/// </summary>
public static class DubPreview
{
    /// <summary>Seconds shown before the first line, so the scene can be heard coming in.</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromSeconds(1);

    public static string Folder => Path.Combine(Path.GetTempPath(), "SubtitleStudio", "previews");

    /// <summary>Where a stretch starts: just before the line, never before the video does.</summary>
    public static TimeSpan StartFor(TimeSpan lineStart, TimeSpan length, TimeSpan? videoLength)
    {
        var start = lineStart - LeadIn;
        if (videoLength is { } v && start + length > v) start = v - length;
        return start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    /// <summary>The lines that start in the stretch, moved so the stretch starts at zero (copies).</summary>
    public static List<SubtitleCue> LinesIn(IReadOnlyList<SubtitleCue> cues, TimeSpan start, TimeSpan length)
    {
        var end = start + length;
        return cues.Where(c => c.End > c.Start && c.Start >= start && c.Start < end)
            .Select(c =>
            {
                var copy = c.Clone();
                copy.Start = c.Start - start;
                copy.End = c.End - start;
                return copy;
            })
            .ToList();
    }

    /// <summary>The preview encoder: the graphics card's as it is (fast); x264 at its fastest setting.</summary>
    public static IReadOnlyList<string> FastVideo(VideoEncoder encoder)
        => encoder.Name == VideoEncoders.X264.Name ? new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23" } : encoder.Arguments;

    /// <summary>
    /// A small keep file (for sharing or review): the picture re-encoded to at most 720 high at a modest bitrate.
    /// Just the "-c:v …" and its options (scaling and pixel format are added per stream by DubMixer.Plan). Roughly
    /// a quarter to a third the original's size for a 1080p source. The graphics card's encoder when there is one.
    /// </summary>
    public static IReadOnlyList<string> SmallVideo(VideoEncoder encoder)
    {
        var name = encoder.Name;
        if (name.Contains("amf", StringComparison.OrdinalIgnoreCase)) return new[] { "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "26", "-qp_p", "28", "-qp_b", "30" };
        if (name.Contains("nvenc", StringComparison.OrdinalIgnoreCase)) return new[] { "h264_nvenc", "-rc", "constqp", "-qp", "27", "-preset", "p4" };
        if (name.Contains("qsv", StringComparison.OrdinalIgnoreCase)) return new[] { "h264_qsv", "-global_quality", "27" };
        return new[] { "libx264", "-preset", "veryfast", "-crf", "27" };
    }

    /// <param name="bed">With the original voices removed: the stretch's sound without them (from the stretch's start).</param>
    public static IReadOnlyList<string> RenderArgs(string video, string voice, TimeSpan start, TimeSpan length, int audioTrack, DuckLevel duck,
        IReadOnlyList<string> videoEncoder, string output, string? bed = null)
    {
        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-ss", Seconds(start), "-t", Seconds(length), "-i", video,
        };
        if (bed is null)
            args.AddRange(new[] { "-i", voice, "-filter_complex", "[0:v:0]scale=-2:'min(720,ih)'[pic];" + DubMixer.Filter(audioTrack, duck, 0) });
        else
            args.AddRange(new[] { "-i", bed, "-i", voice, "-filter_complex", "[0:v:0]scale=-2:'min(720,ih)'[pic];" + DubMixer.FilterOverBed(0, 1, 2) });
        args.AddRange(new[] { "-map", "[pic]", "-map", "[mix]" });
        args.AddRange(videoEncoder);
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-t", Seconds(length), "-f", "matroska", output });
        return args;
    }

    /// <summary>Clears clips left by earlier previews (they're only for listening once).</summary>
    public static void ClearOld()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.GetFiles(Folder, "dub-*.*"))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
