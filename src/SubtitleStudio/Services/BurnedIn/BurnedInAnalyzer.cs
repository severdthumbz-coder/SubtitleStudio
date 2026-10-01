using System.Text;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services.BurnedIn;

/// <summary>
/// OCR result for one sampled frame (line boxes in pixels of a frame of the given size).
/// <paramref name="LightRatios"/>, when given, is parallel to <paramref name="Lines"/>: the share of
/// white / yellow pixels inside each line box (see <see cref="SubtitleImageCleaner.LightRatio"/>).
/// </summary>
public sealed record FrameText(TimeSpan Time, int FrameWidth, int FrameHeight, IReadOnlyList<OcrLine> Lines, IReadOnlyList<double>? LightRatios = null);

public enum SubtitlePosition
{
    Bottom,
    Top,
}

public sealed record DetectedLine(TimeSpan Time, string Text);

/// <param name="LineHeight">Typical height of one subtitle line as a fraction of the frame height.</param>
/// <param name="LightText">Subtitles are white or yellow, so the background clean-up can be used.</param>
/// <param name="PreviewTime">A sampled moment that shows subtitles in the detected area (for the preview frame).</param>
public sealed record DetectionResult(
    bool Likely,
    int SampleCount,
    int SamplesWithSubtitles,
    double BandTop,
    double BandBottom,
    SubtitlePosition Position,
    IReadOnlyList<DetectedLine> Examples,
    IReadOnlyList<string> IgnoredStaticTexts,
    double LineHeight = BurnedInAnalyzer.DefaultLineHeight,
    bool LightText = true,
    TimeSpan? PreviewTime = null)
{
    public double HitRatio => SampleCount == 0 ? 0 : (double)SamplesWithSubtitles / SampleCount;
}

/// <summary>
/// Pure logic behind the Burned-in tab: decides whether a video has burned-in subtitles and where,
/// and turns per-frame OCR text into timed cues. No ffmpeg / OCR here, so it's fully testable.
/// </summary>
public static class BurnedInAnalyzer
{
    public const double DefaultLineHeight = 0.055;

    /// <summary>Shortest cue kept by <see cref="BuildCues"/>.</summary>
    public const double MinCueSeconds = 0.7;

    /// <summary>A group of stacked lines in one frame (a one- or two-line subtitle, a sign, a credit block).</summary>
    private sealed record Block(int Sample, double Top, double Bottom, double LineHeight, IReadOnlyList<OcrLine> Lines, IReadOnlyList<double> Light, string Text)
    {
        public double Center => (Top + Bottom) / 2;
    }

    /// <summary>
    /// Finds where the subtitles are. Subtitle-like lines (centred, plausible size, with letters) are
    /// grouped into blocks per frame. Real subtitles sit at the same baseline (bottom edge, or top edge
    /// for top subtitles) in many frames and say something different almost every time; signs, screens
    /// and on-stage text either move around or keep repeating the same words. So blocks are clustered
    /// by their anchor edge and the cluster with the most different texts wins. The area is then sized
    /// for two lines of that height, so it hugs the subtitles instead of spanning everything with text.
    /// </summary>
    public static DetectionResult Detect(IReadOnlyList<FrameText> samples)
    {
        int n = samples.Count;
        if (n == 0) return new DetectionResult(false, 0, 0, 0.78, 0.96, SubtitlePosition.Bottom, Array.Empty<DetectedLine>(), Array.Empty<string>());

        // Static text: same normalised text in at least 60% of samples (logos, watermarks).
        var occurrences = new Dictionary<string, int>();
        foreach (var s in samples)
            foreach (var key in s.Lines.Select(l => Normalize(l.Text)).Where(t => t.Length > 0).Distinct())
                occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
        var staticTexts = occurrences.Where(kv => n >= 4 && kv.Value >= Math.Ceiling(n * 0.6)).Select(kv => kv.Key).ToHashSet();

        var blocks = new List<Block>();
        for (int i = 0; i < n; i++)
            blocks.AddRange(BlocksOf(i, samples[i], staticTexts));

        if (blocks.Count == 0)
            return new DetectionResult(false, n, 0, 0.78, 0.96, SubtitlePosition.Bottom, Array.Empty<DetectedLine>(), staticTexts.ToList());

        var bottomPick = BestCluster(blocks.Where(b => b.Center >= 0.5).ToList(), b => b.Bottom);
        var topPick = BestCluster(blocks.Where(b => b.Center < 0.5).ToList(), b => b.Top);
        bool useTop = topPick.Score > bottomPick.Score;
        var (chosen, anchor) = useTop ? (topPick.Blocks, topPick.Anchor) : (bottomPick.Blocks, bottomPick.Anchor);
        var position = useTop ? SubtitlePosition.Top : SubtitlePosition.Bottom;

        if (chosen.Count == 0)
            return new DetectionResult(false, n, 0, 0.78, 0.96, SubtitlePosition.Bottom, Array.Empty<DetectedLine>(), staticTexts.ToList());

        var heights = chosen.SelectMany(b => b.Lines.Select(l => l.Height / samples[b.Sample].FrameHeight)).OrderBy(h => h).ToList();
        double lineH = Percentile(heights, 0.5);

        // Room for two lines with normal spacing (plus a third if one was seen), and a margin of a third of a line.
        double margin = lineH * 0.35;
        double top, bottom;
        if (position == SubtitlePosition.Bottom)
        {
            bottom = Percentile(chosen.Select(b => b.Bottom).OrderBy(v => v).ToList(), 0.95) + margin;
            top = Math.Min(chosen.Min(b => b.Top), anchor - 3.0 * lineH) - margin;
        }
        else
        {
            top = Percentile(chosen.Select(b => b.Top).OrderBy(v => v).ToList(), 0.05) - margin;
            bottom = Math.Max(chosen.Max(b => b.Bottom), anchor + 3.0 * lineH) + margin;
        }
        top = Math.Clamp(top, 0, 1);
        bottom = Math.Clamp(bottom, top + 0.02, 1);

        int hits = chosen.Select(b => b.Sample).Distinct().Count();
        int distinct = DistinctTexts(chosen.Select(b => b.Text));
        bool likely = hits >= Math.Max(3, (int)Math.Ceiling(n * 0.1)) && distinct >= 3;

        var light = chosen.SelectMany(b => b.Light).OrderBy(v => v).ToList();
        bool lightText = light.Count == 0 || Percentile(light, 0.5) >= 0.06;

        var examples = chosen.OrderBy(b => b.Sample).Take(12).Select(b => new DetectedLine(samples[b.Sample].Time, b.Text)).ToList();
        // Preview: prefer a two-line subtitle (shows the full height of the area), else the longest text.
        var previewBlock = chosen.OrderByDescending(b => Math.Min(b.Lines.Count, 2)).ThenByDescending(b => b.Text.Length).First();

        return new DetectionResult(likely, n, hits, top, bottom, position, examples, staticTexts.ToList(), lineH, lightText, samples[previewBlock.Sample].Time);
    }

    private static IEnumerable<Block> BlocksOf(int index, FrameText s, HashSet<string> staticTexts)
    {
        var lines = new List<(OcrLine Line, double Light)>();
        for (int i = 0; i < s.Lines.Count; i++)
        {
            var l = s.Lines[i];
            if (IsSubtitleLike(l, s.FrameWidth, s.FrameHeight) && !staticTexts.Contains(Normalize(l.Text)))
                lines.Add((l, s.LightRatios is { } lr && i < lr.Count ? lr[i] : -1));
        }
        lines.Sort((a, b) => a.Line.Y.CompareTo(b.Line.Y));

        var current = new List<(OcrLine Line, double Light)>();
        IEnumerable<Block> Flush()
        {
            if (current.Count is > 0 and <= 3) // more than three stacked lines: credits or a text screen
            {
                double h = s.FrameHeight;
                var text = string.Join("\n", current.Select(c => c.Line.Text.Trim()));
                yield return new Block(index, current.Min(c => c.Line.Y) / h, current.Max(c => c.Line.Bottom) / h,
                    current.Average(c => c.Line.Height) / h, current.Select(c => c.Line).ToList(),
                    current.Where(c => c.Light >= 0).Select(c => c.Light).ToList(), text);
            }
            current.Clear();
        }

        foreach (var item in lines)
        {
            if (current.Count > 0)
            {
                var last = current[^1].Line;
                double gap = item.Line.Y - last.Bottom;
                double maxH = Math.Max(last.Height, item.Line.Height);
                bool stacked = gap <= maxH * 0.9 && Math.Abs(item.Line.CenterX - last.CenterX) <= s.FrameWidth * 0.15;
                if (!stacked)
                    foreach (var b in Flush()) yield return b;
            }
            current.Add(item);
        }
        foreach (var b in Flush()) yield return b;
    }

    /// <summary>Densest anchor position, scored by how many different texts sit there (then by frames).</summary>
    private static (List<Block> Blocks, double Anchor, double Score) BestCluster(List<Block> blocks, Func<Block, double> anchorOf)
    {
        if (blocks.Count == 0) return (new List<Block>(), 0, -1);
        const double window = 0.025;
        List<Block> best = new();
        double bestAnchor = 0, bestScore = -1;
        foreach (var candidate in blocks.Select(anchorOf).Distinct())
        {
            var members = blocks.Where(b => Math.Abs(anchorOf(b) - candidate) <= window).ToList();
            double centre = members.Average(anchorOf);
            members = blocks.Where(b => Math.Abs(anchorOf(b) - centre) <= window).ToList();
            if (members.Count == 0) continue;
            double score = DistinctTexts(members.Select(m => m.Text)) * 1000 + members.Select(m => m.Sample).Distinct().Count();
            // The middle of the picture is where signs and screens are; subtitles rarely are.
            if (centre is > 0.3 and < 0.7) score /= 2;
            if (score > bestScore)
            {
                bestScore = score;
                best = members;
                bestAnchor = centre;
            }
        }
        return (best, bestAnchor, bestScore);
    }

    private static int DistinctTexts(IEnumerable<string> texts)
    {
        var reps = new List<string>();
        foreach (var t in texts.Select(Normalize).Where(t => t.Length > 0))
            if (!reps.Any(r => Similarity(r, t) >= 0.75)) reps.Add(t);
        return reps.Count;
    }

    public static bool IsSubtitleLike(OcrLine line, int frameWidth, int frameHeight)
    {
        var text = line.Text.Trim();
        if (text.Count(char.IsLetter) < 2) return false;
        double h = line.Height / frameHeight;
        if (h < 0.02 || h > 0.16) return false;
        double centreOffset = Math.Abs(line.CenterX - frameWidth / 2.0) / frameWidth;
        return centreOffset <= 0.2 && line.Width / frameWidth >= 0.04;
    }

    /// <summary>
    /// Keeps the OCR lines of one band image that can be subtitle text and joins them top to bottom:
    /// roughly centred, about the expected line height (when known), and not garbage. More than three
    /// lines left means a credit roll or a text screen, which is not a subtitle.
    /// </summary>
    public static string SubtitleTextOf(IReadOnlyList<OcrLine> lines, int imageWidth, double? expectedLineHeightPx)
        => TextOf(SubtitleLinesOf(lines, imageWidth, expectedLineHeightPx));

    public static IReadOnlyList<OcrLine> SubtitleLinesOf(IReadOnlyList<OcrLine> lines, int imageWidth, double? expectedLineHeightPx)
    {
        var kept = new List<OcrLine>();
        foreach (var l in MergeRows(lines))
        {
            if (l.Text.Count(char.IsLetter) < 1) continue;
            if (Math.Abs(l.CenterX - imageWidth / 2.0) > imageWidth * 0.25) continue;
            if (expectedLineHeightPx is { } eh && (l.Height < eh * 0.55 || l.Height > eh * 1.7)) continue;
            if (TextQuality(l.Text) < 0.45) continue;
            kept.Add(l);
        }
        return kept.Count > 3 ? Array.Empty<OcrLine>() : kept.OrderBy(l => l.Y).ToList();
    }

    /// <summary>
    /// OCR engines return one screen line as several "lines" when words in it are missing or far apart
    /// ("-ook at" / "You ha" / "inged a:" side by side). Pieces that overlap vertically by at least half
    /// their height and are within four line heights of each other are one row: joined left to right
    /// with a space, their boxes combined.
    /// </summary>
    public static IReadOnlyList<OcrLine> MergeRows(IReadOnlyList<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var l in lines.Where(l => l.Text.Trim().Length > 0).OrderBy(l => l.Y + l.Height / 2))
        {
            var row = rows.FirstOrDefault(r =>
            {
                double top = r.Min(x => x.Y), bottom = r.Max(x => x.Bottom);
                double overlap = Math.Min(bottom, l.Bottom) - Math.Max(top, l.Y);
                if (overlap < 0.5 * Math.Min(bottom - top, l.Height)) return false;
                // Close enough to be words of one line (a sign far off to the side is not).
                double gap = Math.Max(l.X - r.Max(x => x.X + x.Width), r.Min(x => x.X) - (l.X + l.Width));
                return gap <= 4 * Math.Max(bottom - top, l.Height);
            });
            if (row is null) rows.Add(new List<OcrLine> { l });
            else row.Add(l);
        }
        return rows.Select(r =>
        {
            if (r.Count == 1) return r[0];
            var ordered = r.OrderBy(x => x.X).ToList();
            double left = ordered.Min(x => x.X), top = ordered.Min(x => x.Y), right = ordered.Max(x => x.X + x.Width), bottom = ordered.Max(x => x.Bottom);
            return new OcrLine(string.Join(' ', ordered.Select(x => x.Text.Trim())), left, top, right - left, bottom - top);
        }).OrderBy(l => l.Y).ToList();
    }

    /// <summary>
    /// A web address in the text ("HollyMovieHD.Com", "GOHD.CC", "www.site.net", "https://..."): the mark of
    /// a website ad burned into the video along with the subtitles.
    /// </summary>
    public static bool LooksLikeWebAddress(string text) => WebAddress.IsMatch(text);

    private static readonly System.Text.RegularExpressions.Regex WebAddress = new(
        // No spaces around the dot and at least three characters before it, so dialogue like "I want to. In
        // fact..." or "5 p.m." never counts.
        @"(?i)\b(www\.|https?://)|\b[a-z0-9][a-z0-9-]{2,}\.(com|net|org|cc|tv|io|me|to|co|info|xyz|site|online|top|vip|live|app|club|pro|biz|ws|la|in|ru|cn|vn)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string TextOf(IReadOnlyList<OcrLine> lines) => Clean(string.Join("\n", lines.Select(l => l.Text.Trim())));

    /// <summary>
    /// Groups per-frame texts into cues:
    ///  - frames whose text matches the current cue (OCR jitter, a stray symbol in front) extend it;
    ///  - one empty or misread frame inside a cue is bridged when the next frame matches again;
    ///  - each cue takes the reading that agrees best with all its frames (not just the most common);
    ///  - neighbouring cues with the same text and a short gap are merged (a line split by misreads);
    ///  - cues shorter than <paramref name="minFrames"/> frames or 0.7 s, or that read as garbage, are dropped.
    /// </summary>
    public static List<SubtitleCue> BuildCues(IReadOnlyList<(TimeSpan Time, string Text)> frames, TimeSpan frameInterval, int minFrames = 2)
    {
        var texts = frames.Select(f => Clean(f.Text)).ToList();
        var groups = new List<(int First, int Last, List<string> Reads)>();

        int i = 0;
        while (i < frames.Count)
        {
            if (texts[i].Length == 0) { i++; continue; }
            int first = i, last = i;
            var reads = new List<string> { texts[i] };
            int j = i + 1;
            while (j < frames.Count)
            {
                var reference = BestReading(reads);
                if (texts[j].Length > 0 && Matches(texts[j], reference))
                {
                    reads.Add(texts[j]);
                    last = j;
                    j++;
                    continue;
                }
                // One bad frame (empty or misread) is bridged if the next one matches again.
                if (j + 1 < frames.Count && texts[j + 1].Length > 0 && Matches(texts[j + 1], reference))
                {
                    j++;
                    continue;
                }
                break;
            }
            groups.Add((first, last, reads));
            i = last + 1;
        }

        // Merge neighbours that say the same thing and are close together.
        var merged = new List<(int First, int Last, List<string> Reads)>();
        foreach (var g in groups)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                bool close = (frames[g.First].Time - frames[prev.Last].Time) <= frameInterval * 3.01;
                if (close && Matches(BestReading(prev.Reads), BestReading(g.Reads)))
                {
                    prev.Reads.AddRange(g.Reads);
                    merged[^1] = (prev.First, g.Last, prev.Reads);
                    continue;
                }
            }
            merged.Add(g);
        }

        // Real subtitles stay up for well over half a second; shorter runs are flicker or passing text.
        int needFrames = Math.Max(minFrames, (int)Math.Ceiling(MinCueSeconds / Math.Max(0.001, frameInterval.TotalSeconds) - 1e-9));
        var cues = new List<SubtitleCue>();
        foreach (var g in merged)
        {
            if (g.Reads.Count < needFrames) continue;
            var best = BestReading(g.Reads);
            if (TextQuality(best) < 0.5) continue;
            cues.Add(new SubtitleCue { Start = frames[g.First].Time, End = frames[g.Last].Time + frameInterval, Text = best });
        }

        for (int k = 0; k < cues.Count; k++)
        {
            cues[k].Index = k + 1;
            if (k + 1 < cues.Count && cues[k].End > cues[k + 1].Start) cues[k].End = cues[k + 1].Start;
        }
        return cues;
    }

    /// <summary>Two readings of the same subtitle: close overall, or one contains the other (garbage around it).</summary>
    public static bool Matches(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (Similarity(na, nb) >= 0.75) return true;
        var (s, l) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        return s.Length >= 5 && s.Length >= l.Length * 0.6 && PartialSimilarity(s, l) >= 0.85;
    }

    /// <summary>
    /// The reading that agrees best with all the others (a medoid), with a bonus for readings that look
    /// like real words. Plain majority voting fails when every frame is misread slightly differently.
    /// </summary>
    public static string BestReading(IReadOnlyList<string> reads)
    {
        if (reads.Count == 1) return reads[0];
        var distinct = reads.GroupBy(r => r).Select(g => (Text: g.Key, Count: g.Count(), Norm: Normalize(g.Key))).ToList();
        if (distinct.Count == 1) return distinct[0].Text;
        string best = distinct[0].Text;
        double bestScore = double.MinValue;
        foreach (var candidate in distinct)
        {
            double agreement = distinct.Sum(o => o.Count * Similarity(candidate.Norm, o.Norm)) / reads.Count;
            double score = agreement + 0.5 * TextQuality(candidate.Text) + 0.001 * candidate.Count;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Text;
            }
        }
        return best;
    }

    private static readonly HashSet<char> KeepAlone = new() { '-', '–', '—', '♪', '♫', '…', '&', '"', '“', '”', '«', '»', '#', '$', '%', '€', '£' };

    /// <summary>
    /// Tidy OCR output: trim lines, collapse spaces, drop tokens that are only stray symbols (| ] _ ~ ...),
    /// and drop lines with fewer than two letters or digits.
    /// </summary>
    public static string Clean(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n')
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Any(char.IsLetterOrDigit) || t.All(KeepAlone.Contains) || t is "..." or "?" or "!")))
            .Select(l => l.Trim())
            .Where(l => l.Count(char.IsLetterOrDigit) >= 2);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 0..1: how much a line looks like real text rather than an OCR misread of a picture. Counts
    /// word-shaped tokens (lower case, Capitalised, ALL CAPS, numbers, 1st / 90s, McName) and
    /// penalises stray symbols. Works for any language written with spaces; unspaced scripts
    /// (Chinese, Japanese) come out as single long words and pass.
    /// </summary>
    public static double TextQuality(string text)
    {
        var tokens = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 0;
        double good = 0, total = 0;
        int odd = 0, chars = 0;
        foreach (var raw in tokens)
        {
            foreach (var c in raw)
            {
                if (char.IsWhiteSpace(c)) continue;
                chars++;
                if (!char.IsLetterOrDigit(c) && ".,!?'’\"“”-–—:;…()♪&%$€£#/*".IndexOf(c) < 0) odd++;
            }
            var word = raw.Trim(".,!?'’\"“”-–—:;…()♪*[]".ToCharArray());
            if (word.Length == 0) continue;
            double weight = Math.Min(word.Length, 6);
            total += weight;
            if (IsWordShaped(word)) good += weight;
        }
        if (total == 0) return 0;
        double oddPenalty = chars == 0 ? 0 : Math.Min(1, 3.0 * odd / chars);
        return good / total * (1 - oddPenalty);
    }

    private const string Vowels = "aeiouyàáâãäåæèéêëìíîïòóôõöøùúûüýÿœ";

    private static bool IsWordShaped(string w)
    {
        // Contractions and possessives: the word before the apostrophe decides (I'M, don't, McDonald's, l'homme).
        int apostrophe = w.IndexOfAny(new[] { '\'', '’' });
        if (apostrophe > 0 && apostrophe < w.Length - 1)
        {
            var tail = w[(apostrophe + 1)..];
            return IsWordShaped(w[..apostrophe]) && tail.Length <= 3 && tail.All(char.IsLetter)
                || IsWordShaped(w[..apostrophe]) && IsWordShaped(tail);
        }

        // Hyphenated words: every part word-shaped, and not ALL CAPS glued to lower case (HH-a).
        var parts = w.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            bool caps = parts.Any(p => p.Length >= 2 && p.All(c => !char.IsLower(c)) && p.Any(char.IsUpper));
            bool lower = parts.Any(p => p.All(c => !char.IsUpper(c)) && p.Any(char.IsLower));
            return parts.All(IsWordShaped) && !(caps && lower);
        }

        if (w.All(c => char.IsDigit(c) || c is ',' or '.' or ':')) return true;
        if (char.IsDigit(w[0]))
        {
            int d = 0;
            while (d < w.Length && char.IsDigit(w[d])) d++;
            var suffix = w[d..].ToLowerInvariant();
            return suffix is "st" or "nd" or "rd" or "th" or "s" or "am" or "pm" or "m" or "k" or "h" or "x";
        }
        if (!w.All(char.IsLetter)) return false;
        // Latin-script words of four letters or more have a vowel ("hcnksj", "wmch" are misreads of the picture).
        if (w.Length >= 4 && w.All(c => c < 0x250) && !w.Any(c => Vowels.Contains(char.ToLowerInvariant(c)))) return false;
        if (w.Length == 1)
            return w is "a" or "A" or "I" or "O" or "o" or "e" or "y" or "à" or "é" or "è" or "ó" || !char.IsUpper(w[0]) && !char.IsLower(w[0]);

        bool anyCase = w.Any(char.IsUpper) || w.Any(char.IsLower);
        if (!anyCase) return true; // caseless script
        if (w.All(c => !char.IsLower(c))) return true;                      // ALL CAPS
        if (w.Skip(1).All(c => !char.IsUpper(c))) return true;             // lower or Capitalised
        // McDonald / MacLeod / DiCaprio / LeBlanc: Capital + lower + Capital + lower
        int uppers = w.Count(char.IsUpper);
        return uppers == 2 && char.IsUpper(w[0]) && char.IsLower(w[1]) && char.IsLower(w[^1]) && w.Length >= 5;
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>1 - normalised Levenshtein distance.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Share of <paramref name="part"/> found in <paramref name="whole"/> in order (longest common subsequence).</summary>
    public static double Coverage(string part, string whole)
    {
        if (part.Length == 0) return 1;
        if (whole.Length == 0) return 0;
        var prev = new int[whole.Length + 1];
        var curr = new int[whole.Length + 1];
        for (int i = 1; i <= part.Length; i++)
        {
            for (int j = 1; j <= whole.Length; j++)
                curr[j] = part[i - 1] == whole[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], curr[j - 1]);
            (prev, curr) = (curr, prev);
        }
        return (double)prev[whole.Length] / part.Length;
    }

    /// <summary>How well <paramref name="shorter"/> matches its best-fitting stretch of <paramref name="longer"/> (free ends).</summary>
    public static double PartialSimilarity(string shorter, string longer)
    {
        if (shorter.Length == 0) return 0;
        var prev = new int[longer.Length + 1]; // row 0: starting anywhere in longer is free
        var curr = new int[longer.Length + 1];
        for (int i = 1; i <= shorter.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= longer.Length; j++)
            {
                int cost = shorter[i - 1] == longer[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return 1.0 - (double)prev.Min() / shorter.Length;
    }

    private static double Percentile(List<double> sorted, double p)
        => sorted[Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
}
