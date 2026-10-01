using System.Text;
using System.Text.Json;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services;

/// <summary>
/// Loads and auto-saves subt_settings.json next to the EXE.
/// Saves are debounced (ScheduleSave) and written atomically (temp file + replace).
/// The JSON snapshot is taken on the caller's thread, so the timer never touches live objects.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ValidFormats = { "srt", "vtt", "ass", "ssa", "sub" };

    private readonly object _writeGate = new();
    private readonly object _pendingGate = new();
    private readonly Timer _timer;
    private string? _pendingJson;
    private bool _disposed;

    public SettingsService(string filePath)
    {
        FilePath = filePath;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath { get; }

    /// <summary>Set when the file existed but could not be read; shown once in the status bar.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Last save error, or null if the last save succeeded.</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>Raised on a background thread when a save fails (e.g. read-only folder).</summary>
    public event Action<string>? SaveFailed;

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new AppSettings();
            SaveNow(fresh);
            return fresh;
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadWarning = $"Could not read {AppPaths.SettingsFileName} ({ex.Message}). Using defaults for this session.";
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for the user, then start clean.
            var backup = FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
                LoadWarning = $"{AppPaths.SettingsFileName} was damaged and has been reset. The old file was kept as {Path.GetFileName(backup)}.";
            }
            catch (Exception)
            {
                LoadWarning = $"{AppPaths.SettingsFileName} was damaged and has been reset to defaults.";
            }

            var fresh = new AppSettings();
            SaveNow(fresh);
            return fresh;
        }
    }

    /// <summary>Debounced save (~400 ms after the last change).</summary>
    public void ScheduleSave(AppSettings settings)
    {
        if (_disposed) return;
        var json = Serialize(settings);
        lock (_pendingGate) _pendingJson = json;
        _timer.Change(400, Timeout.Infinite);
    }

    /// <summary>Immediate synchronous save (startup defaults, shutdown).</summary>
    public void SaveNow(AppSettings settings)
    {
        var json = Serialize(settings);
        lock (_pendingGate) _pendingJson = json;
        Flush();
    }

    public void Flush()
    {
        lock (_writeGate)
        {
            string? json;
            lock (_pendingGate)
            {
                json = _pendingJson;
                _pendingJson = null;
            }
            if (json is null) return;

            var tempPath = FilePath + ".tmp";
            Exception? last = null;

            // A few quick retries: antivirus / indexers occasionally hold the file for a moment.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    File.Move(tempPath, FilePath, overwrite: true);
                    LastSaveError = null;
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(60);
                }
            }

            LastSaveError = last is UnauthorizedAccessException
                ? $"Settings could not be saved: the folder {AppPaths.ExeDirectory} is not writable. Move Subtitle Studio to a folder you can write to."
                : $"Settings could not be saved: {last?.Message}";
            SaveFailed?.Invoke(LastSaveError);
        }
    }

    private static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    private static void Normalize(AppSettings s)
    {
        s.ApiKeys ??= new Dictionary<string, string>();
        s.FfmpegPath ??= string.Empty;
        s.WhisperModel ??= string.Empty;
        s.WhisperDevice = string.IsNullOrWhiteSpace(s.WhisperDevice) ? "auto" : s.WhisperDevice;
        s.TranscribeLanguage = string.IsNullOrWhiteSpace(s.TranscribeLanguage) ? "auto" : s.TranscribeLanguage;
        s.TranscriptionEngine ??= string.Empty;

        if (s.Theme is not ("Dark" or "Light")) s.Theme = "Dark";

        s.DefaultOutputFormat = (s.DefaultOutputFormat ?? "srt").Trim().ToLowerInvariant();
        if (!ValidFormats.Contains(s.DefaultOutputFormat)) s.DefaultOutputFormat = "srt";

        s.DefaultLanguage = string.IsNullOrWhiteSpace(s.DefaultLanguage) ? "en" : s.DefaultLanguage.Trim().ToLowerInvariant();

        // Keys are stored under lower-case provider ids.
        s.ApiKeys = s.ApiKeys
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .GroupBy(kv => kv.Key.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Flush();
    }
}
