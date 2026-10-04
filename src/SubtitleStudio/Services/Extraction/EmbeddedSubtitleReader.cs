using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SubtitleStudio.Models;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Muxing;
using SubtitleStudio.Services.Subtitles;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.Extraction;

/// <summary>A subtitle track inside a video, or in a picture-subtitle file next to it (.sup, .idx/.sub).</summary>
/// <param name="SourcePath">The file the track is in.</param>
public sealed record EmbeddedTrack(string SourcePath, MediaStream Stream, bool ExternalFile)
{
    public static readonly IReadOnlySet<string> PictureCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hdmv_pgs_subtitle", "dvd_subtitle" };

    public bool IsText => Stream.IsTextSubtitle;

    public bool IsPicture => PictureCodecs.Contains(Stream.Codec);

    public bool CanRead => IsText || IsPicture;

    /// <summary>The app's language code ("ko"), when the track says.</summary>
    public string? Language => TrackLanguages.FromTrackTag(Stream.Language);

    public string FormatName => Stream.Codec switch
    {
        "subrip" or "srt" => "SubRip text",
        "ass" or "ssa" => "ASS (styled text)",
        "mov_text" => "MP4 text",
        "webvtt" => "WebVTT text",
        "hdmv_pgs_subtitle" => "PGS pictures (Blu-ray)",
        "dvd_subtitle" => "VobSub pictures (DVD)",
        "dvb_subtitle" => "DVB pictures (TV)",
        "eia_608" => "Closed captions",
        _ => Stream.Codec,
    };

    /// <summary>"English · SubRip text · default · forced · "SDH"".</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { TrackLanguages.NameOf(Stream.Language), FormatName };
            if (!string.IsNullOrWhiteSpace(Stream.Title)) parts.Add($"\"{Stream.Title}\"");
            if (Stream.Default) parts.Add("default");
            if (Stream.Forced) parts.Add("forced");
            if (Stream.HearingImpaired) parts.Add("SDH");
            if (ExternalFile) parts.Add("file " + Path.GetFileName(SourcePath));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Why it can't be read (empty when it can).</summary>
    public string CannotReadReason => CanRead ? string.Empty
        : Stream.Codec == "dvb_subtitle" ? "TV (DVB) picture subtitles aren't supported yet."
        : $"{Stream.Codec} subtitles can't be read.";
}

/// <summary>What reading a track gave.</summary>
/// <param name="Pictures">Picture subtitles in the track (0 for text).</param>
/// <param name="Unreadable">Pictures OCR found no text in.</param>
public sealed record ExtractedTrack(SubtitleDocument Document, int Pictures, int Unreadable, string? OcrLanguage);

public sealed record ExtractionStep(double Fraction, string Text, string? LatestText = null);

/// <summary>
/// Subtitles already inside a video. Text tracks (SubRip, ASS, MP4 text, WebVTT) are copied out by
/// FFmpeg exactly. Picture tracks (PGS from Blu-ray, VobSub from DVD) are decoded here and each picture
/// is read with OCR (Windows OCR in the app): the letters' fill is separated from the outline and given
/// to the OCR as dark text on white, enlarged to a comfortable size. Timings come from the track itself.
/// </summary>
public sealed class EmbeddedSubtitleReader
{
    /// <summary>Text lines are enlarged (or reduced) to about this height for reading.</summary>
    public const double TargetLinePixels = 44;

    private readonly SubtitleFormatRegistry _formats;
    private readonly ITextRecognizer? _ocr;
    private readonly ActivityLog? _log;

    public EmbeddedSubtitleReader(SubtitleFormatRegistry formats, ITextRecognizer? ocr, ActivityLog? log = null)
    {
        _formats = formats;
        _ocr = ocr;
        _log = log;
    }

    // ---------------- Listing ----------------

    /// <summary>The subtitle tracks in the video, and in .sup / .idx files beside it.</summary>
    public static async Task<IReadOnlyList<EmbeddedTrack>> ListAsync(string ffprobe, string videoPath, IEnumerable<string> pictureFiles, CancellationToken ct)
    {
        var tracks = new List<EmbeddedTrack>();
        var info = await SubtitleMuxer.ProbeAsync(ffprobe, videoPath, ct).ConfigureAwait(false);
        tracks.AddRange(info.Streams.Where(s => s.Kind == StreamKind.Subtitle).Select(s => new EmbeddedTrack(videoPath, s, false)));
        foreach (var file in pictureFiles)
        {
            try
            {
                var side = await SubtitleMuxer.ProbeAsync(ffprobe, file, ct).ConfigureAwait(false);
                var sidecar = SidecarDetector.TryParse(videoPath, file);
                foreach (var s in side.Streams.Where(s => s.Kind == StreamKind.Subtitle))
                {
                    // A .sup has no language inside: take it from the file name ("Film.en.sup").
                    var stream = s.Language is null or "und" && sidecar?.Language is { } lang
                        ? s with { Language = TrackLanguages.ToIso6392(lang) ?? lang, Forced = s.Forced || sidecar.Forced }
                        : s;
                    tracks.Add(new EmbeddedTrack(file, stream, true));
                }
            }
            catch (MuxException)
            {
                // Not readable by FFmpeg: not listed.
            }
        }
        return tracks;
    }

    /// <summary>Picture-subtitle files next to a video: .sup, and .idx (whose .sub holds the pictures).</summary>
    public static IEnumerable<string> PictureFilesBeside(string videoPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        string[] files;
        try { files = Directory.GetFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        return SidecarDetector.Detect(videoPath, files)
            .Select(s => s.FilePath)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".sup" or ".idx")
            .ToList();
    }

    // ---------------- Reading ----------------

    public async Task<ExtractedTrack> ReadAsync(string ffmpeg, string ffprobe, EmbeddedTrack track, IProgress<ExtractionStep>? progress, CancellationToken ct)
    {
        if (!track.CanRead) throw new MuxException(track.CannotReadReason);
        var work = Directory.CreateTempSubdirectory("SubtitleStudio-extract-").FullName;
        try
        {
            return track.IsText
                ? await ReadTextAsync(ffmpeg, track, work, progress, ct).ConfigureAwait(false)
                : await ReadPicturesAsync(ffmpeg, ffprobe, track, work, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<ExtractedTrack> ReadTextAsync(string ffmpeg, EmbeddedTrack track, string work, IProgress<ExtractionStep>? progress, CancellationToken ct)
    {
        bool ass = track.Stream.Codec is "ass" or "ssa";
        var output = Path.Combine(work, ass ? "track.ass" : "track.srt");
        progress?.Report(new ExtractionStep(0, "Copying the text track out of the video..."));
        await RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", track.SourcePath,
            "-map", $"0:{track.Stream.Index}", "-c:s", ass ? "copy" : "srt", "-f", ass ? "ass" : "srt", output }, ct).ConfigureAwait(false);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new MuxException("The track is empty.");
        var doc = _formats.Load(output);
        doc.SourcePath = null;
        doc.Language = track.Language;
        doc.Forced = track.Stream.Forced;
        doc.HearingImpaired = track.Stream.HearingImpaired;
        _log?.Success("Extract", $"{Path.GetFileName(track.SourcePath)}: {track.Label}: {doc.Cues.Count} cues copied out as they are.");
        progress?.Report(new ExtractionStep(1, $"{doc.Cues.Count} cues."));
        return new ExtractedTrack(doc, 0, 0, null);
    }

    private async Task<ExtractedTrack> ReadPicturesAsync(string ffmpeg, string ffprobe, EmbeddedTrack track, string work, IProgress<ExtractionStep>? progress, CancellationToken ct)
    {
        if (_ocr is not { IsAvailable: true }) throw new MuxException("Picture subtitles are read with Windows OCR, which isn't available: " + (_ocr?.UnavailableReason ?? "no OCR engine."));
        string? previousLanguage = _ocr.LanguageTag;
        string? ocrLanguage = ChooseOcrLanguage(track.Language);
        if (track.Language is { } want && ocrLanguage is null)
            throw new MuxException($"The track is {TrackLanguages.NameOf(want)}, and Windows OCR for {TrackLanguages.NameOf(want)} isn't installed. Add it in Windows Settings > Time & language > Language & region > add {TrackLanguages.NameOf(want)} (with its optional features), then try again.");
        ocrLanguage ??= previousLanguage;
        try
        {
            if (ocrLanguage is not null && ocrLanguage != _ocr.LanguageTag) _ocr.SetLanguage(ocrLanguage);

            progress?.Report(new ExtractionStep(0, "Reading the pictures out of the track..."));
            List<SubtitlePicture> pictures;
            if (track.Stream.Codec == "hdmv_pgs_subtitle")
            {
                string sup = track.SourcePath;
                if (!sup.EndsWith(".sup", StringComparison.OrdinalIgnoreCase))
                {
                    sup = Path.Combine(work, "track.sup");
                    await RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", track.SourcePath,
                        "-map", $"0:{track.Stream.Index}", "-c", "copy", "-f", "sup", sup }, ct).ConfigureAwait(false);
                }
                await using var stream = File.OpenRead(sup);
                pictures = PgsDecoder.Decode(stream);
            }
            else
            {
                var (packets, header) = await ReadPacketsAsync(ffprobe, track, ct).ConfigureAwait(false);
                pictures = VobSubDecoder.Decode(packets, VobSubDecoder.ParsePalette(header));
            }
            pictures = Merge(pictures);
            _log?.Info("Extract", $"{Path.GetFileName(track.SourcePath)}: {track.Label}: {pictures.Count} pictures; reading them with Windows OCR ({_ocr.LanguageTag}).");

            var doc = new SubtitleDocument { Language = track.Language, Forced = track.Stream.Forced, HearingImpaired = track.Stream.HearingImpaired };
            var cache = new Dictionary<string, string>();
            int unreadable = 0, read = 0;
            for (int i = 0; i < pictures.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pic = pictures[i];
                var key = pic.InkKey();
                if (!cache.TryGetValue(key, out var text))
                {
                    text = await ReadPictureAsync(pic, ct).ConfigureAwait(false);
                    cache[key] = text;
                    read++;
                }
                if (text.Length == 0)
                {
                    unreadable++;
                    continue;
                }
                var end = pic.End ?? (i + 1 < pictures.Count ? pictures[i + 1].Start : pic.Start + TimeSpan.FromSeconds(4));
                if (end <= pic.Start) end = pic.Start + TimeSpan.FromSeconds(1);
                doc.Cues.Add(new SubtitleCue
                {
                    Index = doc.Cues.Count + 1, Start = pic.Start, End = end,
                    Text = (pic.AtTop ? "{\\an8}" : string.Empty) + text,
                });
                if (i % 5 == 0 || i == pictures.Count - 1)
                    progress?.Report(new ExtractionStep((i + 1) / (double)pictures.Count, $"Reading picture {i + 1} of {pictures.Count}...", text.Replace('\n', ' ')));
            }
            _log?.Success("Extract", $"{Path.GetFileName(track.SourcePath)}: {doc.Cues.Count} cues from {pictures.Count} pictures ({read} read, the rest identical)"
                + (unreadable > 0 ? $"; {unreadable} picture{(unreadable == 1 ? "" : "s")} had no text OCR could read." : "."));
            return new ExtractedTrack(doc, pictures.Count, unreadable, _ocr.LanguageTag);
        }
        finally
        {
            if (previousLanguage is not null && previousLanguage != _ocr.LanguageTag) _ocr.SetLanguage(previousLanguage);
        }
    }

    /// <summary>The installed OCR language for the track's language ("ko" → "ko-KR"); null when none is.</summary>
    private string? ChooseOcrLanguage(string? language)
    {
        if (_ocr is null || language is null) return null;
        var tags = _ocr.AvailableLanguages;
        var code = language == "zh" ? "zh" : language;
        return tags.FirstOrDefault(t => t.Equals(code, StringComparison.OrdinalIgnoreCase))
               ?? tags.FirstOrDefault(t => t.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A picture sent again unchanged (PGS does this) continues the same cue.</summary>
    public static List<SubtitlePicture> Merge(List<SubtitlePicture> pictures)
    {
        var merged = new List<SubtitlePicture>();
        foreach (var p in pictures.OrderBy(p => p.Start))
        {
            if (merged.Count > 0 && merged[^1] is var last && last.SameAs(p) && (last.End is null || last.End >= p.Start - TimeSpan.FromMilliseconds(50)))
            {
                last.End = p.End; // the copy carries on: its end (or none yet) is the cue's end
                continue;
            }
            if (merged.Count > 0 && merged[^1].End is { } e && e > p.Start) merged[^1].End = p.Start;
            merged.Add(p);
        }
        return merged.Where(p => p.Ink.Any(b => b != 0)).ToList();
    }

    /// <summary>The picture as dark letters on white, cropped, padded and scaled to about 44 px a line, then read.</summary>
    public async Task<string> ReadPictureAsync(SubtitlePicture pic, CancellationToken ct)
    {
        var image = ImageFor(pic, _ocr?.MaxImageDimension ?? 4000);
        if (image is null) return string.Empty;
        var lines = await _ocr!.RecognizeAsync(image, ct).ConfigureAwait(false);
        var ordered = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        // Pieces of one line (OCR sometimes splits them) are joined with a space; lines with a line break.
        var rows = new List<List<OcrLine>>();
        foreach (var l in ordered)
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].Y + rows[^1][0].Height / 2 - (l.Y + l.Height / 2)) < l.Height * 0.5) rows[^1].Add(l);
            else rows.Add(new List<OcrLine> { l });
        }
        var text = string.Join("\n", rows.Select(r => string.Join(" ", r.OrderBy(l => l.X).Select(l => Tidy(l.Text))))).Trim();
        return FixCommonMisreads(text, _ocr.LanguageTag);
    }

    /// <summary>
    /// Mistakes OCR makes on subtitle fonts, fixed only where the right reading is certain: "|" (never in
    /// dialogue) is the capital I, and in English a lone "l" is "I" ("l think" → "I think", "l'm" → "I'm").
    /// </summary>
    public static string FixCommonMisreads(string text, string? languageTag)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\|", "I");
        if (languageTag is null || languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}\p{N}])l(?=['’]?(?:m|ll|ve|d|s)?(?![\p{L}\p{N}]))", "I");
        return text;
    }

    private static string Tidy(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s{2,}", " ");

    public static FrameSample? ImageFor(SubtitlePicture pic, int maxDimension)
    {
        int w = pic.Width, h = pic.Height;
        int left = w, right = -1, top = h, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (pic.Ink[y * w + x] != 0)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
        if (right < 0) return null;

        // Line height: the tallest run of rows with ink (one text line, ascenders to descenders).
        var lineHeights = new List<int>();
        int run = 0;
        for (int y = top; y <= bottom + 1; y++)
        {
            bool any = false;
            if (y <= bottom)
                for (int x = left; x <= right && !any; x++) any = pic.Ink[y * w + x] != 0;
            if (any) run++;
            else if (run > 0) { lineHeights.Add(run); run = 0; }
        }
        double line = lineHeights.Count == 0 ? bottom - top + 1 : lineHeights.Where(v => v >= 4).DefaultIfEmpty(lineHeights.Max()).Max();
        double scale = Math.Clamp(TargetLinePixels / Math.Max(1, line), 0.5, 4);
        int cw = right - left + 1, ch = bottom - top + 1;
        int pad = (int)Math.Ceiling(TargetLinePixels * 0.5);
        int ow = (int)Math.Ceiling(cw * scale) + 2 * pad, oh = (int)Math.Ceiling(ch * scale) + 2 * pad;
        if (Math.Max(ow, oh) > maxDimension)
        {
            double fit = (maxDimension - 2.0 * pad) / Math.Max(cw * scale, ch * scale);
            scale *= fit;
            ow = (int)Math.Ceiling(cw * scale) + 2 * pad;
            oh = (int)Math.Ceiling(ch * scale) + 2 * pad;
        }
        var bgra = new byte[ow * oh * 4];
        Array.Fill(bgra, (byte)255);
        for (int y = 0; y < oh - 2 * pad; y++)
        {
            double sy = (y + 0.5) / scale - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, ch - 1), y1 = Math.Min(y0 + 1, ch - 1);
            double fy = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < ow - 2 * pad; x++)
            {
                double sx = (x + 0.5) / scale - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, cw - 1), x1 = Math.Min(x0 + 1, cw - 1);
                double fx = Math.Clamp(sx - x0, 0, 1);
                double a = Ink(x0, y0) * (1 - fx) + Ink(x1, y0) * fx;
                double b = Ink(x0, y1) * (1 - fx) + Ink(x1, y1) * fx;
                byte v = (byte)(255 - Math.Round(a * (1 - fy) + b * fy));
                int o = ((y + pad) * ow + x + pad) * 4;
                bgra[o] = bgra[o + 1] = bgra[o + 2] = v;
            }
        }
        return new FrameSample(ow, oh, bgra, pic.Start);

        double Ink(int x, int y) => pic.Ink[(top + y) * w + left + x];
    }

    // ---------------- FFmpeg / ffprobe ----------------

    /// <summary>The track's packets (with their bytes) and its header, through ffprobe.</summary>
    private static async Task<(List<VobSubDecoder.Packet> Packets, string? Header)> ReadPacketsAsync(string ffprobe, EmbeddedTrack track, CancellationToken ct)
    {
        var json = await RunAsync(ffprobe, new[] { "-v", "error", "-select_streams", track.Stream.Index.ToString(CultureInfo.InvariantCulture),
            "-show_streams", "-show_packets", "-show_data", "-print_format", "json", track.SourcePath }, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        string? header = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
                if (s.TryGetProperty("extradata", out var ed) && ed.GetString() is { } dump)
                    header = Encoding.Latin1.GetString(HexDump(dump));
        var packets = new List<VobSubDecoder.Packet>();
        if (doc.RootElement.TryGetProperty("packets", out var list))
        {
            foreach (var p in list.EnumerateArray())
            {
                if (!p.TryGetProperty("data", out var data) || data.GetString() is not { } dump) continue;
                var time = Seconds(p, "pts_time");
                if (time is null) continue;
                packets.Add(new VobSubDecoder.Packet(time.Value, Seconds(p, "duration_time"), HexDump(dump)));
            }
        }
        return (packets, header);

        static TimeSpan? Seconds(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
               && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;
    }

    /// <summary>ffprobe's "00000000: 1600 1302 ...  ascii" dump back to bytes.</summary>
    public static byte[] HexDump(string dump)
    {
        var bytes = new List<byte>();
        foreach (var raw in dump.Split('\n'))
        {
            if (raw.Length < 11 || raw[8] != ':') continue;
            var hex = raw.Substring(10, Math.Min(39, raw.Length - 10)).Replace(" ", string.Empty);
            for (int i = 0; i + 1 < hex.Length; i += 2)
                bytes.Add(byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return bytes.ToArray();
    }

    private static async Task<string> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
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
            throw new MuxException($"{Path.GetFileName(exe)} couldn't be started: {ex.Message}", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details.";
            throw new MuxException($"{Path.GetFileNameWithoutExtension(exe)} couldn't read the track: {last}");
        }
        return await stdout.ConfigureAwait(false);
    }
}
