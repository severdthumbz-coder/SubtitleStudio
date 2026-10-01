using System.Globalization;
using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Models;

public enum MediaKind
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>One row in the Source / Files list.</summary>
public sealed class MediaItem : ObservableObject
{
    private TimeSpan? _duration;
    private string _durationText;
    private IReadOnlyList<SidecarInfo> _sidecars;

    public MediaItem(string fullPath, MediaKind kind, long sizeBytes, IReadOnlyList<SidecarInfo>? sidecars = null)
    {
        FullPath = fullPath;
        Kind = kind;
        SizeBytes = sizeBytes;
        Name = Path.GetFileName(fullPath);
        Folder = Path.GetDirectoryName(fullPath) ?? string.Empty;
        Extension = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant();
        _sidecars = sidecars ?? Array.Empty<SidecarInfo>();
        _durationText = kind == MediaKind.Subtitle ? "-" : "...";
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Folder { get; }
    public string Extension { get; }
    public MediaKind Kind { get; }
    public long SizeBytes { get; }

    public bool IsVideo => Kind == MediaKind.Video;
    public string KindText => Kind.ToString();

    public string KindGlyph => Kind switch
    {
        MediaKind.Video => "",
        MediaKind.Audio => "",
        _ => "",
    };

    public string SizeText => FormatSize(SizeBytes);

    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            if (!SetProperty(ref _duration, value)) return;
            DurationText = value is { } d ? FormatDuration(d) : "?";
            OnPropertyChanged(nameof(DurationSortKey));
        }
    }

    public double DurationSortKey => _duration?.TotalSeconds ?? -1;

    public string DurationText
    {
        get => _durationText;
        set => SetProperty(ref _durationText, value);
    }

    public IReadOnlyList<SidecarInfo> Sidecars
    {
        get => _sidecars;
        set
        {
            if (!SetProperty(ref _sidecars, value)) return;
            OnPropertyChanged(nameof(SidecarsText));
            OnPropertyChanged(nameof(HasSidecars));
        }
    }

    public bool HasSidecars => _sidecars.Count > 0;

    public string SidecarsText => Kind != MediaKind.Video
        ? string.Empty
        : _sidecars.Count == 0 ? "None" : string.Join(",  ", _sidecars.Select(s => s.ShortLabel));

    public static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)d.TotalHours, d.Minutes, d.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", d.Minutes, d.Seconds);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} {1}", bytes, units[unit])
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} {1}", value, units[unit]);
    }
}
