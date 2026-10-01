using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>A recognised line of text, with its box in pixels of the frame that was passed in.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double Bottom => Y + Height;
}

/// <summary>Text recognition on video frames. Windows OCR in the app; a fake in tests.</summary>
public interface ITextRecognizer
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    /// <summary>Language used for recognition, e.g. "en-US".</summary>
    string? LanguageTag { get; }

    /// <summary>OCR languages installed on this PC.</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>Largest image width or height the engine accepts.</summary>
    int MaxImageDimension { get; }

    /// <summary>Switch language; false if that language isn't installed.</summary>
    bool SetLanguage(string languageTag);

    Task<IReadOnlyList<OcrLine>> RecognizeAsync(FrameSample frame, CancellationToken ct);
}
