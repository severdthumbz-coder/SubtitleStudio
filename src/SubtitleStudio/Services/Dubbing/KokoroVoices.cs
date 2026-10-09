using System.Globalization;
using System.IO.Compression;
using System.Text;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>One of Kokoro's English voices: who it sounds like, for the voice list.</summary>
public sealed record KokoroVoiceInfo(string Id, string Name, string Gender, string Accent, bool Recommended, string Note)
{
    public string Label => $"{Name}  ·  {Gender}, {Accent}" + (Recommended ? "  ·  recommended" : string.Empty);

    public TtsVoice ToTtsVoice() => new(Id, Label, Accent == "British" ? "en-GB" : "en-US", Gender == "female" ? "female" : "male");

    public override string ToString() => Label;
}

/// <summary>
/// Kokoro's voices file (voices-v1.0.bin): a NumPy .npz, that is a zip of one .npy array per voice, each
/// 510 x 1 x 256 floats (one style vector for each possible length of the input). Read inside the app.
/// </summary>
public sealed class KokoroVoices
{
    public const int Rows = 510;
    public const int StyleSize = 256;

    private readonly Dictionary<string, float[]> _voices;

    private KokoroVoices(Dictionary<string, float[]> voices) => _voices = voices;

    public IReadOnlyCollection<string> Names => _voices.Keys;

    public bool Contains(string id) => _voices.ContainsKey(id);

    /// <summary>The style vector for an input of <paramref name="tokens"/> phonemes (row tokens - 1, as Kokoro does).</summary>
    public float[] Style(string id, int tokens)
    {
        if (!_voices.TryGetValue(id, out var data)) throw new InvalidOperationException($"The voices file has no voice \"{id}\".");
        int row = Math.Clamp(tokens, 1, Rows) - 1;
        var style = new float[StyleSize];
        Array.Copy(data, row * StyleSize, style, 0, StyleSize);
        return style;
    }

    public static KokoroVoices Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static KokoroVoices Load(Stream npz)
    {
        var voices = new Dictionary<string, float[]>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(npz, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".npy", StringComparison.Ordinal)) continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                voices[entry.Name[..^4]] = ReadNpy(ms.ToArray(), entry.Name);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The voices file isn't a Kokoro voices file (" + ex.Message + ").", ex);
        }
        if (voices.Count == 0) throw new InvalidDataException("The voices file has no voices in it.");
        return new KokoroVoices(voices);
    }

    /// <summary>One .npy array: the magic, a header (dtype, order, shape), then little-endian float32 data.</summary>
    internal static float[] ReadNpy(byte[] bytes, string name)
    {
        if (bytes.Length < 10 || bytes[0] != 0x93 || Encoding.ASCII.GetString(bytes, 1, 5) != "NUMPY")
            throw new InvalidDataException($"{name} isn't a NumPy array.");
        int major = bytes[6];
        int headerLength, offset;
        if (major == 1) { headerLength = BitConverter.ToUInt16(bytes, 8); offset = 10; }
        else { headerLength = (int)BitConverter.ToUInt32(bytes, 8); offset = 12; }
        var header = Encoding.ASCII.GetString(bytes, offset, headerLength);
        if (!header.Contains("'<f4'") || header.Contains("'fortran_order': True"))
            throw new InvalidDataException($"{name} isn't a float32 array in C order.");
        int open = header.IndexOf('(', header.IndexOf("shape", StringComparison.Ordinal)), close = header.IndexOf(')', open);
        long count = header[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Aggregate(1L, (a, d) => a * long.Parse(d, CultureInfo.InvariantCulture));
        int start = offset + headerLength;
        if (count != Rows * StyleSize || bytes.Length - start < count * 4)
            throw new InvalidDataException($"{name} has {count} values; a Kokoro voice has {Rows * StyleSize}.");
        var data = new float[count];
        Buffer.BlockCopy(bytes, start, data, 0, (int)count * 4);
        if (!BitConverter.IsLittleEndian)
            for (int i = 0; i < data.Length; i++) data[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(data[i])));
        return data;
    }

    /// <summary>
    /// The English voices, best first (quality as graded by Kokoro's author: the recommended ones are the
    /// clearest). Female and male, American and British.
    /// </summary>
    public static IReadOnlyList<KokoroVoiceInfo> English { get; } = new[]
    {
        new KokoroVoiceInfo("af_heart", "Heart", "female", "American", true, "Warm and natural; the best voice"),
        new KokoroVoiceInfo("af_bella", "Bella", "female", "American", true, "Lively, expressive"),
        new KokoroVoiceInfo("am_michael", "Michael", "male", "American", true, "Calm, clear"),
        new KokoroVoiceInfo("am_fenrir", "Fenrir", "male", "American", true, "Deeper, firm"),
        new KokoroVoiceInfo("am_puck", "Puck", "male", "American", true, "Younger, bright"),
        new KokoroVoiceInfo("bf_emma", "Emma", "female", "British", true, "Clear British"),
        new KokoroVoiceInfo("bm_george", "George", "male", "British", true, "Clear British"),
        new KokoroVoiceInfo("af_nicole", "Nicole", "female", "American", false, "Soft, close to a whisper"),
        new KokoroVoiceInfo("af_aoede", "Aoede", "female", "American", false, "Light"),
        new KokoroVoiceInfo("af_kore", "Kore", "female", "American", false, "Even"),
        new KokoroVoiceInfo("af_sarah", "Sarah", "female", "American", false, "Plain"),
        new KokoroVoiceInfo("af_alloy", "Alloy", "female", "American", false, "Neutral"),
        new KokoroVoiceInfo("af_nova", "Nova", "female", "American", false, "Bright"),
        new KokoroVoiceInfo("af_sky", "Sky", "female", "American", false, "Young"),
        new KokoroVoiceInfo("af_jessica", "Jessica", "female", "American", false, "Lower quality"),
        new KokoroVoiceInfo("af_river", "River", "female", "American", false, "Lower quality"),
        new KokoroVoiceInfo("bf_isabella", "Isabella", "female", "British", false, "Soft British"),
        new KokoroVoiceInfo("bf_alice", "Alice", "female", "British", false, "Lower quality"),
        new KokoroVoiceInfo("bf_lily", "Lily", "female", "British", false, "Lower quality"),
        new KokoroVoiceInfo("bm_fable", "Fable", "male", "British", false, "Storyteller"),
        new KokoroVoiceInfo("bm_lewis", "Lewis", "male", "British", false, "Lower quality"),
        new KokoroVoiceInfo("bm_daniel", "Daniel", "male", "British", false, "Lower quality"),
        new KokoroVoiceInfo("am_echo", "Echo", "male", "American", false, "Lower quality"),
        new KokoroVoiceInfo("am_eric", "Eric", "male", "American", false, "Lower quality"),
        new KokoroVoiceInfo("am_liam", "Liam", "male", "American", false, "Lower quality"),
        new KokoroVoiceInfo("am_onyx", "Onyx", "male", "American", false, "Lower quality, deep"),
        new KokoroVoiceInfo("am_adam", "Adam", "male", "American", false, "Lowest quality"),
    };

    public static KokoroVoiceInfo? Find(string? id) => English.FirstOrDefault(v => v.Id == id);
}
