using System.Reflection;

namespace SubtitleStudio.Services.Transcription;

/// <summary>A stretch of speech found by the speech detector.</summary>
public readonly record struct SpeechRegion(TimeSpan Start, TimeSpan End);

/// <summary>
/// The Silero speech detector (VAD), built into the EXE (885 KB, MIT; whisper.cpp's ggml conversion).
/// Unlike a loudness check it tells speech from music and effects, so Whisper isn't handed the opening
/// music, where it invents captions it learned from the internet ("see you in the next video").
/// Written out once to models\vad next to the app, where whisper.cpp can load it.
/// </summary>
public static class SileroVad
{
    public const string FileName = "ggml-silero-v6.2.0.bin";
    public const string ResourceName = "SubtitleStudio." + FileName;
    public const long Size = 885_098;

    /// <summary>The model file in <paramref name="folder"/>, written from the EXE if missing. Null if it can't be.</summary>
    public static string? EnsureModel(string folder, Action<string>? log = null)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == Size) return path;
            using var resource = typeof(SileroVad).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                log?.Invoke("The speech detector isn't built into this copy of the app; silences are found by loudness instead.");
                return null;
            }
            Directory.CreateDirectory(folder);
            var tmp = path + ".tmp";
            using (var output = File.Create(tmp)) resource.CopyTo(output);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"The speech detector couldn't be written to {folder} ({ex.Message}); silences are found by loudness instead.");
            return null;
        }
    }
}
