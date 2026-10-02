using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// During removal: leaves the text on the keep list in the picture. Whenever the text in the subtitle
/// area changes, that frame's text is read (OCR) and the lines matching the list are taken out of the
/// area to replace; while the text stays the same, that answer is reused, so only a reading or two per
/// subtitle is needed. Everything else in the area is removed as usual.
/// </summary>
public sealed class KeepFilter
{
    /// <summary>
    /// The text counts as changed when this share of its pixels differs from the last reading. The same
    /// subtitle over a moving picture differs by about 0.1%; a new subtitle next to unchanged text, 13%.
    /// Too low only costs an extra reading.
    /// </summary>
    public const double ChangeThreshold = 0.04;

    private readonly KeepList _list;
    private readonly ITextRecognizer _ocr;
    private readonly int _width, _bandY, _bandH;
    private readonly double _linePx;
    private readonly int _pad;
    private byte[]? _lastRead;
    private List<(int X0, int Y0, int X1, int Y1)> _keep = new();
    private readonly HashSet<string> _matched = new(StringComparer.Ordinal);

    private readonly double _fps;
    private readonly TimeSpan _start;
    private long _frame;

    /// <param name="fps">Frame rate and <paramref name="start"/> (where rendering began): for the times in <see cref="Trace"/>.</param>
    public KeepFilter(KeepList list, ITextRecognizer ocr, int width, int bandY, int bandH, double linePx, double fps = 24, TimeSpan start = default)
    {
        _fps = fps > 0 ? fps : 24;
        _start = start;
        _list = list;
        _ocr = ocr;
        _width = width;
        _bandY = bandY;
        _bandH = bandH;
        _linePx = linePx;
        // The cover reaches past the letters (outline, shadow, dilation): keep a margin around the line too.
        _pad = (int)Math.Ceiling(linePx * 0.35) + 2;
    }

    /// <summary>Text readings made.</summary>
    public int Reads { get; private set; }

    /// <summary>Frames in which something was left in the picture.</summary>
    public int KeptFrames { get; private set; }

    /// <summary>Keep-list entries that were found.</summary>
    public IReadOnlyCollection<string> Matched => _matched;

    /// <summary>What was read most recently, for the Log (lines, and whether each was kept).</summary>
    public Action<TimeSpan, IReadOnlyList<(string Text, bool Kept)>>? Trace { get; set; }

    /// <summary>
    /// Takes the kept lines out of the covers of the frames, in video order (<paramref name="covers"/> are
    /// changed in place; a cover with nothing left to remove becomes null). Call before the fill.
    /// </summary>
    public async Task ApplyAsync(FrameSample[] frames, byte[]?[] covers, int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++, _frame++)
        {
            var cover = covers[i];
            if (cover is null)
            {
                _lastRead = null;
                _keep = new();
                continue;
            }
            if (_lastRead is null || Difference(cover, _lastRead) > ChangeThreshold)
            {
                _lastRead = (byte[])cover.Clone();
                _keep = await ReadAsync(frames[i], _start + TimeSpan.FromSeconds(_frame / _fps), ct).ConfigureAwait(false);
            }
            if (_keep.Count == 0) continue;
            covers[i] = Without(cover, _keep);
            KeptFrames++;
        }
    }

    private async Task<List<(int, int, int, int)>> ReadAsync(FrameSample frame, TimeSpan time, CancellationToken ct)
    {
        Reads++;
        var band = frame.CropRows(_bandY, _bandH);
        var keep = new List<(int, int, int, int)>();
        var trace = new List<(string, bool)>();

        // OCR reads lines about 30-60 px tall best, and refuses images over its size limit.
        int max = Math.Max(256, _ocr.MaxImageDimension);
        double scale = Math.Clamp(BurnedInService.TargetLinePixels / Math.Max(8, _linePx), 1, 3);
        if (band.Width * scale > max) scale = max / (double)band.Width;

        // Two readings: the picture itself (thin or serif lettering, a caption on a plain background) and the
        // cleaned-up text (white subtitles over a busy picture). Thin strokes can break apart in the
        // clean-up; busy pictures can hide text from the plain reading. A line found by either one counts.
        var readings = new List<(IReadOnlyList<OcrLine> Lines, string Kind)>();
        readings.Add((await _ocr.RecognizeAsync(Resize(band, scale), ct).ConfigureAwait(false), "picture"));
        var mask = SubtitleImageCleaner.RemoveNonText(SubtitleImageCleaner.LightTextMask(band, _linePx), _linePx);
        if (mask.Count > 0)
            readings.Add((await _ocr.RecognizeAsync(SubtitleImageCleaner.Render(Scale(mask, scale), time), ct).ConfigureAwait(false), "clean"));

        foreach (var (lines, _) in readings)
            foreach (var line in lines)
            {
                var entry = _list.Match(line.Text);
                trace.Add((line.Text, entry is not null));
                if (entry is null) continue;
                _matched.Add(entry);
                int x0 = (int)Math.Floor(line.X / scale) - _pad, y0 = (int)Math.Floor(line.Y / scale) - _pad;
                int x1 = (int)Math.Ceiling((line.X + line.Width) / scale) + _pad, y1 = (int)Math.Ceiling(line.Bottom / scale) + _pad;
                keep.Add((Math.Max(0, x0), Math.Max(0, y0), Math.Min(_width, x1), Math.Min(_bandH, y1)));
            }
        Trace?.Invoke(time, trace);
        return keep;
    }

    /// <summary>A picture resized (bilinear) for reading.</summary>
    public static FrameSample Resize(FrameSample image, double scale)
    {
        if (Math.Abs(scale - 1) < 0.01) return image;
        int w = Math.Max(1, (int)Math.Round(image.Width * scale)), h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var src = image.Bgra;
        var dst = new byte[w * h * 4];
        int sw = image.Width, sh = image.Height;
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Clamp((y + 0.5) / scale - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1);
            double ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x + 0.5) / scale - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1);
                double tx = fx - x0;
                int a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, d = (y1 * sw + x1) * 4, o = (y * w + x) * 4;
                for (int k = 0; k < 3; k++)
                {
                    double top = src[a + k] + (src[b + k] - src[a + k]) * tx;
                    double bottom = src[c + k] + (src[d + k] - src[c + k]) * tx;
                    dst[o + k] = (byte)Math.Round(top + (bottom - top) * ty);
                }
                dst[o + 3] = 255;
            }
        }
        return new FrameSample(w, h, dst, image.Time);
    }

    /// <summary>The cover without the kept boxes; null when too little is left to be text.</summary>
    private byte[]? Without(byte[] cover, List<(int X0, int Y0, int X1, int Y1)> keep)
    {
        foreach (var (x0, y0, x1, y1) in keep)
            for (int y = y0; y < y1; y++)
                Array.Clear(cover, y * _width + x0, Math.Max(0, x1 - x0));
        int left = 0;
        foreach (var b in cover) left += b;
        return left < Math.Max(30, (int)(_linePx * _linePx * 0.15)) ? null : cover;
    }

    /// <summary>Share of text pixels that differ (sampled: every 3rd pixel is enough to tell).</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 1;
        int differ = 0, either = 0;
        for (int p = 0; p < a.Length; p += 3)
        {
            int x = a[p], y = b[p];
            if ((x | y) == 0) continue;
            either++;
            if (x != y) differ++;
        }
        return either == 0 ? 0 : differ / (double)either;
    }

    /// <summary>Resizes a text mask: enlarging repeats pixels, reducing keeps a pixel if any pixel it covers is text.</summary>
    public static TextMask Scale(TextMask mask, double scale)
    {
        if (Math.Abs(scale - 1) < 0.01) return mask;
        int w = Math.Max(1, (int)Math.Round(mask.Width * scale)), h = Math.Max(1, (int)Math.Round(mask.Height * scale));
        var bits = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int sy0 = (int)(y / scale), sy1 = Math.Max(sy0 + 1, Math.Min(mask.Height, (int)((y + 1) / scale)));
            for (int x = 0; x < w; x++)
            {
                int sx0 = (int)(x / scale), sx1 = Math.Max(sx0 + 1, Math.Min(mask.Width, (int)((x + 1) / scale)));
                byte v = 0;
                for (int sy = sy0; sy < sy1 && v == 0 && sy < mask.Height; sy++)
                    for (int sx = sx0; sx < sx1 && sx < mask.Width; sx++)
                        if (mask.Bits[sy * mask.Width + sx] != 0) { v = 1; break; }
                bits[y * w + x] = v;
            }
        }
        return new TextMask(w, h, bits);
    }
}
