using System.Globalization;
using SubtitleStudio.Services.Muxing;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>How far the original sound goes down while the voice speaks.</summary>
public enum DuckLevel { Little, Lower, Much }

/// <param name="AudioTrack">Which of the video's audio tracks goes under the voice (0 = the first).</param>
/// <param name="MakeDefault">The new English track is the one players start with.</param>
public sealed record DubMixRequest(string VideoPath, string VoicePath, string OutputPath, int AudioTrack = 0, DuckLevel Duck = DuckLevel.Lower,
    bool MakeDefault = true, double VoiceGainDb = 0);

public sealed record DubMixResult(string OutputPath, int AudioTracks, IReadOnlyList<string> Notes);

/// <summary>
/// The voice-over into a copy of the video, with FFmpeg: the original sound is turned down while the
/// voice speaks (a sidechain compressor keyed by the voice, so it comes back up between lines), the
/// voice is laid on top, and the mix is added as an extra English audio track (AAC, stereo). Picture,
/// the original audio tracks and the subtitles are copied as they are. The original video is never
/// changed: the new one is written under a temporary name and checked before it takes its real name.
/// </summary>
public static class DubMixer
{
    public const string TrackTitle = "English (AI voice-over)";

    /// <summary>Compressor ratio for each level. Measured with pink noise under Kokoro speech: about 8, 12 and 15 dB down while the voice speaks.</summary>
    public static double Ratio(DuckLevel level) => level switch { DuckLevel.Little => 2, DuckLevel.Much => 10, _ => 4 };

    /// <summary>"Episode 3.mkv" → "Episode 3 (English dub).mkv" (a name that isn't taken).</summary>
    public static string SuggestOutput(string videoPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        var name = Path.GetFileNameWithoutExtension(videoPath);
        var ext = SubtitleMuxer.Extension(SubtitleMuxer.ContainerFor(videoPath));
        var candidate = Path.Combine(folder, $"{name} (English dub){ext}");
        for (int n = 2; File.Exists(candidate); n++) candidate = Path.Combine(folder, $"{name} (English dub {n}){ext}");
        return candidate;
    }

    public static string Filter(int audioTrack, DuckLevel duck, double voiceGainDb)
    {
        string gain = voiceGainDb.ToString("0.#", CultureInfo.InvariantCulture), ratio = Ratio(duck).ToString("0.#", CultureInfo.InvariantCulture);
        return $"[0:a:{audioTrack}]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[orig];"
            + $"[1:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={gain}dB,asplit=2[key][voice];"
            + $"[orig][key]sidechaincompress=threshold=0.02:ratio={ratio}:attack=20:release=400:level_sc=2[ducked];"
            + "[ducked][voice]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.97:level=0[mix]";
    }

    /// <summary>
    /// The mix on its own, into an audio file (AAC in Matroska). Done apart from the copy: with the mix filter
    /// and a subtitle track in the same FFmpeg run, FFmpeg 6 stopped the new track where the subtitles first
    /// pause (1.1 s into a 12 s test video).
    /// </summary>
    public static IReadOnlyList<string> MixArgs(DubMixRequest request, string mixPath) => new List<string>
    {
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
        "-i", request.VideoPath, "-i", request.VoicePath, "-filter_complex", Filter(request.AudioTrack, request.Duck, request.VoiceGainDb),
        "-map", "[mix]", "-c:a", "aac", "-b:a", "192k", "-vn", "-sn", "-dn", "-f", "matroska", mixPath,
    };

    /// <summary>The copy: everything from the video, plus the mix as a new English track. Streams and Audio: what the new video must have.</summary>
    public static (IReadOnlyList<string> Args, IReadOnlyList<string> Notes, int Streams, int Audio) Plan(MediaInfo info, DubMixRequest request, string mixPath, string output)
    {
        bool mp4 = SubtitleMuxer.ContainerFor(output) == MuxContainer.Mp4;
        int audioCount = info.Streams.Count(s => s.Kind == StreamKind.Audio);
        if (audioCount == 0) throw new MuxException("The video has no sound to put the voice over.");
        if (request.AudioTrack < 0 || request.AudioTrack >= audioCount) throw new MuxException($"The video has {audioCount} audio track{(audioCount == 1 ? "" : "s")}; track {request.AudioTrack + 1} isn't there.");

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-i", request.VideoPath, "-i", mixPath, "-map_metadata", "0", "-map_chapters", "0" };
        var per = new List<string>();
        var notes = new List<string>();
        int index = 0, audio = 0, dropped = 0;
        foreach (var s in info.Streams)
        {
            string codec = "copy";
            if (s.Kind == StreamKind.Data || (s.Kind == StreamKind.Video && s.AttachedPicture && !mp4)
                || (mp4 && s.Kind == StreamKind.Attachment) || (mp4 && s.Kind == StreamKind.Subtitle && !s.IsTextSubtitle))
            {
                dropped++;
                continue;
            }
            if (s.Kind == StreamKind.Subtitle) codec = mp4 ? (s.Codec == "mov_text" ? "copy" : "mov_text") : s.Codec is "mov_text" or "text" ? "srt" : "copy";
            args.AddRange(new[] { "-map", $"0:{s.Index}" });
            per.AddRange(new[] { $"-c:{index}", codec });
            if (s.Kind == StreamKind.Audio)
            {
                var flags = new List<string>();
                if (s.Default && !request.MakeDefault) flags.Add("default");
                if (s.Forced) flags.Add("forced");
                if (s.HearingImpaired) flags.Add("hearing_impaired");
                per.AddRange(new[] { $"-disposition:{index}", flags.Count == 0 ? "0" : string.Join("+", flags) });
                audio++;
            }
            index++;
        }
        args.AddRange(new[] { "-map", "1:a:0" });
        per.AddRange(new[] { $"-c:{index}", "copy", $"-metadata:s:{index}", "language=eng", $"-metadata:s:{index}", "title=" + TrackTitle,
            $"-disposition:{index}", request.MakeDefault ? "default" : "0" });
        if (mp4) per.AddRange(new[] { $"-metadata:s:{index}", "handler_name=" + TrackTitle });
        index++;
        audio++;
        args.AddRange(per);
        if (mp4) args.AddRange(new[] { "-f", "mp4" });
        else args.AddRange(new[] { "-default_mode", "passthrough", "-max_interleave_delta", "0", "-f", "matroska" });
        args.Add(output);

        if (dropped > 0) notes.Add($"{dropped} stream{(dropped == 1 ? "" : "s")} {(mp4 ? "MP4 can't hold" : "of data")} left out (picture-based subtitles, fonts, covers or timecode). MKV keeps more.");
        var original = info.Streams.Where(s => s.Kind == StreamKind.Audio).ElementAt(request.AudioTrack);
        notes.Add($"The voice is mixed over audio track {request.AudioTrack + 1} ({original.Describe()}), as stereo.");
        return (args, notes, index, audio);
    }

    public static async Task<DubMixResult> RunAsync(string ffmpeg, string ffprobe, DubMixRequest request, IProgress<double>? progress, ActivityLog? log, CancellationToken ct)
    {
        var video = Path.GetFullPath(request.VideoPath);
        var target = Path.GetFullPath(request.OutputPath);
        if (!File.Exists(video)) throw new MuxException($"{Path.GetFileName(video)} isn't there any more.");
        if (!File.Exists(request.VoicePath)) throw new MuxException("The voice track is missing.");
        if (string.Equals(video, target, StringComparison.OrdinalIgnoreCase)) throw new MuxException("The dubbed video can't overwrite the one it's made from. Choose another name.");

        var info = await SubtitleMuxer.ProbeAsync(ffprobe, video, ct).ConfigureAwait(false);
        var ext = Path.GetExtension(target);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".partial" + ext);
        var mixPath = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".mix.partial.mka");
        request = request with { VideoPath = video };
        var plan = Plan(info, request, mixPath, partial);
        log?.Info("Dubbing", $"Mixing the voice into {Path.GetFileName(target)}: original sound {request.Duck switch { DuckLevel.Little => "a little lower", DuckLevel.Much => "much lower", _ => "lower" }} under the voice, new English track {(request.MakeDefault ? "first choice (default)" : "added after the others")}.");
        var mixArgs = MixArgs(request, mixPath);
        log?.Detail("Dubbing", ActivityLog.CommandLine(ffmpeg, mixArgs));
        log?.Detail("Dubbing", ActivityLog.CommandLine(ffmpeg, plan.Args));
        foreach (var n in plan.Notes) log?.Info("Dubbing", n);
        try
        {
            // The mix is most of the work (decoding and encoding the sound); the copy is quick.
            await SubtitleMuxer.RunFfmpegAsync(ffmpeg, mixArgs, info.Duration, progress is null ? null : new Progress<double>(f => progress.Report(f * 0.8)), ct).ConfigureAwait(false);
            await SubtitleMuxer.RunFfmpegAsync(ffmpeg, plan.Args, info.Duration, progress is null ? null : new Progress<double>(f => progress.Report(0.8 + f * 0.2)), ct).ConfigureAwait(false);
            var made = await SubtitleMuxer.ProbeAsync(ffprobe, partial, ct).ConfigureAwait(false);
            int audio = made.Streams.Count(s => s.Kind == StreamKind.Audio);
            if (made.Streams.Count < plan.Streams || audio != plan.Audio)
                throw new MuxException($"The new video has {made.Streams.Count} streams ({audio} audio tracks) instead of {plan.Streams} ({plan.Audio}); it was not kept.");
            if (info.Duration is { } a && made.Duration is { } b && (a - b).TotalSeconds > Math.Max(2, a.TotalSeconds * 0.01))
                throw new MuxException($"The new video is {b:h\\:mm\\:ss} long instead of {a:h\\:mm\\:ss}; it was not kept.");
            var mixLength = (await SubtitleMuxer.ProbeAsync(ffprobe, mixPath, ct).ConfigureAwait(false)).Duration;
            if (info.Duration is { } v && mixLength is { } m && (v - m).TotalSeconds > Math.Max(2, v.TotalSeconds * 0.01))
                throw new MuxException($"The mixed sound is {m:h\\:mm\\:ss} long instead of {v:h\\:mm\\:ss}; the video was not kept.");
            File.Move(partial, target, overwrite: true);
            return new DubMixResult(target, audio, plan.Notes);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            try { if (File.Exists(mixPath)) File.Delete(mixPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
