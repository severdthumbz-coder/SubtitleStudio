using SubtitleStudio.Services.Video;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// Windows' built-in OCR (Windows.Media.Ocr): offline, free, no install. Uses the OCR languages
/// that come with the Windows language packs on this PC (Settings > Time &amp; language > Language).
/// </summary>
public sealed class WindowsOcrService : ITextRecognizer
{
    private OcrEngine? _engine;

    public WindowsOcrService()
    {
        try
        {
            AvailableLanguages = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null && AvailableLanguages.Count > 0)
                _engine = OcrEngine.TryCreateFromLanguage(new Language(AvailableLanguages[0]));
            if (_engine is null)
                UnavailableReason = "No OCR language is installed. Add a language with its optical character recognition feature in Windows Settings > Time & language > Language & region.";
        }
        catch (Exception ex)
        {
            AvailableLanguages = Array.Empty<string>();
            UnavailableReason = $"Windows OCR is not available on this PC: {ex.Message}";
        }
    }

    public bool IsAvailable => _engine is not null;
    public string? UnavailableReason { get; private set; }
    public string? LanguageTag => _engine?.RecognizerLanguage.LanguageTag;
    public IReadOnlyList<string> AvailableLanguages { get; }

    public int MaxImageDimension
    {
        get
        {
            try { return (int)Math.Min(OcrEngine.MaxImageDimension, int.MaxValue); }
            catch (Exception) { return 2600; }
        }
    }

    public bool SetLanguage(string languageTag)
    {
        try
        {
            var language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language)) return false;
            var engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is null) return false;
            _engine = engine;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(FrameSample frame, CancellationToken ct)
    {
        var engine = _engine ?? throw new InvalidOperationException(UnavailableReason ?? "OCR is not available.");

        // Windows OCR refuses images larger than MaxImageDimension; BurnedInService sizes frames to
        // stay under it, so this only guards against misuse.
        if (frame.Width > OcrEngine.MaxImageDimension || frame.Height > OcrEngine.MaxImageDimension)
            throw new ArgumentException($"Frame is larger than the OCR limit of {OcrEngine.MaxImageDimension} px.");

        var buffer = CryptographicBuffer.CreateFromByteArray(frame.Bgra);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                left = Math.Min(left, r.X);
                top = Math.Min(top, r.Y);
                right = Math.Max(right, r.X + r.Width);
                bottom = Math.Max(bottom, r.Y + r.Height);
            }
            lines.Add(new OcrLine(line.Text, left, top, right - left, bottom - top));
        }
        return lines;
    }
}
