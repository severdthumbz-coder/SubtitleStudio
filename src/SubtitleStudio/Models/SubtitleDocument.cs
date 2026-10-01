using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Models;

/// <summary>A single timed subtitle cue. Observable so the editor can bind to it directly.</summary>
public sealed class SubtitleCue : ObservableObject
{
    private int _index;
    private TimeSpan _start;
    private TimeSpan _end;
    private string _text = string.Empty;

    public int Index { get => _index; set => SetProperty(ref _index, value); }

    public TimeSpan Start
    {
        get => _start;
        set { if (SetProperty(ref _start, value)) OnPropertyChanged(nameof(Duration)); }
    }

    public TimeSpan End
    {
        get => _end;
        set { if (SetProperty(ref _end, value)) OnPropertyChanged(nameof(Duration)); }
    }

    public TimeSpan Duration => _end - _start;

    /// <summary>
    /// Cue text in the dialect of the document's Format (HTML-style tags for SRT/VTT, override
    /// blocks for ASS/SSA, control codes for MicroDVD). Line breaks are "\n". Tags are kept verbatim.
    /// </summary>
    public string Text { get => _text; set => SetProperty(ref _text, value ?? string.Empty); }

    /// <summary>
    /// Format-specific per-cue data kept for lossless round-trips, e.g. ASS Layer/Style/Name/Margins/Effect,
    /// VTT cue id and settings. Keys are the format's own column names; "_kind" marks ASS Comment lines.
    /// </summary>
    public Dictionary<string, string>? Extra { get; set; }

    public SubtitleCue Clone() => new()
    {
        Index = Index,
        Start = Start,
        End = End,
        Text = Text,
        Extra = Extra is null ? null : new Dictionary<string, string>(Extra),
    };
}

/// <summary>A subtitle track: cues plus the metadata needed to name its sidecar.</summary>
public sealed class SubtitleDocument
{
    public List<SubtitleCue> Cues { get; } = new();

    /// <summary>ISO 639 code, e.g. "en". Null when unknown.</summary>
    public string? Language { get; set; }

    /// <summary>Format id the cue text is currently written in: srt, vtt, ass, ssa, sub.</summary>
    public string Format { get; set; } = "srt";

    public bool Forced { get; set; }
    public bool HearingImpaired { get; set; }
    public bool Sdh { get; set; }

    public string? SourcePath { get; set; }

    /// <summary>Encoding the file was read with, for display ("UTF-8", "Windows-1252", ...).</summary>
    public string? SourceEncoding { get; set; }

    /// <summary>Format header kept verbatim on round-trip (ASS [Script Info]/[Styles]/[Events] Format; VTT header/STYLE/REGION).</summary>
    public string? FormatHeader { get; set; }

    /// <summary>Format sections after the cues, kept verbatim (ASS [Fonts]/[Graphics]).</summary>
    public string? FormatTrailer { get; set; }

    /// <summary>Frames per second for frame-based formats (MicroDVD .sub). Null when not applicable.</summary>
    public double? FrameRate { get; set; }

    /// <summary>True when a frame-based file had no frame-rate line and a default was assumed.</summary>
    public bool FrameRateAssumed { get; set; }
}
