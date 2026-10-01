using System.Text;

namespace SubtitleStudio.Services.Translation;

/// <summary>
/// What a GGUF model file (llama.cpp's format) says about itself: its architecture, name, size and
/// whether it carries a chat template (needed to talk to it). Read from the header, without loading it.
/// </summary>
public sealed record GgufInfo(string Architecture, string? Name, string? SizeLabel, long? ContextLength, bool HasChatTemplate, int Version, int? BlockCount = null)
{
    private const uint Magic = 0x46554747; // "GGUF"

    public string Title => !string.IsNullOrWhiteSpace(Name) ? Name! : Architecture;

    /// <summary>Reads and checks the header; throws <see cref="InvalidDataException"/> with a plain message.</summary>
    public static GgufInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        try
        {
            if (stream.Length < 24 || reader.ReadUInt32() != Magic)
                throw new InvalidDataException("This is not a GGUF model file (the format llama.cpp uses). Download one from the list, or a .gguf file from Hugging Face.");
            int version = (int)reader.ReadUInt32();
            if (version is < 2 or > 3) throw new InvalidDataException($"This GGUF file is version {version}, which this app can't read.");
            reader.ReadUInt64(); // tensors
            ulong kvCount = reader.ReadUInt64();
            if (kvCount > 100_000) throw new InvalidDataException("This GGUF file's header is damaged.");

            string? arch = null, name = null, size = null;
            long? context = null, blocks = null;
            bool template = false;
            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                if (key == "general.architecture" && type == 8) arch = ReadString(reader);
                else if (key == "general.name" && type == 8) name = ReadString(reader);
                else if (key == "general.size_label" && type == 8) size = ReadString(reader);
                else if (key == "tokenizer.chat_template" && type == 8) { template = ReadString(reader).Length > 0; }
                else if (arch is not null && key == arch + ".context_length") context = ReadInteger(reader, type);
                else if (arch is not null && key == arch + ".block_count") blocks = ReadInteger(reader, type);
                else Skip(reader, type);
            }
            if (arch is null) throw new InvalidDataException("This GGUF file doesn't say what kind of model it is.");
            return new GgufInfo(arch, name, size, context, template, version, blocks is > 0 and < 10_000 ? (int)blocks : null);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("This GGUF file is cut short (an unfinished download?).");
        }
    }

    private static string ReadString(BinaryReader r)
    {
        ulong length = r.ReadUInt64();
        if (length > 64 << 20) throw new InvalidDataException("This GGUF file's header is damaged.");
        return Encoding.UTF8.GetString(r.ReadBytes((int)length));
    }

    private static long? ReadInteger(BinaryReader r, uint type) => type switch
    {
        0 => r.ReadByte(), 1 => r.ReadSByte(), 2 => r.ReadUInt16(), 3 => r.ReadInt16(),
        4 => r.ReadUInt32(), 5 => r.ReadInt32(), 10 => (long)r.ReadUInt64(), 11 => r.ReadInt64(),
        _ => SkipAndNull(r, type),
    };

    private static long? SkipAndNull(BinaryReader r, uint type)
    {
        Skip(r, type);
        return null;
    }

    private static void Skip(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2: case 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4: case 5: case 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10: case 11: case 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8:
                ulong length = r.ReadUInt64();
                if (length > int.MaxValue) throw new InvalidDataException("This GGUF file's header is damaged.");
                r.BaseStream.Seek((long)length, SeekOrigin.Current);
                break;
            case 9:
                uint elementType = r.ReadUInt32();
                ulong count = r.ReadUInt64();
                if (count > 100_000_000) throw new InvalidDataException("This GGUF file's header is damaged.");
                int fixedSize = elementType switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
                if (fixedSize > 0) r.BaseStream.Seek((long)count * fixedSize, SeekOrigin.Current);
                else for (ulong k = 0; k < count; k++) Skip(r, elementType);
                break;
            default:
                throw new InvalidDataException($"This GGUF file has a value type ({type}) this app doesn't know.");
        }
    }
}
