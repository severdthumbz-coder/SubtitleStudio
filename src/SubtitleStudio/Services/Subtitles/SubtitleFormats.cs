using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Subtitles;

/// <summary>Thrown when a file cannot be read as the format its extension claims.</summary>
public sealed class SubtitleFormatException : Exception
{
    public SubtitleFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Shared plumbing for text formats: decoding, line endings, encoding on write.</summary>
public abstract partial class TextSubtitleFormat : ISubtitleFormat
{
    [GeneratedRegex(@"^\s*(?<start>(?:\d+:)?\d{1,2}:\d{1,2}(?:[,.:]\d{1,3})?)\s*-->\s*(?<end>(?:\d+:)?\d{1,2}:\d{1,2}(?:[,.:]\d{1,3})?)(?<rest>.*)$", RegexOptions.CultureInvariant)]
    protected static partial Regex TimingLine();

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<string> Extensions { get; }
    public bool CanRead => true;
    public bool CanWrite => true;

    /// <summary>Encoding used when writing.</summary>
    protected virtual Encoding WriteEncoding => SubtitleEncoding.Utf8WithBom;

    public SubtitleDocument Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        CheckBytes(bytes);

        var (text, encodingName) = SubtitleEncoding.Decode(bytes);
        var doc = new SubtitleDocument { Format = Id, SourceEncoding = encodingName };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Parse(lines, doc);

        for (int i = 0; i < doc.Cues.Count; i++) doc.Cues[i].Index = i + 1;
        return doc;
    }

    public void Write(SubtitleDocument document, Stream stream)
    {
        var sb = new StringBuilder();
        Render(document, sb);
        var text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = WriteEncoding.GetPreamble().Concat(WriteEncoding.GetBytes(text)).ToArray();
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Hook to reject binary look-alikes before decoding.</summary>
    protected virtual void CheckBytes(byte[] bytes) { }

    protected abstract void Parse(string[] lines, SubtitleDocument doc);

    protected abstract void Render(SubtitleDocument doc, StringBuilder sb);

    /// <summary>Cue text converted from the document's dialect into this format's dialect.</summary>
    protected string TextFor(SubtitleDocument doc, SubtitleCue cue)
        => SubtitleText.Convert(cue.Text, SubtitleText.DialectOf(doc.Format), SubtitleText.DialectOf(Id));

    protected static bool IsIndexLine(string line) => line.Trim().Length > 0 && line.Trim().All(char.IsDigit);
}

// ================================================================================================
//  SRT
// ================================================================================================
public sealed class SrtFormat : TextSubtitleFormat
{
    public override string Id => "srt";
    public override string DisplayName => "SubRip (.srt)";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".srt" };

    protected override void Parse(string[] lines, SubtitleDocument doc)
    {
        int i = 0;
        while (i < lines.Length)
        {
            var timing = TimingLine().Match(lines[i]);
            if (!timing.Success
                || !Timecode.TryParse(timing.Groups["start"].Value, out var start)
                || !Timecode.TryParse(timing.Groups["end"].Value, out var end))
            {
                i++;
                continue;
            }

            i++;
            var text = new List<string>();
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0) break;
                if (TimingLine().IsMatch(line)) break;
                if (IsIndexLine(line) && i + 1 < lines.Length && TimingLine().IsMatch(lines[i + 1])) break;
                text.Add(line.TrimEnd());
                i++;
            }

            doc.Cues.Add(new SubtitleCue { Start = start, End = end, Text = string.Join("\n", text) });
        }
    }

    protected override void Render(SubtitleDocument doc, StringBuilder sb)
    {
        int n = 1;
        foreach (var cue in doc.Cues)
        {
            sb.Append(n++).Append('\n');
            sb.Append(Timecode.FormatSrt(cue.Start)).Append(" --> ").Append(Timecode.FormatSrt(cue.End)).Append('\n');
            sb.Append(TextFor(doc, cue).TrimEnd('\n')).Append("\n\n");
        }
    }
}

// ================================================================================================
//  WebVTT
// ================================================================================================
public sealed class VttFormat : TextSubtitleFormat
{
    public override string Id => "vtt";
    public override string DisplayName => "WebVTT (.vtt)";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".vtt" };

    // The spec allows a BOM; plain UTF-8 is the most compatible choice for browsers and players.
    protected override Encoding WriteEncoding => SubtitleEncoding.Utf8NoBom;

    protected override void Parse(string[] lines, SubtitleDocument doc)
    {
        var blocks = SplitBlocks(lines);
        if (blocks.Count == 0 || !blocks[0][0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal))
            throw new SubtitleFormatException("Not a WebVTT file: the first line must start with WEBVTT.");

        var header = new List<string> { string.Join("\n", blocks[0]) };
        bool cuesStarted = false;

        foreach (var block in blocks.Skip(1))
        {
            var first = block[0].TrimStart();
            if (first.StartsWith("NOTE", StringComparison.Ordinal)) continue;
            if (!cuesStarted && (first.StartsWith("STYLE", StringComparison.Ordinal) || first.StartsWith("REGION", StringComparison.Ordinal)))
            {
                header.Add(string.Join("\n", block));
                continue;
            }

            int timingIndex = TimingLine().IsMatch(block[0]) ? 0 : block.Count > 1 && TimingLine().IsMatch(block[1]) ? 1 : -1;
            if (timingIndex < 0) continue;

            var m = TimingLine().Match(block[timingIndex]);
            if (!Timecode.TryParse(m.Groups["start"].Value, out var start) || !Timecode.TryParse(m.Groups["end"].Value, out var end))
                continue;

            cuesStarted = true;
            var cue = new SubtitleCue { Start = start, End = end, Text = string.Join("\n", block.Skip(timingIndex + 1)) };
            var settings = m.Groups["rest"].Value.Trim();
            if (timingIndex == 1 || settings.Length > 0)
            {
                cue.Extra = new Dictionary<string, string>();
                if (timingIndex == 1) cue.Extra["id"] = block[0].Trim();
                if (settings.Length > 0) cue.Extra["settings"] = settings;
            }
            doc.Cues.Add(cue);
        }

        doc.FormatHeader = string.Join("\n\n", header);
    }

    protected override void Render(SubtitleDocument doc, StringBuilder sb)
    {
        var header = doc.Format == Id && !string.IsNullOrWhiteSpace(doc.FormatHeader) ? doc.FormatHeader!.Trim() : "WEBVTT";
        sb.Append(header).Append("\n\n");

        bool keepExtra = doc.Format == Id;
        foreach (var cue in doc.Cues)
        {
            if (keepExtra && cue.Extra is not null && cue.Extra.TryGetValue("id", out var id) && id.Length > 0)
                sb.Append(id).Append('\n');

            sb.Append(Timecode.FormatVtt(cue.Start)).Append(" --> ").Append(Timecode.FormatVtt(cue.End));
            if (keepExtra && cue.Extra is not null && cue.Extra.TryGetValue("settings", out var settings) && settings.Length > 0)
                sb.Append(' ').Append(settings);
            sb.Append('\n');

            // "-->" is not allowed inside cue text.
            var text = SubtitleText.ForVtt(TextFor(doc, cue)).Replace("-->", "->").TrimEnd('\n');
            sb.Append(text).Append("\n\n");
        }
    }

    private static List<List<string>> SplitBlocks(string[] lines)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = new List<string>();
                blocks.Add(current);
            }
            current.Add(line);
        }
        return blocks;
    }
}

// ================================================================================================
//  ASS / SSA
// ================================================================================================
public sealed class AssFormat : TextSubtitleFormat
{
    private const string AssEventColumns = "Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";
    private const string SsaEventColumns = "Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text";

    private readonly bool _ssa;

    public AssFormat(bool ssa)
    {
        _ssa = ssa;
        Extensions = new[] { ssa ? ".ssa" : ".ass" };
    }

    public override string Id => _ssa ? "ssa" : "ass";
    public override string DisplayName => _ssa ? "SubStation Alpha (.ssa)" : "Advanced SubStation Alpha (.ass)";
    public override IReadOnlyList<string> Extensions { get; }

    protected override void Parse(string[] lines, SubtitleDocument doc)
    {
        var header = new StringBuilder();
        var trailer = new StringBuilder();
        string[]? columns = null;
        string section = string.Empty;
        bool sawEvents = false, afterEvents = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed.ToLowerInvariant();
                if (section == "[events]") sawEvents = true;
                else if (sawEvents) afterEvents = true;
            }

            if (afterEvents)
            {
                trailer.Append(line).Append('\n');
                continue;
            }

            if (section == "[events]")
            {
                if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    columns = trimmed["Format:".Length..].Split(',').Select(c => c.Trim()).ToArray();
                    header.Append(line).Append('\n');
                    continue;
                }

                var kind = trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) ? "Dialogue"
                         : trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase) ? "Comment"
                         : null;
                if (kind is not null)
                {
                    columns ??= (_ssa ? SsaEventColumns : AssEventColumns).Split(',').Select(c => c.Trim()).ToArray();
                    var cue = ParseEvent(trimmed[(kind.Length + 1)..].TrimStart(), columns, kind);
                    if (cue is not null) doc.Cues.Add(cue);
                    continue;
                }

                if (columns is not null && trimmed.Length > 0 && !trimmed.StartsWith(';')) continue; // unknown event lines
            }

            if (!(section == "[events]" && columns is not null))
                header.Append(line).Append('\n');
        }

        if (!sawEvents && doc.Cues.Count == 0)
            throw new SubtitleFormatException("Not an ASS/SSA script: no [Events] section found.");

        if (columns is null)
            header.Append("Format: ").Append(_ssa ? SsaEventColumns : AssEventColumns).Append('\n');

        doc.FormatHeader = header.ToString().TrimEnd('\n');
        doc.FormatTrailer = trailer.Length > 0 ? trailer.ToString().TrimEnd('\n') : null;
    }

    private static SubtitleCue? ParseEvent(string payload, string[] columns, string kind)
    {
        var values = payload.Split(',', columns.Length);
        if (values.Length < columns.Length) return null;

        var cue = new SubtitleCue { Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
        if (kind == "Comment") cue.Extra["_kind"] = "Comment";

        for (int c = 0; c < columns.Length; c++)
        {
            var col = columns[c];
            var value = values[c];
            if (col.Equals("Start", StringComparison.OrdinalIgnoreCase))
            {
                if (!Timecode.TryParse(value, out var t)) return null;
                cue.Start = t;
            }
            else if (col.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                if (!Timecode.TryParse(value, out var t)) return null;
                cue.End = t;
            }
            else if (col.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                // Keep ASS text verbatim, but show hard line breaks as real newlines in the editor.
                cue.Text = value.Replace("\\N", "\n");
            }
            else
            {
                cue.Extra[col] = value.Trim();
            }
        }
        return cue;
    }

    protected override void Render(SubtitleDocument doc, StringBuilder sb)
    {
        bool sameFormat = doc.Format == Id && !string.IsNullOrWhiteSpace(doc.FormatHeader);
        var header = sameFormat ? doc.FormatHeader!.TrimEnd() : DefaultHeader();
        sb.Append(header).Append('\n');

        var formatLine = header.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("Format:", StringComparison.OrdinalIgnoreCase));
        var columns = (formatLine?["Format:".Length..] ?? (_ssa ? SsaEventColumns : AssEventColumns))
            .Split(',').Select(c => c.Trim()).ToArray();

        foreach (var cue in doc.Cues)
        {
            var kind = sameFormat && cue.Extra is not null && cue.Extra.TryGetValue("_kind", out var k) ? k : "Dialogue";
            var values = columns.Select(col => col.ToLowerInvariant() switch
            {
                "start" => Timecode.FormatAss(cue.Start),
                "end" => Timecode.FormatAss(cue.End),
                "text" => AssTextFor(doc, cue),
                _ => sameFormat && cue.Extra is not null && cue.Extra.TryGetValue(col, out var v) ? v : DefaultValue(col),
            });
            sb.Append(kind).Append(": ").Append(string.Join(",", values)).Append('\n');
        }

        if (sameFormat && !string.IsNullOrWhiteSpace(doc.FormatTrailer))
            sb.Append('\n').Append(doc.FormatTrailer!.TrimEnd()).Append('\n');
    }

    private string AssTextFor(SubtitleDocument doc, SubtitleCue cue)
    {
        var fromDialect = SubtitleText.DialectOf(doc.Format);
        // In memory, ASS text keeps real newlines for editing; on disk they must be \N.
        var text = fromDialect == TextDialect.Ass ? cue.Text : TextFor(doc, cue);
        return text.Replace("\r", string.Empty).Replace("\n", "\\N");
    }

    private string DefaultValue(string column) => column.ToLowerInvariant() switch
    {
        "layer" => "0",
        "marked" => "Marked=0",
        "style" => "Default",
        "marginl" or "marginr" or "marginv" => "0",
        _ => string.Empty,
    };

    private string DefaultHeader() => _ssa
        ? string.Join("\n",
            "[Script Info]",
            "; Script generated by Subtitle Studio",
            "ScriptType: v4.00",
            "PlayResX: 1920",
            "PlayResY: 1080",
            "",
            "[V4 Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, TertiaryColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, AlphaLevel, Encoding",
            "Style: Default,Arial,64,16777215,255,0,0,0,0,1,3,1,2,40,40,50,0,1",
            "",
            "[Events]",
            "Format: " + SsaEventColumns)
        : string.Join("\n",
            "[Script Info]",
            "; Script generated by Subtitle Studio",
            "ScriptType: v4.00+",
            "PlayResX: 1920",
            "PlayResY: 1080",
            "WrapStyle: 0",
            "ScaledBorderAndShadow: yes",
            "",
            "[V4+ Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
            "Style: Default,Arial,64,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,3,1,2,40,40,50,1",
            "",
            "[Events]",
            "Format: " + AssEventColumns);
}

// ================================================================================================
//  MicroDVD (.sub, text, frame-based)
// ================================================================================================
public sealed partial class MicroDvdFormat : TextSubtitleFormat
{
    public const double DefaultFrameRate = 23.976;

    [GeneratedRegex(@"^\{(?<s>\d+)\}\{(?<e>\d*)\}(?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LineRx();

    public override string Id => "sub";
    public override string DisplayName => "MicroDVD (.sub)";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".sub" };

    protected override void CheckBytes(byte[] bytes)
    {
        // VobSub .sub is an MPEG program stream (pack header 00 00 01 BA).
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0xBA)
            throw new SubtitleFormatException("This .sub is an image-based VobSub file (it pairs with an .idx). To read it as text, select the video in Source / Files and use \"Subtitles inside\" (it's read with Windows OCR there).");
        if (bytes.Take(4096).Count(b => b == 0) > 16)
            throw new SubtitleFormatException("This .sub file is binary, not MicroDVD text, so it can't be edited here.");
    }

    protected override void Parse(string[] lines, SubtitleDocument doc)
    {
        double? fps = null;
        var raw = new List<(long Start, long? End, string Text)>();

        foreach (var line in lines)
        {
            var m = LineRx().Match(line.Trim());
            if (!m.Success) continue;
            long s = long.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            long? e = m.Groups["e"].Value.Length > 0 ? long.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture) : null;
            var text = m.Groups["text"].Value;

            // Optional frame-rate line: {1}{1}23.976 (sometimes {0}{0}) as the very first entry.
            if (raw.Count == 0 && fps is null && s <= 1 && (e ?? 0) <= 1
                && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate is > 1 and < 200)
            {
                fps = rate;
                continue;
            }
            raw.Add((s, e, text));
        }

        if (raw.Count == 0 && fps is null)
            throw new SubtitleFormatException("No MicroDVD lines ({start}{end}text) were found in this .sub file.");

        doc.FrameRateAssumed = fps is null;
        doc.FrameRate = fps ?? DefaultFrameRate;
        double rateUsed = doc.FrameRate.Value;

        for (int i = 0; i < raw.Count; i++)
        {
            var (s, e, text) = raw[i];
            var start = TimeSpan.FromSeconds(s / rateUsed);
            var end = e is { } ef
                ? TimeSpan.FromSeconds(ef / rateUsed)
                : i + 1 < raw.Count ? TimeSpan.FromSeconds(raw[i + 1].Start / rateUsed) : start + TimeSpan.FromSeconds(3);
            doc.Cues.Add(new SubtitleCue { Start = start, End = end, Text = text.Replace("|", "\n") });
        }
    }

    protected override void Render(SubtitleDocument doc, StringBuilder sb)
    {
        double fps = doc.FrameRate is > 0 ? doc.FrameRate.Value : DefaultFrameRate;
        sb.Append("{1}{1}").Append(fps.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');

        var fromDialect = SubtitleText.DialectOf(doc.Format);
        foreach (var cue in doc.Cues)
        {
            long s = (long)Math.Round(Math.Max(0, cue.Start.TotalSeconds) * fps, MidpointRounding.AwayFromZero);
            long e = (long)Math.Round(Math.Max(0, cue.End.TotalSeconds) * fps, MidpointRounding.AwayFromZero);
            var text = fromDialect == TextDialect.MicroDvd ? cue.Text.Replace("\r", string.Empty).Replace("\n", "|") : TextFor(doc, cue);
            sb.Append('{').Append(s).Append("}{").Append(e).Append('}').Append(text).Append('\n');
        }
    }
}
