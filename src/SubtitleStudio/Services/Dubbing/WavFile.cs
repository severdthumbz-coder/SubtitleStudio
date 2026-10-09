using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// 16-bit mono PCM WAV, written as it goes (an episode's voice track is too long to hold as floats).
/// The sizes in the header are filled in when it's closed.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _samples;
    private bool _closed;

    public WavWriter(string path, int sampleRate)
    {
        SampleRate = sampleRate;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        _writer.Write("RIFF"u8);
        _writer.Write(0);
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1);          // PCM
        _writer.Write((short)1);          // mono
        _writer.Write(sampleRate);
        _writer.Write(sampleRate * 2);    // bytes a second
        _writer.Write((short)2);          // bytes a frame
        _writer.Write((short)16);
        _writer.Write("data"u8);
        _writer.Write(0);
    }

    public int SampleRate { get; }

    public long SamplesWritten => _samples;

    private readonly byte[] _buffer = new byte[1 << 16];

    public void Write(ReadOnlySpan<float> samples)
    {
        _writer.Flush();
        while (samples.Length > 0)
        {
            int n = Math.Min(samples.Length, _buffer.Length / 2);
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                _buffer[2 * i] = (byte)v;
                _buffer[2 * i + 1] = (byte)(v >> 8);
            }
            _stream.Write(_buffer, 0, n * 2);
            _samples += n;
            samples = samples[n..];
        }
    }

    public void WriteSilence(long count)
    {
        _writer.Flush();
        Array.Clear(_buffer);
        while (count > 0)
        {
            int n = (int)Math.Min(count, _buffer.Length / 2);
            _stream.Write(_buffer, 0, n * 2);
            _samples += n;
            count -= n;
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _writer.Flush();
        long data = _samples * 2;
        // WAV sizes are 32-bit: a track over about 12 hours at 24 kHz can't be described (never the case for an episode).
        _stream.Position = 4;
        _writer.Write((uint)Math.Min(uint.MaxValue, 36 + data));
        _stream.Position = 40;
        _writer.Write((uint)Math.Min(uint.MaxValue, data));
        _writer.Flush();
    }

    public void Dispose()
    {
        try { Close(); }
        finally
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}

public static class WavFile
{
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var w = new WavWriter(path, sampleRate);
        w.Write(samples);
    }

    /// <summary>Reads a 16-bit PCM mono WAV (as written here) back as floats.</summary>
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        int rate = 0, bits = 0, channels = 0;
        while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                r.BaseStream.Position += size - 16;
            }
            else if (id == "data")
            {
                if (bits != 16 || channels != 1) throw new InvalidDataException("Only 16-bit mono WAV is read here.");
                int count = (int)Math.Min(size, r.BaseStream.Length - r.BaseStream.Position) / 2;
                var samples = new float[count];
                for (int i = 0; i < count; i++) samples[i] = r.ReadInt16() / 32768f;
                return (samples, rate);
            }
            else r.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidDataException("The WAV file has no audio.");
    }
}
