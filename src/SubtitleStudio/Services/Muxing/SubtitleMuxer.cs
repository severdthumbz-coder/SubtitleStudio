using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.Services.Muxing;

public enum StreamKind { Video, Audio, Subtitle, Attachment, Data }

public enum MuxContainer { Mkv, Mp4 }

/// <summary>One stream already in the video, as ffprobe reports it.</summary>
public sealed record MediaStream(int Index, StreamKind Kind, string Codec, string? Language, string? Title,
    bool Default, bool Forced, bool HearingImpaired, bool AttachedPicture = false)
{
    public static readonly IReadOnlySet<string> TextSubtitleCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

    public bool IsTextSubtitle => Kind == StreamKind.Subtitle && TextSubtitleCodecs.Contains(Codec);

    /// <summary>"Korean subtitles (SubRip), default".</summary>
    public string Describe()
    {
        var what = Kind switch
        {
            StreamKind.Video => AttachedPicture ? "Cover picture" : "Video",
            StreamKind.Audio => "Audio",
            StreamKind.Subtitle => "Subtitles",
            StreamKind.Attachment => "Attachment",
            _ => "Data",
        };
        var named = Kind is StreamKind.Audio or StreamKind.Subtitle && TrackLanguages.FromTrackTag(Language) is not null;
        var lang = named ? TrackLanguages.NameOf(Language) + " " : string.Empty;
        var flags = new List<string>();
        if (Default) flags.Add("default");
        if (Forced) flags.Add("forced");
        if (HearingImpaired) flags.Add("SDH");
        return $"{(named ? lang + what.ToLowerInvariant() : what)} ({Codec}{(string.IsNullOrEmpty(Title) ? "" : ", \"" + Title + "\"")})"
               + (flags.Count > 0 ? ", " + string.Join(", ", flags) : string.Empty);
    }
}

public sealed record MediaInfo(IReadOnlyList<MediaStream> Streams, TimeSpan? Duration, string FormatName);

/// <summary>A subtitle file to add as a track.</summary>
/// <param name="Language">The app's code ("ko"); null: no language.</param>
/// <param name="Title">The track name players show (null: none).</param>
public sealed record SubtitleTrack(string Path, string? Language, string? Title, bool Default, bool Forced, bool HearingImpaired);

/// <param name="OutputPath">Where the new video goes (ignored when replacing the original).</param>
/// <param name="KeepExistingSubtitles">Keep the subtitle tracks the video already has.</param>
/// <param name="ReplaceOriginal">The original goes (to the Recycle Bin) and the new video takes its name.</param>
public sealed record MuxRequest(string VideoPath, IReadOnlyList<SubtitleTrack> Tracks, MuxContainer Container, string OutputPath,
    bool KeepExistingSubtitles = true, bool ReplaceOriginal = false);

/// <summary>The FFmpeg arguments, and what will be left out (and why).</summary>
public sealed record MuxPlan(IReadOnlyList<string> Arguments, IReadOnlyList<string> Notes, int StreamCount, int SubtitleCount);

public sealed record MuxResult(string OutputPath, int SubtitlesAdded, int SubtitleTracks, IReadOnlyList<string> Notes, string? ReplacedOriginal);

/// <summary>A reason the tracks can't be added, in words for the user.</summary>
public sealed class MuxException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Adds subtitle files to a video as tracks, with FFmpeg and without re-encoding: picture and sound are
/// copied as they are. Each subtitle is read by the app and written out clean (SRT, or ASS for ASS/SSA into
/// MKV, so styling is kept); MP4 stores subtitles as mov_text. Each track gets its language (ISO 639-2), a
/// name, and the default / forced / hearing-impaired flags. The new video is written beside the target
/// under a temporary name and checked (all streams there, same length) before it takes its real name, so a
/// failed run never leaves a broken video, and the original is only replaced after that check.
/// </summary>
public sealed class SubtitleMuxer
{
    private static readonly HashSet<string> Mp4Video = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "av1", "vp9", "mpeg4", "mpeg2video", "mpeg1video", "mjpeg", "png" };
    private static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac", "opus", "flac", "dts", "mp2" };

    private readonly SubtitleFormatRegistry _formats;
    private readonly ActivityLog? _log;

    public SubtitleMuxer(SubtitleFormatRegistry formats, ActivityLog? log = null)
    {
        _formats = formats;
        _log = log;
    }

    public static string Extension(MuxContainer c) => c == MuxContainer.Mp4 ? ".mp4" : ".mkv";

    /// <summary>MKV for anything but an .mp4/.m4v/.mov file (which stays MP4 unless MKV is chosen).</summary>
    public static MuxContainer ContainerFor(string videoPath)
        => Path.GetExtension(videoPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" ? MuxContainer.Mp4 : MuxContainer.Mkv;

    /// <summary>"Episode 1.mkv" → "Episode 1 (subtitles).mkv" (a name that isn't taken).</summary>
    public static string SuggestOutput(string videoPath, MuxContainer container)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var ext = Extension(container);
        var candidate = Path.Combine(folder, $"{name} (subtitles){ext}");
        for (int n = 2; File.Exists(candidate); n++) candidate = Path.Combine(folder, $"{name} (subtitles {n}){ext}");
        return candidate;
    }

    /// <summary>"English", "English (Forced)", "English (SDH)".</summary>
    public static string DefaultTitle(string? language, bool forced, bool hearingImpaired)
    {
        var name = string.IsNullOrEmpty(language) ? "Subtitles" : TrackLanguages.NameOf(language);
        return forced ? name + " (Forced)" : hearingImpaired ? name + " (SDH)" : name;
    }

    // ---------------- Probe ----------------

    public static async Task<MediaInfo> ProbeAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (exit, output, error) = await RunTextAsync(ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }, ct).ConfigureAwait(false);
        if (exit != 0) throw new MuxException($"ffprobe couldn't read {Path.GetFileName(path)}: {LastLine(error)}");
        return ParseProbe(output);
    }

    public static MediaInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var list))
        {
            foreach (var s in list.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                var kind = type switch
                {
                    "video" => StreamKind.Video,
                    "audio" => StreamKind.Audio,
                    "subtitle" => StreamKind.Subtitle,
                    "attachment" => StreamKind.Attachment,
                    _ => StreamKind.Data,
                };
                string? lang = null, title = null;
                if (s.TryGetProperty("tags", out var tags))
                {
                    foreach (var t in tags.EnumerateObject())
                    {
                        if (t.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = t.Value.GetString();
                        else if (t.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = t.Value.GetString();
                    }
                }
                bool def = false, forced = false, hi = false, pic = false;
                if (s.TryGetProperty("disposition", out var d))
                {
                    def = Flag(d, "default");
                    forced = Flag(d, "forced");
                    hi = Flag(d, "hearing_impaired");
                    pic = Flag(d, "attached_pic");
                }
                streams.Add(new MediaStream(s.TryGetProperty("index", out var ix) ? ix.GetInt32() : streams.Count, kind,
                    Str(s, "codec_name") ?? (kind == StreamKind.Attachment ? "attachment" : "unknown"), lang, title, def, forced, hi, pic));
            }
        }
        TimeSpan? duration = null;
        string format = string.Empty;
        if (root.TryGetProperty("format", out var f))
        {
            format = Str(f, "format_name") ?? string.Empty;
            if (Str(f, "duration") is { } ds && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
                duration = TimeSpan.FromSeconds(secs);
        }
        return new MediaInfo(streams, duration, format);

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;
    }

    // ---------------- Plan ----------------

    /// <param name="subtitleInputs">The prepared subtitle files, one per track in the request, in order.</param>
    public static MuxPlan Plan(MediaInfo info, MuxRequest request, IReadOnlyList<string> subtitleInputs, string outputPath)
    {
        bool mp4 = request.Container == MuxContainer.Mp4;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", request.VideoPath };
        foreach (var s in subtitleInputs) args.AddRange(new[] { "-i", s });

        var notes = new List<string>();
        var perStream = new List<string>();
        int output = 0, subtitleOut = 0, droppedSubs = 0, droppedImageSubs = 0, droppedAttachments = 0, droppedData = 0, droppedPictures = 0;
        bool newDefault = request.Tracks.Any(t => t.Default);
        args.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0" });

        foreach (var s in info.Streams)
        {
            string codec = "copy";
            switch (s.Kind)
            {
                case StreamKind.Video when s.AttachedPicture && !mp4:
                    droppedPictures++;
                    continue;
                case StreamKind.Video when mp4 && !Mp4Video.Contains(s.Codec):
                    throw new MuxException($"The video is {s.Codec}, which MP4 can't hold without re-encoding. Choose MKV.");
                case StreamKind.Audio when mp4 && !Mp4Audio.Contains(s.Codec):
                    throw new MuxException($"An audio track is {s.Codec}, which MP4 can't hold without re-encoding. Choose MKV.");
                case StreamKind.Subtitle when !request.KeepExistingSubtitles:
                    droppedSubs++;
                    continue;
                case StreamKind.Subtitle when mp4 && !s.IsTextSubtitle:
                    droppedImageSubs++;
                    continue;
                case StreamKind.Subtitle:
                    codec = mp4 ? (s.Codec == "mov_text" ? "copy" : "mov_text") : s.Codec is "mov_text" or "text" ? "srt" : "copy";
                    break;
                case StreamKind.Attachment when mp4:
                    droppedAttachments++;
                    continue;
                case StreamKind.Data:
                    droppedData++;
                    continue;
            }
            args.AddRange(new[] { "-map", $"0:{s.Index}" });
            perStream.AddRange(new[] { $"-c:{output}", codec });
            if (s.Kind == StreamKind.Subtitle)
            {
                bool def = s.Default && !newDefault;
                perStream.AddRange(new[] { $"-disposition:{output}", Disposition(def, s.Forced, s.HearingImpaired) });
                subtitleOut++;
            }
            output++;
        }

        for (int i = 0; i < request.Tracks.Count; i++)
        {
            var t = request.Tracks[i];
            args.AddRange(new[] { "-map", $"{i + 1}:0" });
            perStream.AddRange(new[] { $"-c:{output}", mp4 ? "mov_text" : "copy" });
            perStream.AddRange(new[] { $"-metadata:s:{output}", "language=" + (TrackLanguages.ToIso6392(t.Language) ?? "und") });
            if (!string.IsNullOrWhiteSpace(t.Title))
            {
                perStream.AddRange(new[] { $"-metadata:s:{output}", "title=" + t.Title.Trim() });
                if (mp4) perStream.AddRange(new[] { $"-metadata:s:{output}", "handler_name=" + t.Title.Trim() });
            }
            perStream.AddRange(new[] { $"-disposition:{output}", Disposition(t.Default, t.Forced, t.HearingImpaired) });
            subtitleOut++;
            output++;
        }

        args.AddRange(perStream);
        if (mp4) args.AddRange(new[] { "-f", "mp4" });
        else args.AddRange(new[] { "-default_mode", "infer_no_subs", "-max_interleave_delta", "0", "-f", "matroska" });
        args.Add(outputPath);

        if (droppedSubs > 0) notes.Add($"{droppedSubs} subtitle track{(droppedSubs == 1 ? "" : "s")} already in the video left out (as chosen).");
        if (droppedImageSubs > 0) notes.Add($"{droppedImageSubs} picture-based subtitle track{(droppedImageSubs == 1 ? "" : "s")} (PGS or VobSub) left out: MP4 can't hold them. MKV keeps them.");
        if (droppedAttachments > 0) notes.Add($"{droppedAttachments} attachment{(droppedAttachments == 1 ? "" : "s")} (fonts) left out: MP4 can't hold them.");
        if (droppedPictures > 0) notes.Add("The cover picture is left out (MKV keeps covers as attachments, which this doesn't convert).");
        if (droppedData > 0) notes.Add($"{droppedData} data stream{(droppedData == 1 ? "" : "s")} (such as timecode) left out.");
        if (mp4 && request.Tracks.Any(t => !t.Path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
            notes.Add("MP4 subtitles are plain text (mov_text): ASS styling and positions aren't kept. MKV keeps them.");
        return new MuxPlan(args, notes, output, subtitleOut);
    }

    private static string Disposition(bool def, bool forced, bool hearingImpaired)
    {
        var flags = new List<string>();
        if (def) flags.Add("default");
        if (forced) flags.Add("forced");
        if (hearingImpaired) flags.Add("hearing_impaired");
        return flags.Count == 0 ? "0" : string.Join("+", flags);
    }

    // ---------------- Run ----------------

    /// <param name="recycle">Sends a file to the Recycle Bin (true when done); needed to replace the original.</param>
    /// <param name="progress">0..1 of the copy.</param>
    public async Task<MuxResult> RunAsync(string ffmpeg, string ffprobe, MuxRequest request, Func<string, bool>? recycle, IProgress<double>? progress, CancellationToken ct)
    {
        if (request.Tracks.Count == 0) throw new MuxException("Choose at least one subtitle file to add.");
        var video = Path.GetFullPath(request.VideoPath);
        if (!File.Exists(video)) throw new MuxException($"{Path.GetFileName(video)} isn't there any more.");
        var ext = Extension(request.Container);
        var target = request.ReplaceOriginal ? Path.ChangeExtension(video, ext) : Path.GetFullPath(request.OutputPath);
        if (!request.ReplaceOriginal && string.Equals(target, video, StringComparison.OrdinalIgnoreCase))
            throw new MuxException("The new video can't overwrite the one it's made from. Choose another name, or \"Replace the original\".");
        if (request.ReplaceOriginal && !string.Equals(target, video, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            throw new MuxException($"{Path.GetFileName(target)} already exists next to the original. Move it, or save the new video under another name.");
        if (request.ReplaceOriginal && recycle is null) throw new MuxException("The original can't be replaced here.");

        var info = await ProbeAsync(ffprobe, video, ct).ConfigureAwait(false);
        if (!info.Streams.Any(s => s.Kind is StreamKind.Video or StreamKind.Audio))
            throw new MuxException($"{Path.GetFileName(video)} has no video or audio that FFmpeg can read.");

        var work = Directory.CreateTempSubdirectory("SubtitleStudio-mux-").FullName;
        var partial = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".partial" + ext);
        bool keepPartial = false;
        try
        {
            var inputs = new List<string>();
            for (int i = 0; i < request.Tracks.Count; i++)
                inputs.Add(Prepare(request.Tracks[i], request.Container, work, i));

            var plan = Plan(info, request, inputs, partial);
            _log?.Info("Mux", $"{Path.GetFileName(video)}: adding {request.Tracks.Count} subtitle track{(request.Tracks.Count == 1 ? "" : "s")} ("
                + string.Join(", ", request.Tracks.Select(Describe)) + $") into {(request.Container == MuxContainer.Mp4 ? "MP4" : "MKV")}, picture and sound copied as they are.");
            _log?.Detail("Mux", ActivityLog.CommandLine(ffmpeg, plan.Arguments));
            foreach (var n in plan.Notes) _log?.Info("Mux", n);

            await RunFfmpegAsync(ffmpeg, plan.Arguments, info.Duration, progress, ct).ConfigureAwait(false);

            // Check the result before it takes the real name.
            var made = await ProbeAsync(ffprobe, partial, ct).ConfigureAwait(false);
            int subs = made.Streams.Count(s => s.Kind == StreamKind.Subtitle);
            if (made.Streams.Count < plan.StreamCount || subs != plan.SubtitleCount)
                throw new MuxException($"The new video has {made.Streams.Count} streams ({subs} subtitle tracks) instead of {plan.StreamCount} ({plan.SubtitleCount}); it was not kept.");
            // Shorter means something was cut off. (Longer is fine: a subtitle can end after the picture does.)
            if (info.Duration is { } a && made.Duration is { } b && (a - b).TotalSeconds > Math.Max(2, a.TotalSeconds * 0.01))
                throw new MuxException($"The new video is {b:h\\:mm\\:ss} long instead of {a:h\\:mm\\:ss}; it was not kept.");

            string? replaced = null;
            if (request.ReplaceOriginal)
            {
                keepPartial = true;
                if (!recycle!(video)) throw new MuxException($"{Path.GetFileName(video)} couldn't be moved to the Recycle Bin (is it open in a player?), so it was left as it is. The new video is saved as {Path.GetFileName(partial)}.");
                replaced = video;
            }
            File.Move(partial, target, overwrite: true);
            _log?.Success("Mux", $"Saved {Path.GetFileName(target)}: {subs} subtitle track{(subs == 1 ? "" : "s")} in all"
                + (replaced is null ? "." : $"; the original {Path.GetFileName(video)} is in the Recycle Bin."));
            return new MuxResult(target, request.Tracks.Count, subs, plan.Notes, replaced);
        }
        catch
        {
            // A half-written or rejected video never stays (unless the original couldn't be recycled: then it's the finished result).
            if (!keepPartial && File.Exists(partial)) TryDelete(partial);
            throw;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The subtitle read by the app and written out clean: ASS/SSA stays ASS in MKV; everything else becomes SRT.</summary>
    private string Prepare(SubtitleTrack track, MuxContainer container, string folder, int index)
    {
        SubtitleDocument doc;
        try
        {
            doc = _formats.Load(track.Path);
        }
        catch (Exception ex) when (ex is SubtitleFormatException or IOException or UnauthorizedAccessException)
        {
            throw new MuxException($"{Path.GetFileName(track.Path)} couldn't be read: {ex.Message}", ex);
        }
        if (doc.Cues.Count == 0) throw new MuxException($"{Path.GetFileName(track.Path)} has no subtitles in it.");
        var keepAss = container == MuxContainer.Mkv && doc.Format is "ass" or "ssa";
        var path = Path.Combine(folder, $"track{index}{(keepAss ? ".ass" : ".srt")}");
        _formats.Save(doc, path, keepAss ? "ass" : "srt");
        return path;
    }

    private static string Describe(SubtitleTrack t)
    {
        var flags = new List<string>();
        if (t.Default) flags.Add("default");
        if (t.Forced) flags.Add("forced");
        if (t.HearingImpaired) flags.Add("SDH");
        return $"{TrackLanguages.NameOf(t.Language)}{(flags.Count > 0 ? " " + string.Join("/", flags) : "")} from {Path.GetFileName(t.Path)}";
    }

    private static async Task RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, TimeSpan? duration, IProgress<double>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new MuxException("FFmpeg couldn't be started: " + ex.Message, ex);
        }
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
        {
            // -progress: "out_time_us=12345678" (microseconds; older builds call it out_time_ms, also microseconds).
            if (duration is not { TotalSeconds: > 0 } d) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            if (key is not ("out_time_us" or "out_time_ms")) continue;
            if (long.TryParse(line[(eq + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                progress?.Report(Math.Clamp(us / 1e6 / d.TotalSeconds, 0, 1));
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new MuxException("FFmpeg stopped: " + LastLine(error));
        progress?.Report(1);
    }

    private static async Task<(int Exit, string Out, string Err)> RunTextAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no details." : lines[^1];
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
