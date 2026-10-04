using System.Buffers.Binary;
using System.Globalization;

namespace SubtitleStudio.Services.Extraction;

/// <summary>
/// One picture subtitle (PGS from Blu-ray, VobSub from DVD), reduced to its text: <see cref="Ink"/> is 255
/// where the letters' fill is and 0 elsewhere (outline, shadow and background dropped), ready to read.
/// </summary>
public sealed class SubtitlePicture
{
    public TimeSpan Start { get; set; }
    public TimeSpan? End { get; set; }
    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Ink { get; init; } = Array.Empty<byte>();
    public bool Forced { get; init; }

    /// <summary>Shown in the top part of the picture (signs, or lines moved up): written with {\an8}.</summary>
    public bool AtTop => CanvasHeight > 0 && Y + Height < CanvasHeight * 0.4;

    /// <summary>The same letters in the same place (pictures sent again unchanged).</summary>
    public bool SameAs(SubtitlePicture other)
        => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height && Ink.AsSpan().SequenceEqual(other.Ink);

    /// <summary>A key for the letters alone (identical pictures are read once).</summary>
    public string InkKey()
    {
        var hash = System.Security.Cryptography.SHA1.HashData(Ink);
        return $"{Width}x{Height}:{Convert.ToHexString(hash)}";
    }
}

/// <summary>Turns the colours of a subtitle picture into "ink": the fill of the letters, without the outline.</summary>
public static class InkExtractor
{
    /// <param name="groupOf">Per pixel: -1 for transparent, else which colour group it belongs to.</param>
    /// <remarks>
    /// Subtitles are drawn as a fill (usually white or yellow) with an outline (usually black) around it.
    /// The outline is the group that touches the transparent background most; the fill is the other one.
    /// Groups with very few pixels (anti-aliasing) are ignored when choosing.
    /// </remarks>
    public static byte[] Extract(int width, int height, int[] groupOf)
    {
        var count = new Dictionary<int, int>();
        var border = new Dictionary<int, int>();
        int opaque = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int g = groupOf[y * width + x];
                if (g < 0) continue;
                opaque++;
                count[g] = count.GetValueOrDefault(g) + 1;
                bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1
                            || groupOf[y * width + x - 1] < 0 || groupOf[y * width + x + 1] < 0
                            || groupOf[(y - 1) * width + x] < 0 || groupOf[(y + 1) * width + x] < 0;
                if (edge) border[g] = border.GetValueOrDefault(g) + 1;
            }
        }
        var ink = new byte[width * height];
        if (opaque == 0) return ink;
        var candidates = count.Where(kv => kv.Value >= Math.Max(4, opaque * 0.08)).Select(kv => kv.Key).ToList();
        if (candidates.Count == 0) candidates = count.Keys.ToList();
        int fill = candidates.OrderBy(g => border.GetValueOrDefault(g) / (double)count[g]).ThenByDescending(g => count[g]).First();
        for (int i = 0; i < ink.Length; i++)
            if (groupOf[i] == fill) ink[i] = 255;
        return ink;
    }
}

/// <summary>
/// Reads PGS (Blu-ray "Presentation Graphic Stream") subtitles from a .sup file: display sets of
/// composition, window, palette and object segments; run-length coded pictures in up to 256 colours.
/// </summary>
public static class PgsDecoder
{
    private sealed class PgsObject
    {
        public int Width, Height;
        public readonly List<byte> Data = new();
    }

    private sealed record Placement(int ObjectId, int X, int Y, bool Forced, int CropX, int CropY, int CropW, int CropH, bool Cropped);

    /// <summary>All pictures in the stream, with their start and end times.</summary>
    public static List<SubtitlePicture> Decode(Stream sup)
    {
        var pictures = new List<SubtitlePicture>();
        var palettes = new Dictionary<int, (byte Y, byte A)[]>();
        var objects = new Dictionary<int, PgsObject>();
        var placements = new List<Placement>();
        int canvasW = 0, canvasH = 0, paletteId = 0;
        bool composed = false;
        TimeSpan setTime = TimeSpan.Zero;
        SubtitlePicture? open = null;
        var header = new byte[13];

        while (ReadExactly(sup, header))
        {
            if (header[0] != (byte)'P' || header[1] != (byte)'G') throw new InvalidDataException("This isn't a PGS (.sup) subtitle stream.");
            uint pts = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2));
            byte type = header[10];
            int size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(11));
            var payload = new byte[size];
            if (!ReadExactly(sup, payload)) break;
            var p = payload.AsSpan();
            switch (type)
            {
                case 0x16 when size >= 11: // presentation composition
                    setTime = TimeSpan.FromSeconds(pts / 90000.0);
                    canvasW = BinaryPrimitives.ReadUInt16BigEndian(p);
                    canvasH = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
                    byte state = p[7];
                    paletteId = p[9];
                    int n = p[10];
                    if ((state & 0xC0) != 0) objects.Clear(); // epoch start / acquisition point: objects are sent again
                    placements.Clear();
                    int at = 11;
                    for (int i = 0; i < n && at + 8 <= size; i++)
                    {
                        int id = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
                        byte flags = p[at + 3];
                        int x = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 4)..]);
                        int y = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 6)..]);
                        at += 8;
                        bool cropped = (flags & 0x80) != 0;
                        int cx = 0, cy = 0, cw = 0, ch = 0;
                        if (cropped && at + 8 <= size)
                        {
                            cx = BinaryPrimitives.ReadUInt16BigEndian(p[at..]);
                            cy = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 2)..]);
                            cw = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 4)..]);
                            ch = BinaryPrimitives.ReadUInt16BigEndian(p[(at + 6)..]);
                            at += 8;
                        }
                        placements.Add(new Placement(id, x, y, (flags & 0x40) != 0, cx, cy, cw, ch, cropped));
                    }
                    composed = true;
                    break;
                case 0x14 when size >= 2: // palette
                    var palette = palettes.TryGetValue(p[0], out var known) ? known : new (byte, byte)[256];
                    for (int at2 = 2; at2 + 5 <= size; at2 += 5) palette[p[at2]] = (p[at2 + 1], p[at2 + 4]);
                    palettes[p[0]] = palette;
                    break;
                case 0x15 when size >= 4: // object (picture), possibly in several pieces
                    int objectId = BinaryPrimitives.ReadUInt16BigEndian(p);
                    byte sequence = p[3];
                    if ((sequence & 0x80) != 0 && size >= 11)
                    {
                        var o = new PgsObject
                        {
                            Width = BinaryPrimitives.ReadUInt16BigEndian(p[7..]),
                            Height = BinaryPrimitives.ReadUInt16BigEndian(p[9..]),
                        };
                        o.Data.AddRange(payload.Skip(11));
                        objects[objectId] = o;
                    }
                    else if (objects.TryGetValue(objectId, out var o2))
                        o2.Data.AddRange(payload.Skip(4));
                    break;
                case 0x80: // end of display set
                    if (!composed) break;
                    composed = false;
                    if (open is not null)
                    {
                        open.End = setTime;
                        open = null;
                    }
                    if (placements.Count == 0) break;
                    var pic = Render(placements, objects, palettes.GetValueOrDefault(paletteId), canvasW, canvasH, setTime);
                    if (pic is null) break;
                    pictures.Add(pic);
                    open = pic;
                    break;
            }
        }
        return pictures;
    }

    private static SubtitlePicture? Render(List<Placement> placements, Dictionary<int, PgsObject> objects, (byte Y, byte A)[]? palette,
        int canvasW, int canvasH, TimeSpan start)
    {
        if (palette is null) return null;
        var parts = new List<(Placement P, int W, int H, byte[] Pixels)>();
        foreach (var pl in placements)
        {
            if (!objects.TryGetValue(pl.ObjectId, out var o) || o.Width <= 0 || o.Height <= 0) continue;
            var pixels = DecodeRle(o.Data, o.Width, o.Height);
            if (pl.Cropped && pl.CropW > 0 && pl.CropH > 0)
            {
                var cut = new byte[pl.CropW * pl.CropH];
                for (int y = 0; y < pl.CropH && pl.CropY + y < o.Height; y++)
                    for (int x = 0; x < pl.CropW && pl.CropX + x < o.Width; x++)
                        cut[y * pl.CropW + x] = pixels[(pl.CropY + y) * o.Width + pl.CropX + x];
                parts.Add((pl, pl.CropW, pl.CropH, cut));
            }
            else parts.Add((pl, o.Width, o.Height, pixels));
        }
        if (parts.Count == 0) return null;
        int left = parts.Min(q => q.P.X), top = parts.Min(q => q.P.Y);
        int right = parts.Max(q => q.P.X + q.W), bottom = parts.Max(q => q.P.Y + q.H);
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0 || (long)w * h > 40_000_000) return null;
        // Light and dark opaque pixels are the two groups (fill and outline); transparent is -1.
        var groups = new int[w * h];
        Array.Fill(groups, -1);
        foreach (var (pl, pw, ph, pixels) in parts)
        {
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    var (lum, alpha) = palette[pixels[y * pw + x]];
                    if (alpha < 128) continue;
                    groups[(pl.Y - top + y) * w + pl.X - left + x] = lum >= 128 ? 1 : 0;
                }
            }
        }
        return new SubtitlePicture
        {
            Start = start, CanvasWidth = canvasW, CanvasHeight = canvasH, X = left, Y = top, Width = w, Height = h,
            Ink = InkExtractor.Extract(w, h, groups), Forced = parts.Any(q => q.P.Forced),
        };
    }

    /// <summary>PGS run-length coding: a colour byte, or 0 then a code for runs and line ends.</summary>
    public static byte[] DecodeRle(IReadOnlyList<byte> data, int width, int height)
    {
        var pixels = new byte[width * height];
        int i = 0, x = 0, y = 0;
        while (i < data.Count && y < height)
        {
            byte b = data[i++];
            int length;
            byte colour;
            if (b != 0)
            {
                length = 1;
                colour = b;
            }
            else
            {
                if (i >= data.Count) break;
                byte f = data[i++];
                if (f == 0)
                {
                    x = 0;
                    y++;
                    continue;
                }
                length = f & 0x3F;
                if ((f & 0x40) != 0)
                {
                    if (i >= data.Count) break;
                    length = (length << 8) | data[i++];
                }
                colour = 0;
                if ((f & 0x80) != 0)
                {
                    if (i >= data.Count) break;
                    colour = data[i++];
                }
            }
            for (int k = 0; k < length && x < width; k++) pixels[y * width + x++] = colour;
        }
        return pixels;
    }

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Reads DVD (VobSub) subtitle packets: a control sequence with the start and stop delays, the colours
/// and the area, and a 4-colour picture in two interlaced fields, run-length coded in nibbles.
/// </summary>
public static class VobSubDecoder
{
    /// <summary>One packet as the container gives it.</summary>
    public sealed record Packet(TimeSpan Time, TimeSpan? Duration, byte[] Data);

    /// <summary>Delays in control sequences count in 1024/90000 s.</summary>
    private const double DelayUnit = 1024.0 / 90000.0;

    /// <param name="palette">The 16-colour palette (from the .idx or the track header); used to tell fill from outline when it's known.</param>
    public static List<SubtitlePicture> Decode(IEnumerable<Packet> packets, uint[]? palette)
    {
        var pictures = new List<SubtitlePicture>();
        foreach (var packet in packets)
        {
            var pic = DecodeOne(packet, palette, out var shownAt);
            // An open picture ends when the next packet shows (or, with nothing in it, clears the screen).
            if (pictures.Count > 0 && pictures[^1].End is null && shownAt is { } at && at > pictures[^1].Start) pictures[^1].End = at;
            if (pic is not null) pictures.Add(pic);
        }
        return pictures;
    }

    /// <param name="shownAt">When the packet takes the screen (its picture, or an empty one that clears it).</param>
    public static SubtitlePicture? DecodeOne(Packet packet, uint[]? palette, out TimeSpan? shownAt)
    {
        shownAt = null;
        var d = packet.Data;
        if (d.Length < 4) return null;
        int size = BinaryPrimitives.ReadUInt16BigEndian(d);
        int control = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(2));
        if (size > d.Length || control >= size) return null;

        double? startDelay = null, stopDelay = null;
        bool forced = false;
        int[] colour = { 0, 1, 2, 3 }, alpha = { 0, 15, 15, 15 };
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, top = -1, bottom = -1;
        int at = control;
        for (int guard = 0; guard < 64 && at + 4 <= size; guard++)
        {
            int rawDelay = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at));
            double delay = rawDelay * DelayUnit;
            int next = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at + 2));
            int c = at + 4;
            while (c < size)
            {
                byte cmd = d[c++];
                if (cmd == 0xFF) break;
                switch (cmd)
                {
                    case 0x00:
                        forced = true;
                        startDelay ??= delay;
                        break;
                    case 0x01:
                        startDelay ??= delay;
                        break;
                    case 0x02 when rawDelay != 0xFFFF: // 0xFFFF: "until further notice" (FFmpeg writes it when the end isn't known)
                        stopDelay ??= delay;
                        break;
                    case 0x02:
                        break;
                    case 0x03 when c + 2 <= size:
                        colour = new[] { d[c + 1] & 0xF, d[c + 1] >> 4, d[c] & 0xF, d[c] >> 4 };
                        c += 2;
                        break;
                    case 0x04 when c + 2 <= size:
                        alpha = new[] { d[c + 1] & 0xF, d[c + 1] >> 4, d[c] & 0xF, d[c] >> 4 };
                        c += 2;
                        break;
                    case 0x05 when c + 6 <= size:
                        x1 = (d[c] << 4) | (d[c + 1] >> 4);
                        x2 = ((d[c + 1] & 0xF) << 8) | d[c + 2];
                        y1 = (d[c + 3] << 4) | (d[c + 4] >> 4);
                        y2 = ((d[c + 4] & 0xF) << 8) | d[c + 5];
                        c += 6;
                        break;
                    case 0x06 when c + 4 <= size:
                        top = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(c));
                        bottom = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(c + 2));
                        c += 4;
                        break;
                    case 0x07 when c + 2 <= size: // colour/contrast changes over time: skipped
                        c += BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(c));
                        break;
                    default:
                        c = size;
                        break;
                }
            }
            if (next <= at || next >= size) break;
            at = next;
        }

        if (startDelay is not null) shownAt = packet.Time + TimeSpan.FromSeconds(startDelay.Value);
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        if (w <= 0 || h <= 0 || top < 0 || bottom < 0 || w > 4096 || h > 4096) return null;
        var pixels = new byte[w * h];
        DecodeField(d, top, size, pixels, w, h, 0);
        DecodeField(d, bottom, size, pixels, w, h, 1);

        // Groups: each of the 4 colours that is visible; with a palette, colours of the same brightness go together.
        var groups = new int[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int v = pixels[i];
            if (alpha[v] < 8) { groups[i] = -1; continue; }
            groups[i] = palette is { Length: 16 } ? (Luma(palette[colour[v] & 15]) >= 128 ? 1 : 0) : 10 + v;
        }
        var start = packet.Time + TimeSpan.FromSeconds(startDelay ?? 0);
        TimeSpan? end = stopDelay is { } s && s > (startDelay ?? 0) ? packet.Time + TimeSpan.FromSeconds(s)
            : packet.Duration is { } dur && dur > TimeSpan.Zero && dur < TimeSpan.FromHours(1) ? packet.Time + dur : null;
        return new SubtitlePicture
        {
            Start = start, End = end, X = x1, Y = y1, Width = w, Height = h, CanvasWidth = 720, CanvasHeight = y2 > 480 ? 576 : 480,
            Ink = InkExtractor.Extract(w, h, groups), Forced = forced,
        };
    }

    private static void DecodeField(byte[] d, int offset, int size, byte[] pixels, int w, int h, int firstLine)
    {
        int nib = offset * 2, end = size * 2;
        int Next() => nib < end ? (d[nib >> 1] >> ((nib++ & 1) == 0 ? 4 : 0)) & 0xF : 0;
        for (int y = firstLine; y < h; y += 2)
        {
            int x = 0;
            while (x < w && nib < end)
            {
                int v = Next();
                if (v < 0x4)
                {
                    v = (v << 4) | Next();
                    if (v < 0x10)
                    {
                        v = (v << 4) | Next();
                        if (v < 0x40) v = (v << 4) | Next();
                    }
                }
                int length = v >> 2;
                if (length == 0) length = w - x;
                for (int k = 0; k < length && x < w; k++) pixels[y * w + x++] = (byte)(v & 3);
            }
            if ((nib & 1) != 0) nib++; // lines start on a byte
        }
    }

    private static int Luma(uint rgb) => (int)((((rgb >> 16) & 0xFF) * 299 + ((rgb >> 8) & 0xFF) * 587 + (rgb & 0xFF) * 114) / 1000);

    /// <summary>"palette: 000000, 0000ff, ..." from an .idx file or a track header.</summary>
    public static uint[]? ParsePalette(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var line in header.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("palette:", StringComparison.OrdinalIgnoreCase)) continue;
            var values = t[8..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(v => uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) ? u : 0u).ToArray();
            return values.Length == 16 ? values : null;
        }
        return null;
    }
}
