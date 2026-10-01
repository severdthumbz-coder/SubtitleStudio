using System.Text.Json;
using System.Text.Json.Serialization;
using SubtitleStudio.Models;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services;

/// <summary>A long job that was running when the session was saved (restarted after a crash if wanted).</summary>
public enum SessionJobKind { CreateVideo, Extract, Transcribe }

public sealed record SessionJob(SessionJobKind Kind, string VideoPath, string? OutputPath, DateTime StartedUtc);

/// <summary>One subtitle cue as saved in a session (times in ticks: exact).</summary>
public sealed record SessionCue(long Start, long End, string Text, Dictionary<string, string>? Extra);

/// <summary>The subtitle editor: the document cue by cue, whether it had unsaved changes, the video it belongs to.</summary>
public sealed class EditorSession
{
    public string? SourcePath { get; set; }
    public string? SourceEncoding { get; set; }
    public string Format { get; set; } = "srt";
    public string? Language { get; set; }
    public bool Forced { get; set; }
    public bool HearingImpaired { get; set; }
    public bool Sdh { get; set; }
    public string? FormatHeader { get; set; }
    public string? FormatTrailer { get; set; }
    public double? FrameRate { get; set; }
    public bool FrameRateAssumed { get; set; }
    public List<SessionCue> Cues { get; set; } = new();
    public bool Dirty { get; set; }
    public string? PairedVideoPath { get; set; }
    public int SelectedIndex { get; set; }
    public string? StandaloneBaseName { get; set; }

    public static EditorSession From(SubtitleDocument doc, bool dirty, string? pairedVideo, int selectedIndex, string? standaloneBaseName) => new()
    {
        SourcePath = doc.SourcePath,
        SourceEncoding = doc.SourceEncoding,
        Format = doc.Format,
        Language = doc.Language,
        Forced = doc.Forced,
        HearingImpaired = doc.HearingImpaired,
        Sdh = doc.Sdh,
        FormatHeader = doc.FormatHeader,
        FormatTrailer = doc.FormatTrailer,
        FrameRate = doc.FrameRate,
        FrameRateAssumed = doc.FrameRateAssumed,
        Cues = doc.Cues.Select(c => new SessionCue(c.Start.Ticks, c.End.Ticks, c.Text, c.Extra is null ? null : new Dictionary<string, string>(c.Extra))).ToList(),
        Dirty = dirty,
        PairedVideoPath = pairedVideo,
        SelectedIndex = selectedIndex,
        StandaloneBaseName = standaloneBaseName,
    };

    public SubtitleDocument ToDocument()
    {
        var doc = new SubtitleDocument
        {
            SourcePath = SourcePath,
            SourceEncoding = SourceEncoding,
            Format = Format,
            Language = Language,
            Forced = Forced,
            HearingImpaired = HearingImpaired,
            Sdh = Sdh,
            FormatHeader = FormatHeader,
            FormatTrailer = FormatTrailer,
            FrameRate = FrameRate,
            FrameRateAssumed = FrameRateAssumed,
        };
        int i = 1;
        foreach (var c in Cues)
            doc.Cues.Add(new SubtitleCue { Index = i++, Start = TimeSpan.FromTicks(c.Start), End = TimeSpan.FromTicks(c.End), Text = c.Text, Extra = c.Extra is null ? null : new Dictionary<string, string>(c.Extra) });
        return doc;
    }
}

/// <summary>The Burned-in tab: the video, what Detect found (so it needn't run again) and the settings.</summary>
public sealed class BurnedInSession
{
    public string VideoPath { get; set; } = string.Empty;
    public DetectionResult? Detection { get; set; }
    public VideoInfo? Video { get; set; }
    public double Scale { get; set; } = 1;
    public double BandTopPercent { get; set; }
    public double BandBottomPercent { get; set; }
    public bool CleanUp { get; set; } = true;
    public double ExtractFramesPerSecond { get; set; }
    public string? RemovalMethod { get; set; }
    public double FrameSeconds { get; set; }
}

/// <summary>Everything needed to put the app back as it was.</summary>
public sealed class SessionSnapshot
{
    public int Version { get; set; } = SessionStore.CurrentVersion;
    public DateTime SavedUtc { get; set; }
    public string? AppVersion { get; set; }
    public List<string> Files { get; set; } = new();
    public string? SelectedFile { get; set; }
    public int SelectedTab { get; set; }
    public BurnedInSession? BurnedIn { get; set; }
    public EditorSession? Editor { get; set; }
    public SessionJob? Job { get; set; }

    /// <summary>Anything worth offering to restore (an empty app isn't).</summary>
    [JsonIgnore]
    public bool HasContent => Files.Count > 0 || Editor is { Cues.Count: > 0 } || Job is not null;

    /// <summary>One line per item for the restore prompt.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        if (Files.Count > 0) lines.Add(Files.Count == 1 ? $"1 file: {Path.GetFileName(Files[0])}" : $"{Files.Count} files, e.g. {Path.GetFileName(Files[0])}");
        if (BurnedIn is { Detection: not null } b) lines.Add($"Burned-in subtitles found in {Path.GetFileName(b.VideoPath)} (no need to Detect again)");
        if (Editor is { Cues.Count: > 0 } e)
        {
            var name = e.SourcePath is not null ? Path.GetFileName(e.SourcePath) : e.StandaloneBaseName ?? "Untitled subtitle";
            lines.Add($"Subtitle {name}: {e.Cues.Count:N0} cues" + (e.Dirty ? ", with unsaved changes" : string.Empty));
        }
        return lines;
    }
}

/// <summary>
/// Crash recovery: the session is saved to subt_session.json next to the app while it runs and deleted
/// when the app closes normally. Finding it at start-up means the app (or the PC) stopped unexpectedly.
/// Written to a temporary file, flushed to disk and then swapped in, so a crash or power cut in the
/// middle of a save leaves the previous copy intact.
/// </summary>
public sealed class SessionStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "subt_session.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private string? _lastWritten;

    public SessionStore(string path) => FilePath = path;

    public string FilePath { get; }

    public static string Serialize(SessionSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);

    /// <summary>Saves the snapshot if it changed since the last save (the time stamp aside). True when written.</summary>
    public bool Save(SessionSnapshot snapshot)
    {
        var saved = snapshot.SavedUtc;
        snapshot.SavedUtc = default;
        var comparable = Serialize(snapshot);
        snapshot.SavedUtc = saved == default ? DateTime.UtcNow : saved;
        lock (_gate)
        {
            if (comparable == _lastWritten && File.Exists(FilePath)) return false;
            var json = Serialize(snapshot);
            var tmp = FilePath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, FilePath, overwrite: true);
            _lastWritten = comparable;
            return true;
        }
    }

    /// <summary>The session left by a run that didn't close normally, or null (none, unreadable or from a newer version).</summary>
    public SessionSnapshot? LoadLeftOver()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(FilePath), Json);
            return snapshot is { Version: > 0 and <= CurrentVersion } ? snapshot : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable: keep it aside for a look, don't offer it.
            try { File.Move(FilePath, Path.ChangeExtension(FilePath, ".unreadable.json"), overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>Normal close (or "Start fresh"): nothing to recover next time.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lastWritten = null;
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
