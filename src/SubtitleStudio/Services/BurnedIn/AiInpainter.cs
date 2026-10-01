using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// An image inpainting model with a square input (LaMa: 512 x 512, or 256 / 384 / 512 when flexible).
/// Images are RGB planes scaled 0..1 ([n,3,S,S]); masks are 1 where the picture must be invented ([n,1,S,S]).
/// The result is RGB planes 0..255 ([n,3,S,S]).
/// </summary>
public interface IInpaintModel : IDisposable
{
    /// <summary>The input size, or the largest one when <see cref="FlexibleSize"/>.</summary>
    int Size { get; }

    /// <summary>Accepts 256, 384 and 512 pixels, so small patches aren't enlarged (a quarter of the work at 256).</summary>
    bool FlexibleSize => false;

    /// <summary>How many patches to send per call (more on a graphics card).</summary>
    int BatchSize { get; }

    /// <summary>Where the model runs, e.g. "DirectML on AMD Radeon RX 6850M XT".</summary>
    string DeviceLabel { get; }

    Task<float[]> RunAsync(float[] images, float[] masks, int count, int size, CancellationToken ct);
}

/// <summary>
/// AI fill for removal. The text area of a frame is covered with square patches around the text; each
/// patch is scaled to the model's input, filled by the model and pasted back over the text pixels only.
/// Patches whose surroundings are smooth (out-of-focus backgrounds, sky) are left to the ordinary fill,
/// which looks the same there and costs nothing; only patches with detail go to the model.
/// </summary>
public sealed class AiInpainter
{
    private readonly IInpaintModel _model;

    public AiInpainter(IInpaintModel model) => _model = model;

    public IInpaintModel Model => _model;

    /// <summary>Mean gradient (luma levels per pixel) around the text above which a patch goes to the model.</summary>
    public double DetailThreshold { get; set; } = 1.8;

    public int PatchesRun { get; private set; }
    public int PatchesSkipped { get; private set; }

    /// <summary>Model calls made, and the time spent inside them (for the timing line in the Log).</summary>
    public int Calls { get; private set; }

    /// <summary>Patches repainted per model size (for the Log).</summary>
    public Dictionary<int, int> PatchesBySize { get; } = new();
    public TimeSpan ModelTime { get; private set; }

    /// <summary>
    /// A square region of a frame (frame pixels) and its mask; <paramref name="Closed"/> is the same
    /// region of the mask with the gaps between letters closed (<see cref="CloseGaps"/>), if planned.
    /// </summary>
    public sealed record Patch(int FrameIndex, int X, int Y, int Size, byte[] Mask, byte[]? Closed = null);

    /// <summary>
    /// Patches whose surroundings are smoother than this (mean gradient, as <see cref="DetailThreshold"/>)
    /// are repainted with the closed mask: that is where the model echoes letter shapes. Over detailed
    /// pictures the letter mask stays, so the real picture between letters is kept.
    /// </summary>
    public double CloseGapsBelowDetail { get; set; } = 1.8;

    /// <summary>Patches repainted with the closed mask (for the Log).</summary>
    public int PatchesClosed { get; private set; }

    /// <summary>Gaps closed in the model's mask, as a share of the line height (see <see cref="CloseGaps"/>).</summary>
    public const double GapShare = 0.35;

    /// <summary>
    /// The text mask with the gaps between letters, inside letters and between words closed (dilate,
    /// then erode by <see cref="GapShare"/> of a line), so each line becomes one smooth shape. Given the
    /// letter-shaped mask, LaMa echoes the letters faintly on dark smooth backgrounds (ghost text);
    /// with the gaps closed there is no shape left to echo. The shape is then grown by a few pixels
    /// (<see cref="GrowShare"/>, at least 3) so the faint anti-aliased rim of the text isn't left as
    /// context: the model would draw it on as a thin line along the edge.
    /// </summary>
    public static byte[] CloseGaps(byte[] cover, int width, int height, double linePx)
    {
        int r = (int)(linePx * GapShare);
        if (r < 1) return cover;
        var closed = SubtitleRemover.Erode(SubtitleRemover.Dilate(cover, width, height, r), width, height, r);
        for (int i = 0; i < closed.Length; i++) closed[i] |= cover[i];
        return SubtitleRemover.Dilate(closed, width, height, Math.Max(3, (int)Math.Round(linePx * GrowShare)));
    }

    /// <summary>How far the closed mask is grown, as a share of the line height.</summary>
    public const double GrowShare = 0.07;

    /// <summary>
    /// Square patches covering the text of one frame. <paramref name="cover"/> is the text mask of the
    /// band [bandY, bandY+bandH); patches are about 2.2 text heights tall (at least 256 px, at most the
    /// frame height), overlapping by a quarter.
    /// </summary>
    public static List<Patch> PlanPatches(int frameIndex, int frameW, int frameH, int bandY, int bandH, byte[] cover, byte[]? closedCover = null)
    {
        var patches = new List<Patch>();
        int x0 = frameW, x1 = -1, y0 = bandH, y1 = -1;
        for (int y = 0; y < bandH; y++)
            for (int x = 0, p = y * frameW; x < frameW; x++, p++)
                if (cover[p] == 1)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < 0) return patches;
        x1++; y1++;
        int fy0 = bandY + y0, fy1 = bandY + y1;

        int size = Math.Min(Math.Min(frameH, frameW), Math.Max(256, (int)((fy1 - fy0) * 2.2)));
        int top = Math.Clamp((fy0 + fy1) / 2 - size / 2, 0, frameH - size);
        int step = Math.Max(1, size * 3 / 4);
        int span = x1 - x0;
        int count = Math.Max(1, (int)Math.Ceiling((span - size) / (double)step) + 1);
        if (span <= size) count = 1;
        for (int k = 0; k < count; k++)
        {
            int left = count == 1
                ? (x0 + x1) / 2 - size / 2
                : x0 + (int)Math.Round((span - size) * (k / (double)(count - 1)));
            left = Math.Clamp(left, 0, frameW - size);

            var mask = new byte[size * size];
            var closed = closedCover is null ? null : new byte[size * size];
            bool any = false;
            for (int y = 0; y < size; y++)
            {
                int by = top + y - bandY;
                if (by < 0 || by >= bandH) continue;
                Buffer.BlockCopy(cover, by * frameW + left, mask, y * size, size);
                if (closed is not null) Buffer.BlockCopy(closedCover!, by * frameW + left, closed, y * size, size);
                for (int x = 0; x < size && !any; x++) any |= mask[y * size + x] == 1;
            }
            if (any) patches.Add(new Patch(frameIndex, left, top, size, mask, closed));
        }
        return patches;
    }

    /// <summary>
    /// Mean luma gradient of the picture in a ring around the text in a patch: low for smooth or
    /// out-of-focus backgrounds (where the ordinary fill is as good), high where there is detail.
    /// </summary>
    public static double Detail(FrameSample frame, Patch patch)
    {
        int s = patch.Size, w = frame.Width;
        var ring = SubtitleRemover.Dilate(patch.Mask, s, s, Math.Max(4, s / 32));
        double sum = 0;
        int n = 0;
        for (int y = 1; y < s - 1; y++)
            for (int x = 1; x < s - 1; x++)
            {
                int p = y * s + x;
                if (ring[p] == 0 || patch.Mask[p] == 1) continue;
                // Both neighbours used for a gradient must be real picture, not text.
                if (patch.Mask[p + 1] == 1 || patch.Mask[p + s] == 1) continue;
                int i = ((patch.Y + y) * w + patch.X + x) * 4;
                int l = Luma(frame.Bgra, i), lr = Luma(frame.Bgra, i + 4), ld = Luma(frame.Bgra, i + w * 4);
                sum += Math.Abs(lr - l) + Math.Abs(ld - l);
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    private static int Luma(byte[] bgra, int i) => (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;

    // ------------------------------------------------------------------ reuse across frames
    //
    // A subtitle stays on screen for a second or more, and in many shots the picture behind it hardly
    // changes. A repainted patch is kept for a while; a later patch whose surroundings still match the
    // kept one (same place, text within the kept text area) takes the kept pixels instead of another
    // model call. Compared with the frame the model actually ran on, so small changes never add up.

    /// <summary>Reuse model results across frames when the picture behind the text hasn't changed.</summary>
    public bool ReuseAcrossFrames { get; set; } = true;

    /// <summary>Largest mean luma difference (levels) around the text for a reuse.</summary>
    public double ReuseMeanDifference { get; set; } = 2.0;

    /// <summary>Largest share of compared pixels that differ by more than 16 levels (a moving edge, a person).</summary>
    public double ReuseOutlierShare { get; set; } = 0.002;

    /// <summary>How long a result is kept, in frames (about 2 seconds).</summary>
    public int ReuseMaxAgeFrames { get; set; } = 48;

    public int PatchesReused { get; private set; }

    /// <summary>A repainted square: the frame's pixels after the model's result was pasted in.</summary>
    private sealed class Kept
    {
        public required int X, Y, Size;
        public required long Frame;
        public required byte[] Pixels; // BGRA, Size x Size
        public required byte[] Mask;   // Size x Size, 1 = repainted
        public required byte[] Near;   // Mask widened by 3 pixels
    }

    private readonly List<Kept> _kept = new();
    private long _frameBase;

    /// <summary>Where a patch's pixels come from: a model run in this call, or a kept square.</summary>
    private sealed record Source(int Leader, Kept? Kept);

    /// <summary>
    /// Fills the text in the given frames with the model, patch by patch, in batches. Frames must
    /// already hold the ordinary fill (patches skipped as smooth keep it). Frames arrive in video order,
    /// call after call (results are reused across calls).
    /// </summary>
    public async Task FillAsync(IReadOnlyList<FrameSample> frames, IReadOnlyList<Patch> patches, CancellationToken ct)
    {
        var detail = new double[patches.Count];
        Parallel.For(0, patches.Count, i => detail[i] = Detail(frames[patches[i].FrameIndex], patches[i]));
        // One method per frame: if any of a frame's patches needs the model, all of them get it. Mixing
        // the model and the ordinary fill along one line of text left a straight-edged step where
        // neighbouring patches met (a kerb that jumped, a lighter rectangle on a road).
        var needsModel = new HashSet<int>();
        for (int i = 0; i < patches.Count; i++)
            if (detail[i] >= DetailThreshold) needsModel.Add(patches[i].FrameIndex);
        var chosen = new List<Patch>();
        for (int i = 0; i < patches.Count; i++)
        {
            if (!needsModel.Contains(patches[i].FrameIndex)) { PatchesSkipped++; continue; }
            // Smooth surroundings: one smooth shape per line, so no letter shapes are echoed.
            if (patches[i].Closed is { } closed && detail[i] < CloseGapsBelowDetail)
            {
                chosen.Add(patches[i] with { Mask = closed, Closed = null });
                PatchesClosed++;
            }
            else chosen.Add(patches[i]);
        }

        // Decide per patch: run the model, or reuse (a kept square, or a patch of an earlier frame of this
        // call that runs the model). Comparisons read only pixels outside the text, which pasting never changes.
        var leaders = new List<Patch>();
        var sources = new Source?[chosen.Count];
        var leaderNear = new List<byte[]>();
        for (int i = 0; i < chosen.Count; i++)
        {
            var p = chosen[i];
            if (ReuseAcrossFrames)
            {
                long frameNo = _frameBase + p.FrameIndex;
                int tries = 0;
                foreach (var k in _kept.OrderByDescending(k => k.Frame))
                {
                    if (frameNo - k.Frame > ReuseMaxAgeFrames || !Near(p, k.X, k.Y)) continue;
                    if (++tries > 2) break; // the latest squares here didn't match: older ones won't either
                    if (Matches(frames[p.FrameIndex], p, k.X, k.Y, k.Size, k.Mask, k.Near, (x, y) => k.Pixels.AsSpan((y * k.Size + x) * 4, 4)))
                    {
                        sources[i] = new Source(-1, k);
                        break;
                    }
                }
                if (sources[i] is null)
                {
                    int leaderTries = 0;
                    for (int l = leaders.Count - 1; l >= 0 && leaderTries < 2; l--)
                    {
                        var leader = leaders[l];
                        if (leader.FrameIndex == p.FrameIndex || !Near(p, leader.X, leader.Y)) continue;
                        leaderTries++;
                        var lf = frames[leader.FrameIndex];
                        if (Matches(frames[p.FrameIndex], p, leader.X, leader.Y, leader.Size, leader.Mask, leaderNear[l],
                                (x, y) => lf.Bgra.AsSpan(((leader.Y + y) * lf.Width + leader.X + x) * 4, 4)))
                        {
                            sources[i] = new Source(l, null);
                            break;
                        }
                    }
                }
            }
            if (sources[i] is null)
            {
                leaders.Add(p);
                leaderNear.Add(ReuseAcrossFrames ? SubtitleRemover.Dilate(p.Mask, p.Size, p.Size, 3) : Array.Empty<byte>());
            }
        }

        // Model calls, patches of the same model size together. While the graphics card runs one call, the
        // processor prepares the next. Results are pasted back afterwards: frames in parallel, and within
        // a frame in the original order, since neighbouring patches overlap and the later one wins.
        int batch = Math.Max(1, _model.BatchSize);
        var calls = new List<(int Size, List<int> Members)>();
        foreach (var group in Enumerable.Range(0, leaders.Count).GroupBy(i => ModelSize(leaders[i].Size)))
        {
            var members = group.ToList();
            for (int start = 0; start < members.Count; start += batch)
                calls.Add((group.Key, members.GetRange(start, Math.Min(batch, members.Count - start))));
        }
        (float[] Images, float[] Masks) PrepareCall((int Size, List<int> Members) call)
        {
            int s = call.Size, plane = s * s, count = call.Members.Count;
            var images = new float[count * 3 * plane];
            var masks = new float[count * plane];
            Parallel.For(0, count, k =>
                Prepare(frames[leaders[call.Members[k]].FrameIndex], leaders[call.Members[k]], s, images.AsSpan(k * 3 * plane, 3 * plane), masks.AsSpan(k * plane, plane)));
            return (images, masks);
        }

        var results = new (float[] Result, int Offset, int Size)[leaders.Count];
        Task<(float[] Images, float[] Masks)>? next = calls.Count > 0 ? Task.Run(() => PrepareCall(calls[0]), ct) : null;
        for (int c = 0; c < calls.Count; c++)
        {
            ct.ThrowIfCancellationRequested();
            var (images, masks) = await next!.ConfigureAwait(false);
            var call = calls[c];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var running = _model.RunAsync(images, masks, call.Members.Count, call.Size, ct);
            if (c + 1 < calls.Count)
            {
                var following = calls[c + 1]; // copied: the loop variable moves on
                next = Task.Run(() => PrepareCall(following), ct);
            }
            else next = null;
            var result = await running.ConfigureAwait(false);
            ModelTime += System.Diagnostics.Stopwatch.GetElapsedTime(t0);
            Calls++;
            int plane = call.Size * call.Size;
            for (int k = 0; k < call.Members.Count; k++) results[call.Members[k]] = (result, k * 3 * plane, call.Size);
            PatchesRun += call.Members.Count;
            PatchesBySize[call.Size] = PatchesBySize.GetValueOrDefault(call.Size) + call.Members.Count;
        }
        Parallel.ForEach(Enumerable.Range(0, leaders.Count).GroupBy(i => leaders[i].FrameIndex), group =>
        {
            var members = group.ToList();
            var target = frames[group.Key];
            var repaints = members.Select(i =>
            {
                var (result, offset, s) = results[i];
                return (Patch: leaders[i], Pixels: Repaint(target, leaders[i], s, result.AsSpan(offset, 3 * s * s)));
            }).ToList();
            if (CrossFade) PasteBlended(target, repaints);
            else foreach (var r in repaints) PasteBlended(target, new() { r }); // the later patch wins
        });

        // Reused patches, in video order (their sources are complete now).
        for (int i = 0; i < chosen.Count; i++)
        {
            if (sources[i] is not { } src) continue;
            var p = chosen[i];
            var f = frames[p.FrameIndex];
            if (src.Kept is { } k) CopyRepainted(f, p, k.X, k.Y, k.Size, k.Mask, (x, y) => k.Pixels.AsSpan((y * k.Size + x) * 4, 4));
            else
            {
                var leader = leaders[src.Leader];
                var lf = frames[leader.FrameIndex];
                CopyRepainted(f, p, leader.X, leader.Y, leader.Size, leader.Mask, (x, y) => lf.Bgra.AsSpan(((leader.Y + y) * lf.Width + leader.X + x) * 4, 4));
            }
            PatchesReused++;
        }

        // Keep this call's repainted squares; drop old ones.
        if (ReuseAcrossFrames)
        {
            for (int li = 0; li < leaders.Count; li++)
            {
                var l = leaders[li];
                var f = frames[l.FrameIndex];
                var pixels = new byte[l.Size * l.Size * 4];
                for (int y = 0; y < l.Size; y++)
                    Buffer.BlockCopy(f.Bgra, ((l.Y + y) * f.Width + l.X) * 4, pixels, y * l.Size * 4, l.Size * 4);
                _kept.Add(new Kept { X = l.X, Y = l.Y, Size = l.Size, Frame = _frameBase + l.FrameIndex, Pixels = pixels, Mask = l.Mask, Near = leaderNear[li] });
            }
            long last = _frameBase + frames.Count - 1;
            _kept.RemoveAll(k => last - k.Frame > ReuseMaxAgeFrames);
            if (_kept.Count > 48) _kept.RemoveRange(0, _kept.Count - 48); // oldest first
        }
        _frameBase += frames.Count;
    }

    private delegate Span<byte> PixelAt(int x, int y);

    /// <summary>
    /// The model size a patch runs at: its own size rounded up to 256, 320, 384 or 512 when the model is
    /// flexible (the model needs multiples of 64; a few sizes keep the number of GPU sessions small),
    /// else the model's fixed size. Larger patches are scaled down to 512 as before.
    /// </summary>
    public int ModelSize(int patchSize)
        => !_model.FlexibleSize ? _model.Size
         : patchSize <= 256 ? 256
         : patchSize <= 320 ? 320
         : patchSize <= 384 ? 384
         : _model.Size;

    /// <summary>A square at (x, y) is in about the same place as the patch (within a quarter of its size).</summary>
    private static bool Near(Patch p, int x, int y) => Math.Abs(x - p.X) <= p.Size / 4 && Math.Abs(y - p.Y) <= p.Size / 4;

    /// <summary>
    /// Can patch <paramref name="p"/> of <paramref name="frame"/> take the repainted pixels of a square
    /// (at kx, ky, size ks, repainted where kMask = 1)? Its text must lie within the square's repainted
    /// area (a few edge pixels may not: they keep the ordinary fill), and the picture around the text,
    /// where both are real picture, must match: mean luma difference and share of large differences.
    /// </summary>
    private bool Matches(FrameSample frame, Patch p, int kx, int ky, int ks, byte[] kMask, byte[] kNear, PixelAt kept)
    {
        int x0 = Math.Max(p.X, kx), y0 = Math.Max(p.Y, ky), x1 = Math.Min(p.X + p.Size, kx + ks), y1 = Math.Min(p.Y + p.Size, ky + ks);
        if (x1 - x0 < p.Size / 2 || y1 - y0 < p.Size / 2) return false;

        // Text of the new patch inside the kept repainted area.
        int text = 0, outside = 0;
        for (int y = 0; y < p.Size; y++)
            for (int x = 0; x < p.Size; x++)
            {
                if (p.Mask[y * p.Size + x] == 0) continue;
                text++;
                int fx = p.X + x - kx, fy = p.Y + y - ky;
                if (fx < 0 || fy < 0 || fx >= ks || fy >= ks || kMask[fy * ks + fx] == 0) outside++;
            }
        if (text == 0 || outside > text / 100) return false;

        // The picture around it, on the overlap of the two squares, away from both texts.
        var near = SubtitleRemover.Dilate(p.Mask, p.Size, p.Size, 3);
        long compared = 0, big = 0;
        double sum = 0;
        int w = frame.Width;
        long area = (long)(x1 - x0) * (y1 - y0);
        // Early exits: at most `area` pixels are compared, so these totals can't pass any more.
        double sumLimit = area * ReuseMeanDifference, bigLimit = area * ReuseOutlierShare;
        for (int fy = y0; fy < y1; fy++)
        {
            if (sum > sumLimit || big > bigLimit) return false;
            for (int fx = x0; fx < x1; fx++)
            {
                if (near[(fy - p.Y) * p.Size + fx - p.X] == 1) continue;
                int qx = fx - kx, qy = fy - ky;
                if (kNear[qy * ks + qx] == 1) continue;
                int i = (fy * w + fx) * 4;
                var o = kept(qx, qy);
                int d = Math.Abs(Luma(frame.Bgra, i) - ((o[0] * 29 + o[1] * 150 + o[2] * 77) >> 8));
                sum += d;
                if (d > 16) big++;
                compared++;
            }
        }
        if (compared < area / 4) return false;
        return sum / compared <= ReuseMeanDifference && big <= compared * ReuseOutlierShare;
    }

    /// <summary>Copies the repainted pixels of a square over the text of a patch (where both overlap).</summary>
    private static void CopyRepainted(FrameSample frame, Patch p, int kx, int ky, int ks, byte[] kMask, PixelAt kept)
    {
        int w = frame.Width;
        for (int y = 0; y < p.Size; y++)
            for (int x = 0; x < p.Size; x++)
            {
                if (p.Mask[y * p.Size + x] == 0) continue;
                int qx = p.X + x - kx, qy = p.Y + y - ky;
                if (qx < 0 || qy < 0 || qx >= ks || qy >= ks || kMask[qy * ks + qx] == 0) continue;
                var o = kept(qx, qy);
                int i = ((p.Y + y) * w + p.X + x) * 4;
                frame.Bgra[i] = o[0];
                frame.Bgra[i + 1] = o[1];
                frame.Bgra[i + 2] = o[2];
            }
    }

    /// <summary>Patch → model input: bilinear scale to S x S, RGB planes 0..1; mask by nearest neighbour.</summary>
    private static void Prepare(FrameSample frame, Patch patch, int s, Span<float> image, Span<float> mask)
    {
        int w = frame.Width, plane = s * s, n = patch.Size;
        double scale = n / (double)s;
        var bgra = frame.Bgra;

        // Per column (the same for every row): the two source pixels (byte offsets within a frame row),
        // the blend weight, and the source columns covered for the mask.
        var xa = new int[s];
        var xb = new int[s];
        var tx = new float[s];
        var mx0 = new int[s];
        var mx1 = new int[s];
        for (int x = 0; x < s; x++)
        {
            double sx = Math.Clamp((x + 0.5) * scale - 0.5, 0, n - 1);
            int a = (int)sx;
            xa[x] = (patch.X + a) * 4;
            xb[x] = (patch.X + Math.Min(a + 1, n - 1)) * 4;
            tx[x] = (float)(sx - a);
            mx0[x] = (int)(x * scale);
            mx1[x] = Math.Min(n, Math.Max(mx0[x] + 1, (int)Math.Ceiling((x + 1) * scale)));
        }

        const float inv = 1f / 255f;
        for (int y = 0; y < s; y++)
        {
            double sy = Math.Clamp((y + 0.5) * scale - 0.5, 0, n - 1);
            int ya = (int)sy;
            float ty = (float)(sy - ya);
            int rowA = (patch.Y + ya) * w * 4, rowB = (patch.Y + Math.Min(ya + 1, n - 1)) * w * 4;
            int my0 = (int)(y * scale), my1 = Math.Min(n, Math.Max(my0 + 1, (int)Math.Ceiling((y + 1) * scale)));
            int q0 = y * s;
            for (int x = 0; x < s; x++)
            {
                int p00 = rowA + xa[x], p10 = rowA + xb[x], p01 = rowB + xa[x], p11 = rowB + xb[x];
                float fx = tx[x], gx = 1 - fx, gy = 1 - ty;
                int q = q0 + x;
                for (int c = 0; c < 3; c++)
                {
                    int ch = 2 - c; // BGRA -> R, G, B planes
                    float top = bgra[p00 + ch] * gx + bgra[p10 + ch] * fx;
                    float bottom = bgra[p01 + ch] * gx + bgra[p11 + ch] * fx;
                    image[c * plane + q] = (top * gy + bottom * ty) * inv;
                }
                // Any text pixel in the source area of this model pixel marks it (no thin gaps).
                float m = 0;
                for (int yy = my0; yy < my1 && m == 0; yy++)
                {
                    int r = yy * n;
                    for (int xx = mx0[x]; xx < mx1[x]; xx++)
                        if (patch.Mask[r + xx] == 1) { m = 1; break; }
                }
                mask[q] = m;
            }
        }
    }

    private static readonly (int Dx, int Dy)[] Neighbours = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>Margin around the text where the model's colour is computed (for the edge), in pixels.</summary>
    public const int SeamRing = 1;

    /// <summary>Correct the seam between the model's result and the real picture (on by default; off for comparisons).</summary>
    public bool BlendSeams { get; set; } = true;

    /// <summary>
    /// Share of the edge difference removed. All of it makes the edge smoother than the picture itself
    /// (a soft halo); measured on real frames, this leaves the edge about as crisp as the real detail next to it.
    /// </summary>
    public float SeamStrength { get; set; } = 0.6f;

    /// <summary>Cross-fade overlapping patches (on by default; off only for comparisons).</summary>
    public bool CrossFade { get; set; } = true;

    /// <summary>
    /// Model output → patch size (bilinear), written over the text pixels only. Seam correction: at the
    /// edge of the text, the model's pixels and the real picture next to them should agree; where they
    /// don't (a slightly different brightness or colour) the repainted area would show as an outline or
    /// a step. The difference is measured along the edge, spread smoothly across the text area
    /// (pull-push), and removed from the model's pixels, so the repainted area meets the picture without
    /// an edge. A light form of seamless (Poisson) blending.
    /// </summary>
    private float[] Repaint(FrameSample frame, Patch patch, int s, ReadOnlySpan<float> result)
    {
        int w = frame.Width, n = patch.Size, plane = s * s, count = n * n;
        double scale = s / (double)n;
        var mask = patch.Mask;

        // The model's colour at every patch pixel (needed on the ring as well as on the text).
        var ring = BlendSeams ? SubtitleRemover.Dilate(mask, n, n, SeamRing) : null;
        var ai = new float[3 * count];
        for (int y = 0; y < n; y++)
        {
            double sy = Math.Clamp((y + 0.5) * scale - 0.5, 0, s - 1);
            int ya = (int)sy, yb = Math.Min(ya + 1, s - 1);
            double ty = sy - ya;
            for (int x = 0; x < n; x++)
            {
                int q = y * n + x;
                if (mask[q] == 0 && (ring is null || ring[q] == 0)) continue;
                double sx = Math.Clamp((x + 0.5) * scale - 0.5, 0, s - 1);
                int xa = (int)sx, xb = Math.Min(xa + 1, s - 1);
                double tx = sx - xa;
                for (int c = 0; c < 3; c++)
                {
                    int o = c * plane;
                    ai[c * count + q] = (float)((result[o + ya * s + xa] * (1 - tx) + result[o + ya * s + xb] * tx) * (1 - ty)
                                              + (result[o + yb * s + xa] * (1 - tx) + result[o + yb * s + xb] * tx) * ty);
                }
            }
        }

        float[]? offset = null;
        if (ring is not null)
        {
            // Along the edge of the text: the real neighbours just outside minus the model's pixel just
            // inside (the step a viewer would see), then filled in smoothly across the text. The model's
            // own texture stays; only the slow brightness / colour difference goes. Capped, so an unusual
            // edge (a real contour running along the text) can't produce a large change.
            offset = new float[3 * count];
            var known = new float[count];
            const float cap = 24f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int q = y * n + x;
                    if (mask[q] == 0) continue;
                    float r = 0, g = 0, b = 0;
                    int m = 0;
                    foreach (var (dx, dy) in Neighbours)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= n || ny >= n || mask[ny * n + nx] == 1) continue;
                        int i = ((patch.Y + ny) * w + patch.X + nx) * 4;
                        r += frame.Bgra[i + 2]; g += frame.Bgra[i + 1]; b += frame.Bgra[i];
                        m++;
                    }
                    if (m == 0) continue; // inside the text, not on its edge
                    known[q] = 1;
                    offset[q] = Math.Clamp(r / m - ai[q], -cap, cap);
                    offset[count + q] = Math.Clamp(g / m - ai[count + q], -cap, cap);
                    offset[2 * count + q] = Math.Clamp(b / m - ai[2 * count + q], -cap, cap);
                }
            PullPush(offset, known, n);
            for (int k = 0; k < offset.Length; k++) offset[k] *= SeamStrength;
        }

        if (offset is not null)
            for (int k = 0; k < ai.Length; k++) ai[k] += offset[k];
        return ai; // R, G, B planes; valid where the mask is set
    }

    /// <summary>
    /// Writes a frame's repainted patches. Neighbouring patches overlap by about a quarter, and each
    /// invents the hidden picture a little differently: letting the later one simply win left a straight
    /// vertical step where it took over (a car door or a kerb that jumped). Now they hand over gradually,
    /// but only in a narrow band (16 px at 256) at the middle of the overlap: a wide 50/50 blend would
    /// show both inventions at once (a doubled passer-by). Each patch's own picture is kept either side.
    /// </summary>
    private static void PasteBlended(FrameSample frame, List<(Patch Patch, float[] Pixels)> repaints)
    {
        int w = frame.Width;
        var sorted = repaints.OrderBy(r => r.Patch.X).ToList();
        int x0 = sorted.Min(r => r.Patch.X), y0 = sorted.Min(r => r.Patch.Y);
        int x1 = sorted.Max(r => r.Patch.X + r.Patch.Size), y1 = sorted.Max(r => r.Patch.Y + r.Patch.Size);
        int aw = x1 - x0, ah = y1 - y0;

        // Per patch and column: its weight (1 inside it, 0 outside; smooth hand-over in overlaps).
        var weights = new float[sorted.Count][];
        for (int k = 0; k < sorted.Count; k++)
        {
            var p = sorted[k].Patch;
            var wk = new float[aw];
            for (int x = p.X; x < p.X + p.Size; x++) wk[x - x0] = 1;
            weights[k] = wk;
        }
        for (int k = 0; k + 1 < sorted.Count; k++)
        {
            var a = sorted[k].Patch;
            for (int j = k + 1; j < sorted.Count; j++)
            {
                var b = sorted[j].Patch;
                int start = b.X, end = Math.Min(a.X + a.Size, b.X + b.Size);
                if (end <= start) continue;
                double mid = (start + end) / 2.0, r = Math.Max(4, a.Size / 32);
                for (int x = start; x < end; x++)
                {
                    double t = Math.Clamp((x + 0.5 - (mid - r)) / (2 * r), 0, 1);
                    t = t * t * (3 - 2 * t); // smoothstep
                    weights[k][x - x0] = Math.Min(weights[k][x - x0], (float)(1 - t));
                    weights[j][x - x0] = Math.Min(weights[j][x - x0], (float)t);
                }
            }
        }

        var sum = new float[3 * aw * ah];
        var total = new float[aw * ah];
        // Weight of patches that keep the picture at a pixel another patch repaints: a patch with the
        // gaps between letters closed next to one without them fades into the real picture across the
        // hand-over, instead of stopping at its edge with a step.
        var keep = new float[aw * ah];
        var any = new bool[aw * ah];
        var fallback = new int[aw * ah];
        for (int k = 0; k < sorted.Count; k++)
        {
            var (p, v) = sorted[k];
            int n = p.Size, count = n * n;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int q = y * n + x;
                    int ax = p.X + x - x0, a = (p.Y + y - y0) * aw + ax;
                    if (p.Mask[q] == 0) { keep[a] += weights[k][ax]; continue; }
                    if (!any[a]) { any[a] = true; fallback[a] = k; }
                    float wt = weights[k][ax];
                    if (wt <= 0) continue;
                    total[a] += wt;
                    for (int c = 0; c < 3; c++) sum[c * aw * ah + a] += wt * v[c * count + q];
                }
        }
        for (int y = 0; y < ah; y++)
            for (int x = 0; x < aw; x++)
            {
                int a = y * aw + x;
                if (!any[a]) continue;
                int i = ((y0 + y) * w + x0 + x) * 4;
                if (total[a] > 0 || keep[a] > 0)
                {
                    float all = total[a] + keep[a];
                    for (int c = 0; c < 3; c++)
                        frame.Bgra[i + 2 - c] = (byte)Math.Clamp((sum[c * aw * ah + a] + keep[a] * frame.Bgra[i + 2 - c]) / all + 0.5f, 0, 255);
                }
                else
                {
                    // Only a patch whose weight is 0 here has this pixel in its text: use it as it is.
                    var (p, v) = sorted[fallback[a]];
                    int n = p.Size, q = (y0 + y - p.Y) * n + x0 + x - p.X;
                    for (int c = 0; c < 3; c++) frame.Bgra[i + 2 - c] = (byte)Math.Clamp(v[c * n * n + q] + 0.5f, 0, 255);
                }
            }
    }

    /// <summary>
    /// Fills the unknown values of a smooth field (3 planes of n x n; <paramref name="known"/> 1 where set)
    /// from the known ones: averaged down a pyramid, then blended back up (bilinear). O(n²).
    /// </summary>
    internal static void PullPush(float[] values, float[] known, int n)
    {
        int count = n * n;
        if (n <= 1) return;
        int half = (n + 1) / 2;
        var coarse = new float[3 * half * half];
        var weight = new float[half * half];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float k = known[y * n + x];
                if (k == 0) continue;
                int cq = (y / 2) * half + x / 2;
                weight[cq] += k;
                for (int c = 0; c < 3; c++) coarse[c * half * half + cq] += k * values[c * count + y * n + x];
            }
        bool any = false;
        for (int cq = 0; cq < half * half; cq++)
        {
            if (weight[cq] <= 0) continue;
            any = true;
            for (int c = 0; c < 3; c++) coarse[c * half * half + cq] /= weight[cq];
            weight[cq] = Math.Min(1f, weight[cq]);
        }
        if (!any) { Array.Clear(values); return; }
        if (half < n) PullPush(coarse, weight, half);

        for (int y = 0; y < n; y++)
        {
            double cy = Math.Clamp((y + 0.5) / 2 - 0.5, 0, half - 1);
            int ya = (int)cy, yb = Math.Min(ya + 1, half - 1);
            float ty = (float)(cy - ya);
            for (int x = 0; x < n; x++)
            {
                int q = y * n + x;
                float k = known[q];
                if (k >= 1) continue;
                double cx = Math.Clamp((x + 0.5) / 2 - 0.5, 0, half - 1);
                int xa = (int)cx, xb = Math.Min(xa + 1, half - 1);
                float tx = (float)(cx - xa);
                for (int c = 0; c < 3; c++)
                {
                    int o = c * half * half;
                    float up = (coarse[o + ya * half + xa] * (1 - tx) + coarse[o + ya * half + xb] * tx) * (1 - ty)
                             + (coarse[o + yb * half + xa] * (1 - tx) + coarse[o + yb * half + xb] * tx) * ty;
                    values[c * count + q] = k * values[c * count + q] + (1 - k) * up;
                }
            }
        }
    }
}
