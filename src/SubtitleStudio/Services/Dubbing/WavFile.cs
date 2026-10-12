using System.Text;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>
/// 16-bit PCM WAV, mono or stereo, written as it goes (an episode's voice track is too long to hold as
/// floats). The sizes in the header are filled in when it's closed.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _samples;
    private bool _closed;

    public WavWriter(string path, int sampleRate, int channels = 1)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        SampleRate = sampleRate;
        Channels = channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        _writer.Write("RIFF"u8);
        _writer.Write(0);
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1);          // PCM
        _writer.Write((short)channels);
        _writer.Write(sampleRate);
        _writer.Write(sampleRate * 2 * channels); // bytes a second
        _writer.Write((short)(2 * channels));     // bytes a frame
        _writer.Write((short)16);
        _writer.Write("data"u8);
        _writer.Write(0);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Frames written (samples per channel).</summary>
    public long SamplesWritten => _samples;

    private readonly byte[] _buffer = new byte[1 << 16];

    /// <summary>Mono samples (in a stereo file, the same on both sides).</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (Channels == 2)
        {
            Write(samples, 1f, 1f);
            return;
        }
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

    /// <summary>Mono samples placed in a stereo file: <paramref name="left"/> and <paramref name="right"/> are the gains each side.</summary>
    public void Write(ReadOnlySpan<float> samples, float left, float right)
    {
        if (Channels != 2) throw new InvalidOperationException("Not a stereo file.");
        _writer.Flush();
        while (samples.Length > 0)
        {
            int n = Math.Min(samples.Length, _buffer.Length / 4);
            for (int i = 0; i < n; i++)
            {
                Put(4 * i, samples[i] * left);
                Put(4 * i + 2, samples[i] * right);
            }
            _stream.Write(_buffer, 0, n * 4);
            _samples += n;
            samples = samples[n..];
        }
    }

    /// <summary>Two channels (the same length).</summary>
    public void WriteStereo(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (Channels != 2) throw new InvalidOperationException("Not a stereo file.");
        _writer.Flush();
        while (left.Length > 0)
        {
            int n = Math.Min(left.Length, _buffer.Length / 4);
            for (int i = 0; i < n; i++)
            {
                Put(4 * i, left[i]);
                Put(4 * i + 2, right[i]);
            }
            _stream.Write(_buffer, 0, n * 4);
            _samples += n;
            left = left[n..];
            right = right[n..];
        }
    }

    private void Put(int at, float value)
    {
        short v = (short)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        _buffer[at] = (byte)v;
        _buffer[at + 1] = (byte)(v >> 8);
    }

    /// <summary>Silent frames.</summary>
    public void WriteSilence(long count)
    {
        _writer.Flush();
        Array.Clear(_buffer);
        int frame = 2 * Channels;
        while (count > 0)
        {
            int n = (int)Math.Min(count, _buffer.Length / frame);
            _stream.Write(_buffer, 0, n * frame);
            _samples += n;
            count -= n;
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _writer.Flush();
        long data = _samples * 2 * Channels;
        // WAV sizes are 32-bit: a track over about 12 hours at 24 kHz mono, or 3.3 hours at 44.1 kHz stereo, can't be described.
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

/// <summary>Reads stretches of a 16-bit PCM WAV (mono or stereo) without loading it all: the separated voices of an episode.</summary>
public sealed class WavReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataStart;
    private readonly byte[] _buffer = new byte[1 << 16];

    public WavReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        int bits = 0;
        while (_stream.Position + 8 <= _stream.Length)
        {
            var id = Encoding.ASCII.GetString(r.ReadBytes(4));
            uint size = r.ReadUInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                Channels = r.ReadInt16();
                SampleRate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                _stream.Position += size - 16;
            }
            else if (id == "data")
            {
                if (bits != 16 || Channels is not (1 or 2)) throw new InvalidDataException("Only 16-bit mono or stereo WAV is read here.");
                _dataStart = _stream.Position;
                // A file still being written, or over 4 GB, may say less than it holds: the file's length decides.
                Frames = (_stream.Length - _dataStart) / (2 * Channels);
                return;
            }
            else _stream.Position += size + (size & 1);
        }
        throw new InvalidDataException("The WAV file has no audio.");
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long Frames { get; }

    /// <summary>Frames from <paramref name="start"/> into the two outputs (mono: the same in both); past the end, silence.</summary>
    public void Read(long start, Span<float> left, Span<float> right)
    {
        int count = left.Length, frame = 2 * Channels, done = 0;
        left.Clear();
        right.Clear();
        if (start < 0)
        {
            done = (int)Math.Min(count, -start);
            start = 0;
        }
        _stream.Position = _dataStart + start * frame;
        while (done < count && start < Frames)
        {
            int want = (int)Math.Min(Math.Min(count - done, _buffer.Length / frame), Frames - start);
            int got = 0;
            while (got < want * frame)
            {
                int n = _stream.Read(_buffer, got, want * frame - got);
                if (n == 0) break;
                got += n;
            }
            int frames = got / frame;
            if (frames == 0) break;
            for (int i = 0; i < frames; i++)
            {
                float l = (short)(_buffer[i * frame] | _buffer[i * frame + 1] << 8) / 32768f;
                left[done + i] = l;
                right[done + i] = Channels == 2 ? (short)(_buffer[i * frame + 2] | _buffer[i * frame + 3] << 8) / 32768f : l;
            }
            done += frames;
            start += frames;
        }
    }

    public void Dispose() => _stream.Dispose();
}
