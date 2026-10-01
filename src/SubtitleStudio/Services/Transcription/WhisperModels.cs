using System.Globalization;
using System.Text.RegularExpressions;

namespace SubtitleStudio.Services.Transcription;

public enum WhisperSize { Tiny, Base, Small, Medium, Large, Unknown }

/// <summary>
/// The start of a whisper.cpp model file (ggml format): a magic number, then 11 numbers describing
/// the network. Enough to tell what a file is (size, version, languages, compression) before loading it.
/// </summary>
public sealed record WhisperModelHeader(int Vocab, int AudioContext, int AudioState, int AudioHeads, int AudioLayers,
    int TextContext, int TextState, int TextHeads, int TextLayers, int Mels, int FileType)
{
    public const uint Magic = 0x67676d6c; // "ggml"
    public const int ByteLength = 48;

    public WhisperSize Size => AudioLayers switch
    {
        4 => WhisperSize.Tiny,
        6 => WhisperSize.Base,
        12 => WhisperSize.Small,
        24 => WhisperSize.Medium,
        32 => WhisperSize.Large,
        _ => WhisperSize.Unknown,
    };

    /// <summary>English-only models (".en") have a smaller vocabulary.</summary>
    public bool Multilingual => Vocab >= 51865;

    /// <summary>Large v3 and v3 Turbo: 128 mel bands and one more language token (Cantonese).</summary>
    public bool IsV3 => Vocab == 51866;

    /// <summary>Large v3 Turbo: the large encoder with a 4-layer decoder (fast, but not trained to translate).</summary>
    public bool IsTurbo => AudioLayers == 32 && TextLayers == 4;

    /// <summary>ggml keeps the quantisation version in the thousands.</summary>
    public int WeightType => FileType % 1000;

    public bool Compressed => WeightType >= 2;

    /// <summary>Only multilingual models can translate, and Turbo does it poorly (it was trained without translation).</summary>
    public bool TranslatesWell => Multilingual && !IsTurbo;

    public string Name
    {
        get
        {
            string name = Size switch
            {
                WhisperSize.Large when IsTurbo => "Large v3 Turbo",
                WhisperSize.Large when IsV3 => "Large v3",
                WhisperSize.Large => "Large (v1/v2)",
                WhisperSize.Unknown => $"Whisper ({AudioLayers} layers)",
                var s => s.ToString(),
            };
            if (!Multilingual) name += " (English only)";
            if (Compressed) name += $" ({WeightTypeName}, compressed)";
            return name;
        }
    }

    public string WeightTypeName => WeightType switch
    {
        0 => "f32",
        1 => "f16",
        2 => "q4_0",
        3 => "q4_1",
        7 => "q8_0",
        8 => "q5_0",
        9 => "q5_1",
        _ => $"type {WeightType}",
    };

    /// <summary>Reads and checks the header; throws <see cref="InvalidDataException"/> with a plain message.</summary>
    public static WhisperModelHeader Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static WhisperModelHeader Read(Stream stream)
    {
        var buffer = new byte[ByteLength];
        int read = 0, n;
        while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
        if (read < ByteLength || BitConverter.ToUInt32(buffer, 0) != Magic)
            throw new InvalidDataException("This is not a whisper.cpp model file (ggml-*.bin). Download one from the whisper.cpp model page on Hugging Face.");

        int I(int i) => BitConverter.ToInt32(buffer, 4 + 4 * i);
        var header = new WhisperModelHeader(I(0), I(1), I(2), I(3), I(4), I(5), I(6), I(7), I(8), I(9), I(10));
        bool sane = header.Vocab is > 50000 and < 52000
                    && header.AudioLayers is > 0 and <= 64 && header.TextLayers is > 0 and <= 64
                    && header.Mels is 80 or 128 && header.AudioState == header.TextState && header.AudioState > 0;
        if (!sane)
            throw new InvalidDataException("This ggml file is not a Whisper speech model (its settings don't match Whisper's).");
        return header;
    }
}

/// <summary>A model the app offers to download (whisper.cpp's own conversions, on Hugging Face).</summary>
public sealed record WhisperCatalogEntry(string FileName, string Title, string SizeText, string Description, bool Recommended = false)
{
    public const string Repository = "https://huggingface.co/ggerganov/whisper.cpp";

    public string DownloadUrl => $"{Repository}/resolve/main/{FileName}";

    /// <summary>The Git LFS pointer: the file's exact size and SHA-256, checked after downloading.</summary>
    public string PointerUrl => $"{Repository}/raw/main/{FileName}";

    public string Label => $"{Title} ({SizeText})" + (Recommended ? " - recommended" : string.Empty);
}

public static class WhisperCatalog
{
    public static IReadOnlyList<WhisperCatalogEntry> Entries { get; } = new[]
    {
        new WhisperCatalogEntry("ggml-large-v3-turbo.bin", "Large v3 Turbo", "1.6 GB",
            "The best choice for writing out what is said, in the language spoken. Nearly as accurate as Large v3 and several times faster. Not for translating to English.", Recommended: true),
        new WhisperCatalogEntry("ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo, compressed", "570 MB",
            "The same model compressed to about a third of the size and memory, with nearly the same accuracy. Not for translating to English."),
        new WhisperCatalogEntry("ggml-large-v3.bin", "Large v3", "3.1 GB",
            "The most accurate model, and the one to use for turning speech straight into English subtitles. The slowest; needs about 4 GB of graphics memory."),
        new WhisperCatalogEntry("ggml-large-v3-q5_0.bin", "Large v3, compressed", "1.1 GB",
            "Large v3 compressed: good for translating to English with much less memory, slightly less accurate."),
        new WhisperCatalogEntry("ggml-medium.bin", "Medium", "1.5 GB",
            "Older and less accurate than Large v3 Turbo, but it can translate to English. A middle ground for slower graphics cards."),
        new WhisperCatalogEntry("ggml-small.bin", "Small", "490 MB",
            "Fast and light, noticeably less accurate. For quick drafts or PCs without a graphics card."),
        new WhisperCatalogEntry("ggml-base.bin", "Base", "150 MB",
            "Very fast and rough. Mostly for trying the setup."),
    };

    public static WhisperCatalogEntry? Find(string fileName)
        => Entries.FirstOrDefault(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A Git LFS pointer file ("oid sha256:..." and "size ...").</summary>
public sealed record LfsPointer(string Sha256, long Size)
{
    private static readonly Regex Oid = new(@"(?m)^oid sha256:([0-9a-f]{64})\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex SizeLine = new(@"(?m)^size (\d+)\s*$", RegexOptions.CultureInvariant);

    public static LfsPointer? TryParse(string text)
    {
        var oid = Oid.Match(text);
        var size = SizeLine.Match(text);
        if (!oid.Success || !size.Success) return null;
        if (!long.TryParse(size.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0) return null;
        return new LfsPointer(oid.Groups[1].Value, bytes);
    }
}

/// <summary>Languages Whisper knows (codes as whisper.cpp uses them), from OpenAI's Whisper tokenizer.</summary>
public static class WhisperLanguages
{
    public sealed record Language(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    public static IReadOnlyList<Language> All { get; } = new (string Code, string Name)[]
    {
        ("en", "English"),
        ("zh", "Chinese"),
        ("de", "German"),
        ("es", "Spanish"),
        ("ru", "Russian"),
        ("ko", "Korean"),
        ("fr", "French"),
        ("ja", "Japanese"),
        ("pt", "Portuguese"),
        ("tr", "Turkish"),
        ("pl", "Polish"),
        ("ca", "Catalan"),
        ("nl", "Dutch"),
        ("ar", "Arabic"),
        ("sv", "Swedish"),
        ("it", "Italian"),
        ("id", "Indonesian"),
        ("hi", "Hindi"),
        ("fi", "Finnish"),
        ("vi", "Vietnamese"),
        ("he", "Hebrew"),
        ("uk", "Ukrainian"),
        ("el", "Greek"),
        ("ms", "Malay"),
        ("cs", "Czech"),
        ("ro", "Romanian"),
        ("da", "Danish"),
        ("hu", "Hungarian"),
        ("ta", "Tamil"),
        ("no", "Norwegian"),
        ("th", "Thai"),
        ("ur", "Urdu"),
        ("hr", "Croatian"),
        ("bg", "Bulgarian"),
        ("lt", "Lithuanian"),
        ("la", "Latin"),
        ("mi", "Maori"),
        ("ml", "Malayalam"),
        ("cy", "Welsh"),
        ("sk", "Slovak"),
        ("te", "Telugu"),
        ("fa", "Persian"),
        ("lv", "Latvian"),
        ("bn", "Bengali"),
        ("sr", "Serbian"),
        ("az", "Azerbaijani"),
        ("sl", "Slovenian"),
        ("kn", "Kannada"),
        ("et", "Estonian"),
        ("mk", "Macedonian"),
        ("br", "Breton"),
        ("eu", "Basque"),
        ("is", "Icelandic"),
        ("hy", "Armenian"),
        ("ne", "Nepali"),
        ("mn", "Mongolian"),
        ("bs", "Bosnian"),
        ("kk", "Kazakh"),
        ("sq", "Albanian"),
        ("sw", "Swahili"),
        ("gl", "Galician"),
        ("mr", "Marathi"),
        ("pa", "Punjabi"),
        ("si", "Sinhala"),
        ("km", "Khmer"),
        ("sn", "Shona"),
        ("yo", "Yoruba"),
        ("so", "Somali"),
        ("af", "Afrikaans"),
        ("oc", "Occitan"),
        ("ka", "Georgian"),
        ("be", "Belarusian"),
        ("tg", "Tajik"),
        ("sd", "Sindhi"),
        ("gu", "Gujarati"),
        ("am", "Amharic"),
        ("yi", "Yiddish"),
        ("lo", "Lao"),
        ("uz", "Uzbek"),
        ("fo", "Faroese"),
        ("ht", "Haitian Creole"),
        ("ps", "Pashto"),
        ("tk", "Turkmen"),
        ("nn", "Nynorsk"),
        ("mt", "Maltese"),
        ("sa", "Sanskrit"),
        ("lb", "Luxembourgish"),
        ("my", "Myanmar"),
        ("bo", "Tibetan"),
        ("tl", "Tagalog"),
        ("mg", "Malagasy"),
        ("as", "Assamese"),
        ("tt", "Tatar"),
        ("haw", "Hawaiian"),
        ("ln", "Lingala"),
        ("ha", "Hausa"),
        ("ba", "Bashkir"),
        ("jw", "Javanese"),
        ("su", "Sundanese"),
        ("yue", "Cantonese")
    }.Select(l => new Language(l.Code, l.Name)).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static string NameOf(string? code)
        => code is null ? "unknown" : All.FirstOrDefault(l => l.Code == code)?.Name ?? code;
}
