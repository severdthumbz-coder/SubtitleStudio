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

    /// <summary>A sensible floor for the small file's video bitrate: below this a 720p picture looks poor, whatever the source.</summary>
    public const int SmallVideoFloorKbps = 500;

    /// <summary>A ceiling so the target is never wasteful on a high-bitrate source (a review copy needs no more).</summary>
    public const int SmallVideoCeilingKbps = 1200;

    /// <summary>
    /// The video bitrate (kbps) to aim the small file at, chosen from the source so the result is always smaller.
    /// Taken as roughly half the source's overall bitrate (so the whole file, with its audio tracks, comes out well
    /// below the original), then held between a floor (a 720p picture needs a minimum to look decent) and a ceiling
    /// (more than this is wasted on a review copy). Returns null when the source is already so low-bitrate that even
    /// the floor wouldn't shrink it — there's nothing to gain, so the picture is copied instead of re-encoded.
    /// </summary>
    public static int? SmallVideoKbps(long sourceBytes, TimeSpan? duration)
    {
        if (sourceBytes <= 0 || duration is not { TotalSeconds: > 1 } d) return SmallVideoFloorKbps;
        int sourceKbps = (int)(sourceBytes * 8 / 1000 / d.TotalSeconds);
        // The re-encode keeps the original audio tracks and adds the English one; only the picture shrinks. If even
        // the floor-rate video plus that audio wouldn't beat the source, re-encoding can't help — copy instead.
        if (sourceKbps <= SmallVideoFloorKbps + 400) return null;
        int target = sourceKbps / 2;
        return Math.Clamp(target, SmallVideoFloorKbps, SmallVideoCeilingKbps);
    }

    /// <summary>
    /// A small keep file (for sharing or review): the picture re-encoded to at most 720 high at a set bitrate target
    /// (<paramref name="kbps"/>). Just the "-c:v …" and its options (scaling and pixel format are added by
    /// DubMixer.Plan). A bitrate cap, not a quality target: quality modes (CQP/CRF) let the encoder spend as many bits
    /// as it likes, and on an already-compressed source — especially the AMD AMF encoder, which isn't bit-thrifty —
    /// that made the "smaller" file larger than the original. The target is chosen from the source's own bitrate
    /// (SmallVideoKbps), so the result is always a fraction of the original.
    /// </summary>
    public static IReadOnlyList<string> SmallVideo(VideoEncoder encoder, int kbps)
    {
        string k = kbps + "k", max = kbps * 3 / 2 + "k", buf = kbps * 2 + "k";
        var name = encoder.Name;
        // AMF: VBR with a bitrate target and a peak cap (CQP ignored the size entirely).
        if (name.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return new[] { "h264_amf", "-quality", "balanced", "-rc", "vbr_peak", "-b:v", k, "-maxrate", max };
        if (name.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return new[] { "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-b:v", k, "-maxrate", max, "-bufsize", buf };
        if (name.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return new[] { "h264_qsv", "-b:v", k, "-maxrate", max };
        // x264: a target bitrate with a ceiling (one pass; good enough for a review copy).
        return new[] { "libx264", "-preset", "veryfast", "-b:v", k, "-maxrate", max, "-bufsize", buf };
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
