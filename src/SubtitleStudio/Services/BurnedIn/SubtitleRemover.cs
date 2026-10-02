using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

public enum RemovalMethod
{
    /// <summary>Paint the text pixels (letters, outline, shadow) over from the surrounding picture.</summary>
    Fill,

    /// <summary>Fill, then soften the area with a light feathered blur (hides remnants on busy pictures).</summary>
    FillSoften,

    /// <summary>Fill, then an AI model (LaMa) repaints text over detailed backgrounds. Best, slowest.</summary>
    AiFill,
}

/// <param name="LineHeight">Subtitle line height as a fraction of the frame height (from Detect).</param>
/// <param name="LightText">White / yellow subtitles: text is found per frame. Otherwise the whole area is blurred throughout.</param>
/// <param name="Start">Render from here (preview); null = whole video.</param>
/// <param name="Duration">Render this long (preview); null = to the end.</param>
/// <param name="Keep">Text to leave in the picture (read with OCR when the text changes). Only with <paramref name="LightText"/>.</param>
public sealed record RemovalOptions(
    double BandTop,
    double BandBottom,
    double LineHeight,
    bool LightText,
    RemovalMethod Method,
    TimeSpan? Start = null,
    TimeSpan? Duration = null,
    KeepList? Keep = null);

public sealed record RemovalProgress(double Fraction, int Frames, int FramesWithText);

/// <param name="AiPatches">Patches filled by the AI model; <paramref name="AiPatchesSkipped"/>: left to the ordinary fill (smooth background);
/// <paramref name="AiPatchesReused"/>: took an earlier frame's result (the picture behind the text hadn't changed).</param>
/// <param name="KeptFrames">Frames in which text on the keep list was left in the picture; <paramref name="KeepReads"/>: OCR readings made for it.</param>
public sealed record RemovalResult(string OutputPath, int Frames, int FramesWithText, VideoEncoder Encoder, int AiPatches = 0, int AiPatchesSkipped = 0, int AiPatchesReused = 0,
    int KeptFrames = 0, int KeepReads = 0, IReadOnlyList<string>? KeepMatched = null);

/// <summary>
/// Removes burned-in subtitles from the picture and writes a new video:
///   ffmpeg (decode, raw frames) → per-frame processing here → ffmpeg (encode, plus the original's
///   audio, subtitle tracks, chapters and metadata copied unchanged).
/// Per frame, the subtitle area is checked with the same text finder extraction uses; frames without
/// text pass through untouched. The original file is never modified.
/// </summary>
public sealed class SubtitleRemover
{
    private readonly AiInpainter? _ai;
    private readonly Hardware.PerformancePlan _plan;
    private readonly ActivityLog? _log;
    private readonly ITextRecognizer? _ocr;

    /// <param name="ai">Needed for <see cref="RemovalMethod.AiFill"/>; without it AI fill falls back to the ordinary fill.</param>
    /// <param name="plan">How to use this PC (workers, memory for frames); detected hardware in the app.</param>
    /// <param name="ocr">Reads the text for the keep list (<see cref="RemovalOptions.Keep"/>).</param>
    public SubtitleRemover(AiInpainter? ai = null, Hardware.PerformancePlan? plan = null, ActivityLog? log = null, ITextRecognizer? ocr = null)
    {
        _ai = ai;
        _ocr = ocr;
        _plan = plan ?? Hardware.PerformancePlan.Default;
        _log = log;
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>State carried between frames (the previous text mask, to avoid one-frame flicker).</summary>
    public sealed class FrameState
    {
        internal byte[]? PreviousMask;
        internal int PreviousWidth, PreviousHeight;
    }

    /// <summary>
    /// Removes the text from one frame in place (rows [bandY, bandY+bandH)). Returns true when text was
    /// found in this frame (the previous frame's text area is also covered, so a frame can change
    /// without text of its own). <paramref name="linePx"/> is the subtitle line height in pixels of this frame.
    /// </summary>
    public static bool ProcessFrame(FrameSample frame, int bandY, int bandH, double linePx, bool lightText, RemovalMethod method, FrameState state)
    {
        bandY = Math.Clamp(bandY, 0, frame.Height - 1);
        bandH = Math.Clamp(bandH, 1, frame.Height - bandY);
        var cover = FindTextCover(frame, bandY, bandH, linePx, lightText);
        bool hasText = cover is not null;
        byte[]? previous = state.PreviousMask is { } p && state.PreviousWidth == frame.Width && state.PreviousHeight == bandH ? p : null;
        state.PreviousMask = cover;
        state.PreviousWidth = frame.Width;
        state.PreviousHeight = bandH;
        Apply(frame, bandY, bandH, linePx, lightText, method, cover, previous);
        return hasText;
    }

    /// <summary>
    /// Step 1 (independent per frame, so it runs in parallel): the pixels to replace in the area —
    /// letters plus outline, shadow and anti-aliased fringe — or null when the area has no text.
    /// </summary>
    public static byte[]? FindTextCover(FrameSample frame, int bandY, int bandH, double linePx, bool lightText)
    {
        if (!lightText) return null;
        var band = frame.CropRows(bandY, bandH);
        var text = SubtitleImageCleaner.RemoveNonText(SubtitleImageCleaner.LightTextMask(band, linePx), linePx);
        if (text.Count < Math.Max(30, (int)(linePx * linePx * 0.15))) return null;
        return Dilate(text.Bits, band.Width, band.Height, Math.Max(2, (int)Math.Round(linePx * 0.14)));
    }

    /// <summary>
    /// Step 2: replaces this frame's text and the previous frame's (subtitles switch between frames and
    /// the encoder smears a little). Coloured subtitles (not light text) blur the whole area instead.
    /// </summary>
    public static void Apply(FrameSample frame, int bandY, int bandH, double linePx, bool lightText, RemovalMethod method, byte[]? cover, byte[]? previousCover)
    {
        var band = frame.CropRows(bandY, bandH);
        int w = band.Width, h = band.Height;
        if (!lightText)
        {
            BoxBlur(band.Bgra, w, h, 0, 0, w, h, Math.Max(4, (int)(linePx * 0.5)));
            WriteRows(frame, band, bandY);
            return;
        }
        if (cover is null && previousCover is null) return;

        byte[] mask;
        if (cover is not null && previousCover is not null)
        {
            mask = (byte[])cover.Clone();
            for (int i = 0; i < mask.Length; i++) mask[i] |= previousCover[i];
        }
        else mask = cover ?? previousCover!;

        if (!Bounds(mask, w, h, out int x0, out int y0, out int x1, out int y1)) return;
        PullPushFill(band.Bgra, mask, w, h, x0, y0, x1, y1, (int)(linePx * 1.5));
        if (method == RemovalMethod.FillSoften)
            FeatheredBlur(band.Bgra, w, h, x0, y0, x1, y1, Math.Max(2, (int)(linePx * 0.2)), Math.Max(4, (int)(linePx * 0.5)));
        WriteRows(frame, band, bandY);
    }

    internal static byte[]? Union(byte[]? a, byte[]? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        var u = (byte[])a.Clone();
        for (int i = 0; i < u.Length; i++) u[i] |= b[i];
        return u;
    }

    private static void WriteRows(FrameSample frame, FrameSample band, int bandY)
        => Buffer.BlockCopy(band.Bgra, 0, frame.Bgra, bandY * frame.Width * 4, band.Bgra.Length);

    private static bool Bounds(byte[] mask, int w, int h, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = w; y0 = h; x1 = -1; y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0, p = y * w; x < w; x++, p++)
            {
                if (mask[p] == 0) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        x1++; y1++;
        return x1 > 0;
    }

    /// <summary>Square dilation by <paramref name="r"/> (two running-max passes).</summary>
    public static byte[] Dilate(byte[] src, int w, int h, int r)
    {
        var tmp = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, last = -1_000_000;
            for (int x = 0; x < w; x++) { if (src[row + x] == 1) last = x; if (x - last <= r) tmp[row + x] = 1; }
            last = 1_000_000;
            for (int x = w - 1; x >= 0; x--) { if (src[row + x] == 1) last = x; if (last - x <= r) tmp[row + x] = 1; }
        }
        var dst = new byte[src.Length];
        for (int x = 0; x < w; x++)
        {
            int last = -1_000_000;
            for (int y = 0; y < h; y++) { if (tmp[y * w + x] == 1) last = y; if (y - last <= r) dst[y * w + x] = 1; }
            last = 1_000_000;
            for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x] == 1) last = y; if (last - y <= r) dst[y * w + x] = 1; }
        }
        return dst;
    }

    /// <summary>
    /// The opposite of <see cref="Dilate"/>: keeps a pixel only when every pixel within
    /// <paramref name="r"/> (a square) is set. Outside the mask counts as unset.
    /// </summary>
    public static byte[] Erode(byte[] src, int w, int h, int r)
    {
        var tmp = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            // Distance to the nearest unset pixel on the left and on the right (the edges count as unset).
            var left = new int[w];
            int last = -1;
            for (int x = 0; x < w; x++) { if (src[row + x] == 0) last = x; left[x] = x - last; }
            last = w;
            for (int x = w - 1; x >= 0; x--) { if (src[row + x] == 0) last = x; if (left[x] > r && last - x > r) tmp[row + x] = 1; }
        }
        var dst = new byte[src.Length];
        var up = new int[h];
        for (int x = 0; x < w; x++)
        {
            int last = -1;
            for (int y = 0; y < h; y++) { if (tmp[y * w + x] == 0) last = y; up[y] = y - last; }
            last = h;
            for (int y = h - 1; y >= 0; y--) { if (tmp[y * w + x] == 0) last = y; if (up[y] > r && last - y > r) dst[y * w + x] = 1; }
        }
        return dst;
    }

    /// <summary>
    /// Blurs the rectangle around the text and blends it into the picture over <paramref name="feather"/>
    /// pixels outside it, so there is no hard-edged box.
    /// </summary>
    public static void FeatheredBlur(byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, int radius, int feather)
    {
        int bx0 = Math.Max(0, x0 - feather), by0 = Math.Max(0, y0 - feather), bx1 = Math.Min(w, x1 + feather), by1 = Math.Min(h, y1 + feather);
        var blurred = (byte[])bgra.Clone();
        BoxBlur(blurred, w, h, bx0, by0, bx1, by1, radius);
        for (int y = by0; y < by1; y++)
            for (int x = bx0; x < bx1; x++)
            {
                int dx = x < x0 ? x0 - x : x >= x1 ? x - x1 + 1 : 0;
                int dy = y < y0 ? y0 - y : y >= y1 ? y - y1 + 1 : 0;
                float a = 1f - Math.Min(1f, MathF.Sqrt(dx * dx + dy * dy) / feather);
                if (a <= 0) continue;
                int i = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                    bgra[i + c] = (byte)(bgra[i + c] + (blurred[i + c] - bgra[i + c]) * a + 0.5f);
            }
    }

    /// <summary>Three box-blur passes (close to a Gaussian) over a rectangle, edges clamped.</summary>
    public static void BoxBlur(byte[] bgra, int w, int h, int rx0, int ry0, int rx1, int ry1, int radius)
    {
        int rw = rx1 - rx0, rh = ry1 - ry0;
        if (rw <= 0 || rh <= 0) return;
        var buf = new float[rw * rh * 3];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = ((ry0 + y) * w + rx0 + x) * 4, o = (y * rw + x) * 3;
                buf[o] = bgra[i]; buf[o + 1] = bgra[i + 1]; buf[o + 2] = bgra[i + 2];
            }
        var tmp = new float[buf.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BlurLine(buf, tmp, rw, rh, radius, horizontal: true);
            BlurLine(tmp, buf, rw, rh, radius, horizontal: false);
        }
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = ((ry0 + y) * w + rx0 + x) * 4, o = (y * rw + x) * 3;
                bgra[i] = (byte)Math.Clamp(buf[o] + 0.5f, 0, 255);
                bgra[i + 1] = (byte)Math.Clamp(buf[o + 1] + 0.5f, 0, 255);
                bgra[i + 2] = (byte)Math.Clamp(buf[o + 2] + 0.5f, 0, 255);
            }
    }

    private static void BlurLine(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        for (int line = 0; line < lines; line++)
            for (int c = 0; c < 3; c++)
            {
                float sum = 0;
                int Idx(int k) { k = Math.Clamp(k, 0, len - 1); return (horizontal ? line * w + k : k * w + line) * 3 + c; }
                for (int k = -r; k <= r; k++) sum += src[Idx(k)];
                float inv = 1f / (2 * r + 1);
                for (int k = 0; k < len; k++)
                {
                    dst[Idx(k)] = sum * inv;
                    sum += src[Idx(k + r + 1)] - src[Idx(k - r)];
                }
            }
    }

    /// <summary>
    /// Pull-push fill: the masked pixels are rebuilt from the surrounding picture by averaging down a
    /// pyramid of known pixels and interpolating back up. Smooth, seamless and fast; works on a
    /// rectangle around the text with <paramref name="context"/> pixels of picture around it.
    /// </summary>
    public static void PullPushFill(byte[] bgra, byte[] mask, int w, int h, int x0, int y0, int x1, int y1, int context)
    {
        int rx0 = Math.Max(0, x0 - context), ry0 = Math.Max(0, y0 - context);
        int rx1 = Math.Min(w, x1 + context), ry1 = Math.Min(h, y1 + context);
        int rw = rx1 - rx0, rh = ry1 - ry0;
        if (rw <= 0 || rh <= 0) return;

        // Level 0: colour premultiplied by weight (1 = known picture, 0 = text to replace).
        var levels = new List<(float[] C, float[] W, int Width, int Height)>();
        var c0 = new float[rw * rh * 3];
        var w0 = new float[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int p = (ry0 + y) * w + rx0 + x, q = y * rw + x;
                float wt = mask[p] == 1 ? 0 : 1;
                w0[q] = wt;
                c0[q * 3] = bgra[p * 4] * wt;
                c0[q * 3 + 1] = bgra[p * 4 + 1] * wt;
                c0[q * 3 + 2] = bgra[p * 4 + 2] * wt;
            }
        levels.Add((c0, w0, rw, rh));

        // Push: halve until 1x1, keeping weighted sums (weights capped at 1).
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            var (c, wt, lw, lh) = levels[^1];
            int nw = (lw + 1) / 2, nh = (lh + 1) / 2;
            var nc = new float[nw * nh * 3];
            var nwt = new float[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    float sw = 0, s0 = 0, s1 = 0, s2 = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(2 * x + dx, lw - 1), sy = Math.Min(2 * y + dy, lh - 1), s = sy * lw + sx;
                            sw += wt[s]; s0 += c[s * 3]; s1 += c[s * 3 + 1]; s2 += c[s * 3 + 2];
                        }
                    int d = y * nw + x;
                    if (sw > 0)
                    {
                        float k = Math.Min(1f, sw) / sw; // normalise to a weight of at most 1
                        nwt[d] = Math.Min(1f, sw);
                        nc[d * 3] = s0 * k; nc[d * 3 + 1] = s1 * k; nc[d * 3 + 2] = s2 * k;
                    }
                }
            levels.Add((nc, nwt, nw, nh));
        }

        // Pull: from the top, fill each level's missing weight from the (bilinear) level above.
        for (int li = levels.Count - 2; li >= 0; li--)
        {
            var (c, wt, lw, lh) = levels[li];
            var (uc, uw, uwid, uhei) = levels[li + 1];
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    int d = y * lw + x;
                    float missing = 1 - wt[d];
                    if (missing <= 0) continue;
                    float fx = Math.Clamp((x - 0.5f) / 2, 0, uwid - 1), fy = Math.Clamp((y - 0.5f) / 2, 0, uhei - 1);
                    int ax = (int)fx, ay = (int)fy, bx = Math.Min(ax + 1, uwid - 1), by = Math.Min(ay + 1, uhei - 1);
                    float tx = fx - ax, ty = fy - ay;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float v00 = Norm(uc, uw, ay * uwid + ax, ch), v10 = Norm(uc, uw, ay * uwid + bx, ch);
                        float v01 = Norm(uc, uw, by * uwid + ax, ch), v11 = Norm(uc, uw, by * uwid + bx, ch);
                        float v = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                        c[d * 3 + ch] += missing * v;
                    }
                    wt[d] = 1;
                }
        }

        // Write back only the masked pixels.
        var (fc, _, _, _) = levels[0];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int p = (ry0 + y) * w + rx0 + x;
                if (mask[p] == 0) continue;
                int q = (y * rw + x) * 3;
                bgra[p * 4] = (byte)Math.Clamp(fc[q] + 0.5f, 0, 255);
                bgra[p * 4 + 1] = (byte)Math.Clamp(fc[q + 1] + 0.5f, 0, 255);
                bgra[p * 4 + 2] = (byte)Math.Clamp(fc[q + 2] + 0.5f, 0, 255);
            }

        static float Norm(float[] c, float[] w, int i, int ch) => w[i] > 0 ? c[i * 3 + ch] / w[i] : 0;
    }

    // ------------------------------------------------------------------ whole video

    /// <summary>Output name next to the original: "Name (no subs).ext" (MKV when the container can't take H.264 + copied tracks).</summary>
    public static string SuggestedOutputName(string inputPath)
    {
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        var outExt = ext is ".mp4" or ".m4v" or ".mov" or ".mkv" ? ext : ".mkv";
        return Path.GetFileNameWithoutExtension(inputPath) + " (no subs)" + outExt;
    }

    public async Task<RemovalResult> RenderAsync(
        string ffmpeg,
        string inputPath,
        VideoInfo info,
        string outputPath,
        VideoEncoder encoder,
        RemovalOptions options,
        IProgress<RemovalProgress>? progress,
        CancellationToken ct)
    {
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be a new file; the original video is never overwritten.");

        int w = info.Width, h = info.Height;
        double fps = info.FrameRate > 1 ? info.FrameRate : 24;
        string rate = fps.ToString("0.######", CultureInfo.InvariantCulture);
        var (bandY, bandH) = FrameSampler.BandRows(info, options.BandTop, options.BandBottom);
        double linePx = Math.Max(8, options.LineHeight * h);
        var span = options.Duration ?? (info.Duration - (options.Start ?? TimeSpan.Zero));
        double totalFrames = Math.Max(1, span.TotalSeconds * fps);
        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        // Decoder: raw BGRA at a constant frame rate (variable-rate sources are evened out so the
        // frame count matches the copied audio).
        var dec = NewProcess(ffmpeg, redirectInput: false);
        dec.ArgumentList.Add("-nostdin");
        dec.ArgumentList.Add("-v"); dec.ArgumentList.Add("error");
        dec.ArgumentList.Add("-hwaccel"); dec.ArgumentList.Add("auto");
        if (options.Start is { } s0) { dec.ArgumentList.Add("-ss"); dec.ArgumentList.Add(Seconds(s0)); }
        dec.ArgumentList.Add("-i"); dec.ArgumentList.Add(inputPath);
        if (options.Duration is { } d0) { dec.ArgumentList.Add("-t"); dec.ArgumentList.Add(Seconds(d0)); }
        foreach (var a in new[] { "-map", "0:v:0", "-an", "-sn", "-dn", "-fps_mode", "cfr", "-r", rate, "-vf", $"scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            dec.ArgumentList.Add(a);

        // Encoder: frames from us + everything else from the original, copied.
        bool sameContainer = string.Equals(Path.GetExtension(inputPath), Path.GetExtension(outputPath), StringComparison.OrdinalIgnoreCase);
        bool preview = options.Start is not null || options.Duration is not null;
        var enc = NewProcess(ffmpeg, redirectInput: true);
        foreach (var a in new[] { "-y", "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", rate, "-i", "pipe:0" })
            enc.ArgumentList.Add(a);
        if (options.Start is { } s1) { enc.ArgumentList.Add("-ss"); enc.ArgumentList.Add(Seconds(s1)); }
        enc.ArgumentList.Add("-i"); enc.ArgumentList.Add(inputPath);
        foreach (var a in new[] { "-map", "0:v:0", "-map", "1:a?" }) enc.ArgumentList.Add(a);
        if (sameContainer && !preview)
        {
            foreach (var a in new[] { "-map", "1:s?", "-c:s", "copy" }) enc.ArgumentList.Add(a);
            if (Path.GetExtension(outputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                foreach (var a in new[] { "-map", "1:t?" }) enc.ArgumentList.Add(a);
            foreach (var a in new[] { "-map_chapters", "1" }) enc.ArgumentList.Add(a);
        }
        foreach (var a in new[] { "-map_metadata", "1" }) enc.ArgumentList.Add(a);
        foreach (var a in encoder.Arguments) enc.ArgumentList.Add(a);
        // A preview cut starts mid-file: copied audio would begin at the packet before the cut (and run
        // long), so the preview's audio is re-encoded; the full video copies it untouched.
        foreach (var a in preview ? new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" } : new[] { "-pix_fmt", "yuv420p", "-c:a", "copy" })
            enc.ArgumentList.Add(a);
        if (options.Duration is { } d1) { enc.ArgumentList.Add("-t"); enc.ArgumentList.Add(Seconds(d1)); }
        if (Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov")
            foreach (var a in new[] { "-movflags", "+faststart" }) enc.ArgumentList.Add(a);
        enc.ArgumentList.Add(outputPath);

        _log?.Detail("ffmpeg", "decode: " + ActivityLog.CommandLine(ffmpeg, dec.ArgumentList));
        _log?.Detail("ffmpeg", "encode: " + ActivityLog.CommandLine(ffmpeg, enc.ArgumentList));
        using var decoder = Process.Start(dec) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        using var encoderProcess = Process.Start(enc) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var decErr = decoder.StandardError.ReadToEndAsync(CancellationToken.None);
        var encErr = encoderProcess.StandardError.ReadToEndAsync(CancellationToken.None);
        var input = decoder.StandardOutput.BaseStream;
        var output = encoderProcess.StandardInput.BaseStream;

        int frames = 0, withText = 0;
        bool finished = false;

        // Stages run at the same time, handing batches of frames along: the reader fills a batch from the
        // decoder, the main loop finds the text and fills it in (and plans the AI patches), the AI stage
        // repaints the patches on the graphics card, the writer sends the batch to the encoder. The card
        // doesn't wait for decoding, text search or encoding.
        long frameBytes = (long)w * h * 4;
        bool aiRun = options.Method == RemovalMethod.AiFill && _ai is not null;
        int batchSize = _plan.FramesPerBatch(frameBytes, aiRun);
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _plan.Workers };
        _log?.Detail("Remove", $"{w}x{h} frames, {batchSize} per batch, {Hardware.PerformancePlan.BatchesInFlight} batches in flight ({_plan.Summary}).");

        var free = Channel.CreateUnbounded<FrameSample[]>();
        for (int b = 0; b < Hardware.PerformancePlan.BatchesInFlight; b++)
        {
            var set = new FrameSample[batchSize];
            for (int i = 0; i < batchSize; i++) set[i] = new FrameSample(w, h, new byte[frameBytes], TimeSpan.Zero);
            free.Writer.TryWrite(set);
        }
        var decoded = Channel.CreateBounded<(FrameSample[] Set, int Count)>(Hardware.PerformancePlan.BatchesInFlight);
        var cleaned = Channel.CreateBounded<(FrameSample[] Set, int Count, int Text)>(Hardware.PerformancePlan.BatchesInFlight);
        bool aiStage = options.Method == RemovalMethod.AiFill && _ai is not null && options.LightText;
        var toAi = Channel.CreateBounded<(FrameSample[] Set, int Count, int Text, List<AiInpainter.Patch> Patches)>(Hardware.PerformancePlan.BatchesInFlight);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var covers = new byte[]?[batchSize];
        KeepFilter? keep = null;
        if (options.Keep is { IsEmpty: false } keepList)
        {
            if (!options.LightText)
                _log?.Warning("Remove", "The keep list only works when the text is found per frame (white or yellow subtitles); with coloured text the whole area is blurred.");
            else if (_ocr is not { IsAvailable: true } ocr)
                _log?.Warning("Remove", "The keep list needs Windows OCR to read the text, which isn't available: everything in the area is removed.");
            else
            {
                keep = new KeepFilter(keepList, ocr, w, bandY, bandH, linePx, fps, options.Start ?? TimeSpan.Zero);
                _log?.Info("Remove", $"Keep list: {keepList.Count} entr{(keepList.Count == 1 ? "y" : "ies")} ({string.Join(", ", keepList.Entries.Select(e => "\"" + e + "\""))}), read with OCR in {ocr.LanguageTag}.");
                var log = _log;
                keep.Trace = (time, lines) =>
                {
                    if (lines.Any(l => l.Kept))
                        log?.Detail("Remove", $"{time:hh\\:mm\\:ss\\.ff} kept: {string.Join(" / ", lines.Select(l => (l.Kept ? "[kept] " : string.Empty) + l.Text))}");
                };
            }
        }
        byte[]? carried = null;
        long waitDecoder = 0, waitEncoder = 0, waitAi = 0, cleanUp = 0, aiTotal = 0;
        var clock = Stopwatch.StartNew();

        async Task ReadLoop()
        {
            while (true)
            {
                var set = await free.Reader.ReadAsync(token).ConfigureAwait(false);
                int count = 0;
                while (count < batchSize)
                {
                    int got = await ReadFullAsync(input, set[count].Bgra, token).ConfigureAwait(false);
                    if (got < frameBytes) break;
                    count++;
                }
                if (count > 0) await decoded.Writer.WriteAsync((set, count), token).ConfigureAwait(false);
                if (count < batchSize) break;
            }
        }

        async Task WriteLoop()
        {
            await foreach (var (set, count, text) in cleaned.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        await output.WriteAsync(set[i].Bgra, token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        throw new InvalidOperationException($"The video encoder stopped: {FirstLine(await encErr.ConfigureAwait(false))}");
                    }
                }
                frames += count;
                withText += text;
                progress?.Report(new RemovalProgress(Math.Min(1, frames / totalFrames), frames, withText));
                free.Writer.TryWrite(set);
            }
        }

        // The AI stage: repaints each batch's patches in video order (results are reused across batches).
        async Task AiLoop()
        {
            await foreach (var (set, count, text, patches) in toAi.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                long t = Stopwatch.GetTimestamp();
                if (patches.Count > 0) await _ai!.FillAsync(set.Take(count).ToList(), patches, token).ConfigureAwait(false);
                aiTotal += Stopwatch.GetTimestamp() - t;
                t = Stopwatch.GetTimestamp();
                await cleaned.Writer.WriteAsync((set, count, text), token).ConfigureAwait(false);
                waitEncoder += Stopwatch.GetTimestamp() - t;
            }
        }

        // A failing stage stops the others; its own error is the one reported.
        Task Stage(Func<Task> loop, Action<Exception?>? complete = null) => Task.Run(async () =>
        {
            try
            {
                await loop().ConfigureAwait(false);
                complete?.Invoke(null);
            }
            catch (Exception ex)
            {
                complete?.Invoke(ex);
                stop.Cancel();
                throw;
            }
        });

        var reader = Stage(ReadLoop, ex => decoded.Writer.TryComplete(ex));
        var writer = Stage(WriteLoop);
        var repainter = aiStage ? Stage(AiLoop, ex => cleaned.Writer.TryComplete(ex)) : Task.CompletedTask;
        try
        {
            try
            {
                while (true)
                {
                    long t = Stopwatch.GetTimestamp();
                    if (!await decoded.Reader.WaitToReadAsync(token).ConfigureAwait(false)) break;
                    var (batch, count) = await decoded.Reader.ReadAsync(token).ConfigureAwait(false);
                    waitDecoder += Stopwatch.GetTimestamp() - t;

                    t = Stopwatch.GetTimestamp();
                    Parallel.For(0, count, parallel, i =>
                        covers[i] = FindTextCover(batch[i], bandY, bandH, linePx, options.LightText));
                    // Text on the keep list stays: taken out of what is replaced (read before anything is painted).
                    if (keep is not null) await keep.ApplyAsync(batch, covers, count, token).ConfigureAwait(false);
                    var previous = carried;
                    Parallel.For(0, count, parallel, i =>
                        Apply(batch[i], bandY, bandH, linePx, options.LightText, options.Method, covers[i], i == 0 ? previous : covers[i - 1]));
                    cleanUp += Stopwatch.GetTimestamp() - t;

                    // Patches for the AI stage (the detailed parts get repainted on top of the ordinary fill).
                    var patches = new List<AiInpainter.Patch>();
                    if (aiStage)
                        for (int i = 0; i < count; i++)
                        {
                            var union = Union(covers[i], i == 0 ? previous : covers[i - 1]);
                            if (union is not null) patches.AddRange(AiInpainter.PlanPatches(i, w, h, bandY, bandH, union, AiInpainter.CloseGaps(union, w, bandH, linePx)));
                        }
                    carried = covers[count - 1];

                    int text = 0;
                    for (int i = 0; i < count; i++)
                        if (covers[i] is not null || !options.LightText) text++;
                    t = Stopwatch.GetTimestamp();
                    if (aiStage)
                    {
                        await toAi.Writer.WriteAsync((batch, count, text, patches), token).ConfigureAwait(false);
                        waitAi += Stopwatch.GetTimestamp() - t;
                    }
                    else
                    {
                        await cleaned.Writer.WriteAsync((batch, count, text), token).ConfigureAwait(false);
                        waitEncoder += Stopwatch.GetTimestamp() - t;
                    }
                }
                if (aiStage) toAi.Writer.TryComplete();
                else cleaned.Writer.TryComplete();
                await Task.WhenAll(reader, repainter, writer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Stopped because another stage failed: report that failure instead.
                foreach (var stage in new[] { repainter, writer, reader })
                    if (stage.IsFaulted && stage.Exception?.InnerException is { } inner and not OperationCanceledException)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
                throw;
            }
            catch
            {
                stop.Cancel();
                throw;
            }

            output.Close();
            await decoder.WaitForExitAsync(ct).ConfigureAwait(false);
            await encoderProcess.WaitForExitAsync(ct).ConfigureAwait(false);
            if (frames == 0)
                throw new InvalidOperationException($"ffmpeg could not decode the video: {FirstLine(await decErr.ConfigureAwait(false))}");
            if (encoderProcess.ExitCode != 0)
                throw new InvalidOperationException($"The video encoder failed: {FirstLine(await encErr.ConfigureAwait(false))}");
            finished = true;
            LogTiming(clock.Elapsed, frames, waitDecoder, cleanUp, waitAi, aiTotal, waitEncoder);
        }
        finally
        {
            stop.Cancel();
            foreach (var p in new[] { decoder, encoderProcess })
                if (!p.HasExited)
                    try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // The stages end once the pipes close; their errors were reported above.
            try { Task.WaitAll(new[] { reader, repainter, writer }, 5000); } catch (AggregateException) { }
            if (!finished)
            {
                var encText = encErr.IsCompleted ? encErr.Result.Trim() : string.Empty;
                var decText = decErr.IsCompleted ? decErr.Result.Trim() : string.Empty;
                if (encText.Length > 0) _log?.Detail("ffmpeg", "encoder said: " + encText);
                if (decText.Length > 0) _log?.Detail("ffmpeg", "decoder said: " + decText);
                try { encoderProcess.WaitForExit(5000); } catch (InvalidOperationException) { }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        progress?.Report(new RemovalProgress(1, frames, withText));
        return new RemovalResult(outputPath, frames, withText, encoder, _ai?.PatchesRun ?? 0, _ai?.PatchesSkipped ?? 0, _ai?.PatchesReused ?? 0,
            keep?.KeptFrames ?? 0, keep?.Reads ?? 0, keep?.Matched.ToList());
    }

    /// <summary>Where the time went, so a slow run shows which stage held it up.</summary>
    private void LogTiming(TimeSpan total, int frames, long waitDecoder, long cleanUp, long waitAi, long aiTotal, long waitEncoder)
    {
        if (_log is null) return;
        static double Sec(long ticks) => ticks / (double)Stopwatch.Frequency;
        double secs = Math.Max(0.001, total.TotalSeconds);
        var line = $"Timing: {frames} frames in {secs:0.0} s ({frames / secs:0.0} frames/s). Text stage: waiting for decoded frames {Sec(waitDecoder):0.0} s, text search and fill {Sec(cleanUp):0.0} s";
        if (_ai is { Calls: 0 } && aiTotal > 0)
            line += $", waiting for the AI stage {Sec(waitAi):0.0} s. AI stage: {Sec(aiTotal):0.0} s (no model calls: {_ai.PatchesSkipped} smooth patches used Fill in, {_ai.PatchesReused} reused from earlier frames)";
        else if (_ai is { Calls: > 0 } ai)
        {
            double model = ai.ModelTime.TotalSeconds;
            var sizes = string.Join(", ", ai.PatchesBySize.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} at {kv.Key} px"));
            line += $", waiting for the AI stage {Sec(waitAi):0.0} s. AI stage: busy {Sec(aiTotal):0.0} s (graphics card {model:0.0} s in {ai.Calls} calls of up to {ai.Model.BatchSize} patches: {model * 1000 / ai.Calls:0} ms per call, {model * 1000 / Math.Max(1, ai.PatchesRun):0} ms per patch; {ai.PatchesRun} repainted ({sizes}), {ai.PatchesReused} reused from earlier frames, {ai.PatchesClosed} on smooth backgrounds with the gaps between letters closed)";
        }
        line += $", waiting for the encoder {Sec(waitEncoder):0.0} s.";
        _log.Info("Remove", line);
    }

    private static ProcessStartInfo NewProcess(string exe, bool redirectInput) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = redirectInput,
        RedirectStandardOutput = !redirectInput,
        RedirectStandardError = true,
    };

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
}
