using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>A black-and-white map of the pixels that look like subtitle text (1 = text).</summary>
public sealed class TextMask
{
    public TextMask(int width, int height, byte[] bits)
    {
        if (bits.Length != width * height) throw new ArgumentException("Mask size does not match.");
        Width = width;
        Height = height;
        Bits = bits;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bits { get; }

    public int Count
    {
        get
        {
            int c = 0;
            foreach (var b in Bits) c += b;
            return c;
        }
    }

    /// <summary>Share of pixels (0..1) that differ from another mask of the same size.</summary>
    public double DifferenceTo(TextMask other)
    {
        if (other.Width != Width || other.Height != Height) return 1;
        int diff = 0;
        for (int i = 0; i < Bits.Length; i++) diff += Bits[i] ^ other.Bits[i];
        return (double)diff / Bits.Length;
    }
}

/// <summary>
/// Adds up the text masks of consecutive frames that show the same subtitle. The text stays put while
/// the picture behind it moves, so pixels that are "text" in most frames are the subtitle and the rest
/// (bright bits of the background passing through) fall away.
/// </summary>
public sealed class MaskAccumulator
{
    private ushort[]? _counts;
    private int _width, _height;

    public int Frames { get; private set; }

    public void Reset()
    {
        _counts = null;
        Frames = 0;
    }

    public void Add(TextMask mask)
    {
        if (_counts is null || mask.Width != _width || mask.Height != _height)
        {
            _counts = new ushort[mask.Bits.Length];
            _width = mask.Width;
            _height = mask.Height;
            Frames = 0;
        }
        if (Frames == ushort.MaxValue) return;
        for (int i = 0; i < _counts.Length; i++) _counts[i] += mask.Bits[i];
        Frames++;
    }

    /// <summary>Pixels that were text in at least <paramref name="share"/> of the frames.</summary>
    public TextMask? Consensus(double share = 0.6)
    {
        if (_counts is null || Frames == 0) return null;
        int need = Math.Max(1, (int)Math.Ceiling(Frames * share));
        var bits = new byte[_counts.Length];
        for (int i = 0; i < bits.Length; i++) bits[i] = _counts[i] >= need ? (byte)1 : (byte)0;
        return new TextMask(_width, _height, bits);
    }
}

/// <summary>
/// Turns a strip of video with white or yellow subtitles into clean black text on white for OCR.
/// A pixel counts as text when it is light (white / light grey / yellow), part of a letter-sized stroke
/// with a dark edge all round (the outline or shadow every readable subtitle has), and of the subtitle's
/// fill colour. Leftover shapes that are too tall, too big and
/// solid, too tiny, or cut by the top / bottom edge of the strip are removed. OCR engines read this far
/// better than text over a moving picture.
/// </summary>
public static class SubtitleImageCleaner
{
    public static int Luma(byte b, byte g, byte r) => (b * 29 + g * 150 + r * 77) >> 8;

    public static bool IsLight(byte b, byte g, byte r)
    {
        int l = Luma(b, g, r);
        if (l < 165) return false;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min <= 60) return true;                                // white / light grey
        return r >= 150 && g >= 130 && b + 40 <= Math.Min(r, g);          // yellow
    }

    /// <summary>Share of light pixels inside a line box (used by Detect to decide if the clean-up fits).</summary>
    public static double LightRatio(FrameSample frame, OcrLine line)
    {
        int x0 = Math.Clamp((int)line.X, 0, frame.Width - 1), x1 = Math.Clamp((int)Math.Ceiling(line.X + line.Width), x0 + 1, frame.Width);
        int y0 = Math.Clamp((int)line.Y, 0, frame.Height - 1), y1 = Math.Clamp((int)Math.Ceiling(line.Bottom), y0 + 1, frame.Height);
        int light = 0, total = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * frame.Width + x) * 4;
                if (IsLight(frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2])) light++;
                total++;
            }
        return total == 0 ? 0 : (double)light / total;
    }

    /// <summary>
    /// Light pixels that form part of a letter:
    ///  - stroke test: the run of light pixels one way (across or down) is thin, at most about 60% of a
    ///    line, and the run the other way stays within about a letter's height; both runs end at a dark
    ///    edge (the outline or shadow). The gap between two letters over a bright background fails
    ///    because it opens into the background above and below, and a bright patch next to a letter fails
    ///    because its run doesn't end in dark on the far side;
    ///  - colour test: the pixel has the colour most of the strokes have (the subtitle's fill colour),
    ///    which drops bright background of another shade that slipped through;
    ///  - a second pass adds strokes of exactly the fill colour bounded by any other colour, for text
    ///    without a dark edge over a light but tinted background.
    /// </summary>
    public static TextMask LightTextMask(FrameSample band, double lineHeightPx)
    {
        int w = band.Width, h = band.Height, n = w * h;
        var px = band.Bgra;
        int thin = Math.Max(3, (int)Math.Round(lineHeightPx * 0.6)); // strokes join: two strokes wide
        int tall = Math.Max(thin + 1, (int)Math.Round(lineHeightPx * 1.3));
        int edgeRadius = Math.Max(1, (int)Math.Round(lineHeightPx * 0.04));

        var luma = new byte[n];
        var light = new byte[n];
        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            luma[p] = (byte)Luma(px[i], px[i + 1], px[i + 2]);
            if (IsLight(px[i], px[i + 1], px[i + 2])) light[p] = 1;
        }

        // Strokes with a truly dark edge (outline, shadow); their most common colour is the fill.
        var sure = Strokes(130, out int strokeWidth);
        if (FillColour(px, sure) is not { } fill) return new TextMask(w, h, sure);

        // A second fill colour: subtitles (and ads burned in with them) can mix white and yellow lines, e.g.
        // a white line with a yellow web address under it. Kept only when it is clearly a subtitle colour
        // (white, or a strong yellow: not beige or skin), distinct from the first, and a real share of the
        // strokes; otherwise the one-colour rule below drops it as background, as before.
        const int tolerance = 24;
        bool NearFill((int R, int G, int B) f, int i, int tol)
            => Math.Abs(px[i + 2] - f.R) <= tol && Math.Abs(px[i + 1] - f.G) <= tol && Math.Abs(px[i] - f.B) <= tol;
        var fills = new List<(int R, int G, int B)> { fill };
        {
            var rest = new byte[n];
            int restCount = 0, sureCount = 0;
            for (int p = 0, i = 0; p < n; p++, i += 4)
            {
                if (sure[p] == 0) continue;
                sureCount++;
                if (!NearFill(fill, i, tolerance)) { rest[p] = 1; restCount++; }
            }
            if (restCount >= Math.Max(20, sureCount * 15 / 100) && FillColour(px, rest) is { } second
                && IsSubtitleColour(second) && ColourDistance(second, fill) > 3 * tolerance)
                fills.Add(second);
        }

        // Keep only stroke pixels of a fill colour: drops bright background of another shade that slipped through.
        for (int p = 0, i = 0; p < n; p++, i += 4)
        {
            if (sure[p] == 0) continue;
            if (!fills.Any(f => NearFill(f, i, tolerance))) sure[p] = 0;
        }

        // Second pass, by colour: pixels of exactly a fill colour (tight tolerance) in letter-sized strokes
        // bounded by anything of another colour. This keeps letters that have no dark edge: thin text
        // with a faint shadow over a light background that is tinted (a mint collar, skin, a teal wall),
        // which the dark-edge pass drops.
        // Slivers much thinner than a letter stroke are colour fringes along edges (video compression).
        int minAcross = Math.Max(2, (int)Math.Round(strokeWidth * 0.45));
        foreach (var f in fills)
        {
            var inFill = new byte[n];
            var other = new byte[n];
            for (int p = 0, i = 0; p < n; p++, i += 4)
            {
                if (NearFill(f, i, FillTolerance)) inFill[p] = 1; else other[p] = 1;
            }
            var byColour = StrokeMask(inFill, other, w, h, thin, tall, out _, minAcross);
            for (int p = 0; p < n; p++) sure[p] |= byColour[p];
        }
        return new TextMask(w, h, sure);

        byte[] Strokes(int darkAtMost, out int median)
        {
            var on = new byte[n];
            var dark = new byte[n];
            for (int p = 0; p < n; p++)
            {
                if (luma[p] <= darkAtMost) dark[p] = 1;
                else if (light[p] == 1) on[p] = 1;
            }
            return StrokeMask(on, Dilate(dark, w, h, edgeRadius), w, h, thin, tall, out median);
        }
    }

    /// <summary>Clearly a subtitle colour: near-white, or a strong yellow (not beige, skin or a pale wall).</summary>
    public static bool IsSubtitleColour((int R, int G, int B) c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (min >= 190 && max - min <= 40) return true;                     // white / light grey
        return c.R >= 200 && c.G >= 160 && c.B + 90 <= Math.Min(c.R, c.G);  // yellow
    }

    private static int ColourDistance((int R, int G, int B) a, (int R, int G, int B) b)
        => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>Per-channel tolerance around the measured fill colour for the colour pass.</summary>
    public const int FillTolerance = 16;

    /// <summary>
    /// Pixels of <paramref name="on"/> whose run across and run down both end in <paramref name="edge"/>
    /// pixels, with both runs at most <paramref name="tall"/> long and one of them at most <paramref name="thin"/>.
    /// </summary>
    private static byte[] StrokeMask(byte[] on, byte[] edge, int w, int h, int thin, int tall, out int medianStroke, int minAcross = 1)
    {
        int n = w * h;
        var hRun = new ushort[n];
        var vRun = new ushort[n];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, x = 0;
            while (x < w)
            {
                if (on[row + x] == 0) { x++; continue; }
                int start = x;
                while (x < w && on[row + x] == 1) x++;
                if (start > 0 && x < w && edge[row + start - 1] == 1 && edge[row + x] == 1)
                    for (int k = start; k < x; k++) hRun[row + k] = (ushort)Math.Min(x - start, ushort.MaxValue);
            }
        }
        for (int x = 0; x < w; x++)
        {
            int y = 0;
            while (y < h)
            {
                if (on[y * w + x] == 0) { y++; continue; }
                int start = y;
                while (y < h && on[y * w + x] == 1) y++;
                if (start > 0 && y < h && edge[(start - 1) * w + x] == 1 && edge[y * w + x] == 1)
                    for (int k = start; k < y; k++) vRun[k * w + x] = (ushort)(y - start);
            }
        }
        var mask = new byte[n];
        var widths = new int[thin + 1];
        int kept = 0;
        for (int p = 0; p < n; p++)
        {
            int hr = hRun[p], vr = vRun[p];
            if (hr == 0 || vr == 0) continue;
            int across = Math.Min(hr, vr);
            if (hr <= tall && vr <= tall && across <= thin && across >= minAcross)
            {
                mask[p] = 1;
                widths[across]++;
                kept++;
            }
        }

        // Median stroke width (the narrower run through each kept pixel).
        medianStroke = 0;
        if (kept >= 20)
            for (int k = 0, acc = 0; k < widths.Length; k++)
            {
                acc += widths[k];
                if (acc * 2 >= kept) { medianStroke = k; break; }
            }
        return mask;
    }

    /// <summary>Mean colour of the most common colour cell among the mask pixels, or null if there are too few.</summary>
    private static (int R, int G, int B)? FillColour(byte[] px, byte[] mask)
    {
        var bins = new int[4096];
        int total = 0;
        for (int p = 0; p < mask.Length; p++)
        {
            if (mask[p] == 0) continue;
            int i = p * 4;
            bins[(px[i + 2] >> 4 << 8) | (px[i + 1] >> 4 << 4) | (px[i] >> 4)]++;
            total++;
        }
        if (total < 20) return null;

        int best = 0;
        for (int b = 1; b < bins.Length; b++) if (bins[b] > bins[best]) best = b;
        int br = best >> 8, bg = (best >> 4) & 15, bb = best & 15;
        long sr = 0, sg = 0, sb = 0, count = 0;
        for (int p = 0; p < mask.Length; p++)
        {
            if (mask[p] == 0) continue;
            int i = p * 4;
            if (Math.Abs((px[i + 2] >> 4) - br) <= 1 && Math.Abs((px[i + 1] >> 4) - bg) <= 1 && Math.Abs((px[i] >> 4) - bb) <= 1)
            {
                sr += px[i + 2];
                sg += px[i + 1];
                sb += px[i];
                count++;
            }
        }
        return count == 0 ? null : ((int)(sr / count), (int)(sg / count), (int)(sb / count));
    }

    /// <summary>Removes shapes that can't be letters. See the class summary.</summary>
    public static TextMask RemoveNonText(TextMask mask, double lineHeightPx)
    {
        int w = mask.Width, h = mask.Height;
        var bits = (byte[])mask.Bits.Clone();
        var labels = new int[bits.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        double lh = Math.Max(6, lineHeightPx);
        int minCount = Math.Max(3, (int)(lh * lh * 0.003));
        int label = 0;

        for (int start = 0; start < bits.Length; start++)
        {
            if (bits[start] == 0 || labels[start] != 0) continue;
            label++;
            members.Clear();
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                members.Add(p);
                int x = p % w, y = p / w;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        int q = ny * w + nx;
                        if (bits[q] == 1 && labels[q] == 0)
                        {
                            labels[q] = label;
                            stack.Push(q);
                        }
                    }
                }
            }

            int cw = maxX - minX + 1, ch = maxY - minY + 1, count = members.Count;
            double fill = (double)count / (cw * ch);
            bool remove = ch > lh * 1.6                                   // taller than a line of text
                       || minY == 0 || maxY == h - 1                      // cut by the strip's edge: something else entering
                       || count < minCount                                // specks
                       || (count > lh * lh * 0.6 && fill > 0.65)          // big solid blob (a lamp, the sky)
                       || cw > w * 0.6;                                   // spans the whole strip
            if (remove)
                foreach (var p in members) bits[p] = 0;
        }
        return new TextMask(w, h, bits);
    }

    /// <summary>Black text on white, the way OCR engines like it best.</summary>
    public static FrameSample Render(TextMask mask, TimeSpan time)
    {
        var bgra = new byte[mask.Bits.Length * 4];
        for (int p = 0, i = 0; p < mask.Bits.Length; p++, i += 4)
        {
            byte v = mask.Bits[p] == 1 ? (byte)0 : (byte)255;
            bgra[i] = v;
            bgra[i + 1] = v;
            bgra[i + 2] = v;
            bgra[i + 3] = 255;
        }
        return new FrameSample(mask.Width, mask.Height, bgra, time);
    }

    /// <summary>Mask → shape filter → OCR-ready image, in one go.</summary>
    public static FrameSample Clean(FrameSample band, double lineHeightPx)
        => Render(RemoveNonText(LightTextMask(band, lineHeightPx), lineHeightPx), band.Time);

    /// <summary>Binary dilation with a (2r+1) square, as two running-sum passes.</summary>
    private static byte[] Dilate(byte[] src, int w, int h, int r)
    {
        var horizontal = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, sum = 0;
            for (int x = 0; x < Math.Min(r, w); x++) sum += src[row + x];
            for (int x = 0; x < w; x++)
            {
                if (x + r < w) sum += src[row + x + r];
                if (x - r - 1 >= 0) sum -= src[row + x - r - 1];
                horizontal[row + x] = sum > 0 ? (byte)1 : (byte)0;
            }
        }
        var result = new byte[src.Length];
        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = 0; y < Math.Min(r, h); y++) sum += horizontal[y * w + x];
            for (int y = 0; y < h; y++)
            {
                if (y + r < h) sum += horizontal[(y + r) * w + x];
                if (y - r - 1 >= 0) sum -= horizontal[(y - r - 1) * w + x];
                result[y * w + x] = sum > 0 ? (byte)1 : (byte)0;
            }
        }
        return result;
    }
}
