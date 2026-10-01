using System.Globalization;
using System.Text;

namespace SubtitleStudio.Services;

public enum LogLevel
{
    /// <summary>Technical detail: ffmpeg command lines, per-step timings. Hidden in the Log tab unless asked for.</summary>
    Detail,
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Area, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string LevelText => Level switch
    {
        LogLevel.Detail => "detail",
        LogLevel.Success => "ok",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => "info",
    };

    public override string ToString()
        => $"{Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {LevelText,-7}  {Area,-10}  {Message}";
}

/// <summary>
/// What the app did, for the Log tab and subt_activity.log next to the EXE (so a run can be shared).
/// Thread-safe; entries can come from any thread. The file is capped: when it passes the limit it is
/// renamed to subt_activity.old.log (replacing the previous one) and a new file starts.
/// </summary>
public sealed class ActivityLog
{
    public const int MemoryLimit = 5000;

    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private readonly string? _file;
    private readonly long _maxFileBytes;

    public ActivityLog(string? filePath, long maxFileBytes = 2L << 20)
    {
        _file = filePath;
        _maxFileBytes = maxFileBytes;
    }

    public string? FilePath => _file;

    /// <summary>Raised for every entry, on the thread that wrote it.</summary>
    public event Action<LogEntry>? Added;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Detail(string area, string message) => Write(LogLevel.Detail, area, message);
    public void Info(string area, string message) => Write(LogLevel.Info, area, message);
    public void Success(string area, string message) => Write(LogLevel.Success, area, message);
    public void Warning(string area, string message) => Write(LogLevel.Warning, area, message);
    public void Error(string area, string message) => Write(LogLevel.Error, area, message);

    public void Write(LogLevel level, string area, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, area, message.TrimEnd());
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MemoryLimit) _entries.RemoveRange(0, _entries.Count - MemoryLimit);
            AppendToFile(entry);
        }
        Added?.Invoke(entry);
    }

    private void AppendToFile(LogEntry entry)
    {
        if (_file is null) return;
        try
        {
            var info = new FileInfo(_file);
            if (info.Exists && info.Length > _maxFileBytes)
                File.Move(_file, Path.ChangeExtension(_file, ".old.log"), overwrite: true);
            File.AppendAllText(_file, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log must never break the app (read-only folder, file open elsewhere...).
        }
    }

    /// <summary>All entries (optionally without details) as plain text, oldest first.</summary>
    public string ToText(bool includeDetails)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot())
            if (includeDetails || e.Level != LogLevel.Detail) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    /// <summary>A command line as it could be pasted into a terminal (arguments with spaces quoted).</summary>
    public static string CommandLine(string exe, IEnumerable<string> args)
        => string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Length == 0 ? "\"\"" : a.IndexOfAny(new[] { ' ', '\t', '"', ';', '\'' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}
