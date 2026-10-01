using System.IO.Compression;
using System.Text;

namespace SubtitleStudio.Services.Video;

/// <summary>
/// Writes a <see cref="FrameSample"/> as a PNG (RGB, 8 bit). Plain .NET (zlib + CRC-32), no WPF,
/// so services can save frames for troubleshooting.
/// </summary>
public static class PngWriter
{
    public static void Save(FrameSample frame, string path)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            Write(frame, fs);
        File.Move(tmp, path, overwrite: true);
    }

    public static void Write(FrameSample frame, Stream output)
    {
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, frame.Width);
        WriteBigEndian(header, 4, frame.Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: RGB
        WriteChunk(output, "IHDR", header);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[1 + frame.Width * 3]; // filter byte 0 + RGB
                for (int y = 0; y < frame.Height; y++)
                {
                    int src = y * frame.Width * 4;
                    for (int x = 0, d = 1; x < frame.Width; x++, src += 4, d += 3)
                    {
                        row[d] = frame.Bgra[src + 2];
                        row[d + 1] = frame.Bgra[src + 1];
                        row[d + 2] = frame.Bgra[src];
                    }
                    z.Write(row);
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, data.Length);
        output.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
