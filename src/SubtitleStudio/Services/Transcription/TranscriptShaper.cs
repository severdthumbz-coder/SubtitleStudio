using System.Text;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Transcription;

public sealed record CueShapeOptions
{
    /// <summary>Line width in columns (Korean, Chinese and Japanese characters count as two).</summary>
    public int MaxLineColumns { get; init; } = 42;
    public int MaxLines { get; init; } = 2;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(7);
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(0.8);

    /// <summary>A segment Whisper rates as probably not speech (above this) and isn't confident about is dropped.</summary>
    public float NoSpeechThreshold { get; init; } = 0.6f;
    public float LowConfidence { get; init; } = 0.5f;

    /// <summary>The same line this many times in a row is kept; more is Whisper looping, and dropped.</summary>
    public int MaxRepeats { get; init; } = 2;

    /// <summary>Silence at the start or end of a cue longer than this is cut off (0.15 s lead-in, 0.3 s hold kept).</summary>
    public TimeSpan SilenceTrim { get; init; } = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// Reading time: a cue stays up at most this long per column of text (plus <see cref="HoldBase"/>),
    /// so a one-word line isn't held for half a minute when Whisper's times run long.
    /// </summary>
    public TimeSpan HoldPerColumn { get; init; } = TimeSpan.FromSeconds(0.12);
    public TimeSpan HoldBase { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// Speaking pace used to place text when Whisper's own word times can't be used: about 5 Korean
    /// syllables or 12 Latin letters a second. Text is laid over the speech from the segment's start at
    /// this pace instead of being stretched over everything the segment spans.
    /// </summary>
    public TimeSpan SpeechPerColumn { get; init; } = TimeSpan.FromSeconds(0.085);
}

public sealed record ShapeResult(List<SubtitleCue> Cues, int DroppedNotSpeech, int DroppedRepeats, int DroppedKnownPhrases = 0);

/// <summary>
/// Turns Whisper's segments into subtitle cues: long segments split at sentence ends or commas near the
/// middle, text wrapped into at most two balanced lines, timings tidied (minimum duration, no overlaps),
/// and the two usual Whisper slips removed: text over silence or music that it doesn't believe is
/// speech, and a line repeated over and over when it gets stuck.
/// </summary>
public static class TranscriptShaper
{
    private const string StrongEnds = ".?!。？！…";
    private const string SoftEnds = ",;:、，；：";

    /// <param name="active">Sound per 10 ms of the audio Whisper heard (times before <paramref name="offset"/>): cue
    /// edges that hang over silence are pulled in to where the sound is.</param>
    public static ShapeResult Shape(IEnumerable<WhisperSegment> segments, CueShapeOptions? options = null, TimeSpan offset = default, bool[]? active = null)
    {
        var o = options ?? new CueShapeOptions();
        var pieces = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        int droppedNotSpeech = 0, droppedRepeats = 0, droppedPhrases = 0;

        // 1. Text, and what Whisper itself doubts.
        var kept = new List<(WhisperSegment Segment, string Text, string Key)>();
        foreach (var segment in segments)
        {
            var text = Clean(segment.Text);
            if (text.Length == 0 || (!text.Any(char.IsLetterOrDigit) && text.IndexOf('♪') < 0)) continue;
            if (segment.NoSpeechProbability > o.NoSpeechThreshold && segment.Probability > 0 && segment.Probability < o.LowConfidence)
            {
                droppedNotSpeech++;
                continue;
            }
            kept.Add((segment, text, Key(text)));
        }

        // 2. Whisper's known invented phrases, also when they come one word per segment.
        var drop = new bool[kept.Count];
        for (int i = 0; i < kept.Count; i++)
        {
            if (drop[i]) continue;
            var joined = new StringBuilder();
            for (int j = i; j < Math.Min(kept.Count, i + 8); j++)
            {
                joined.Append(kept[j].Key);
                if (j > i && kept[j].Segment.Start - kept[j - 1].Segment.End > TimeSpan.FromSeconds(3)) break;
                if (IsKnownPhrase(joined.ToString(), acrossSegments: j > i))
                {
                    for (int k = i; k <= j; k++) drop[k] = true;
                    droppedPhrases += j - i + 1;
                    break;
                }
            }
        }

        // 3. Repeats: the same line over and over, or a cycle of lines (Whisper stuck in a loop).
        var keys = kept.Select(k => k.Key).ToList();
        for (int i = 0; i < kept.Count; i++)
        {
            if (drop[i]) continue;
            for (int period = 2; period <= 8; period++)
            {
                if (i + 2 * period > kept.Count || !SameRun(keys, i, i + period, period) || keys.Skip(i).Take(period).Sum(k => k.Length) < 4) continue;
                for (int next = i + period; next + period <= kept.Count && SameRun(keys, i, next, period); next += period)
                    for (int k = next; k < next + period; k++)
                        if (!drop[k]) { drop[k] = true; droppedRepeats++; }
                break;
            }
        }
        string? lastKey = null;
        int repeats = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            if (drop[i]) continue;
            repeats = kept[i].Key == lastKey ? repeats + 1 : 1;
            lastKey = kept[i].Key;
            if (repeats > o.MaxRepeats)
            {
                drop[i] = true;
                droppedRepeats++;
            }
        }

        for (int i = 0; i < kept.Count; i++)
        {
            if (drop[i]) continue;
            var (segment, text, _) = kept[i];
            Split(text, 0, text.Length, TimeMap(segment, text, active, o), o, pieces);
        }

        if (active is not null)
            for (int i = 0; i < pieces.Count; i++)
                pieces[i] = TrimToSound(pieces[i], active, o);
        for (int i = 0; i < pieces.Count; i++)
        {
            var (start, end, text) = pieces[i];
            var hold = HoldFor(text, o);
            if (end - start > hold) pieces[i] = (start, start + hold, text);
        }

        pieces.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cues = new List<SubtitleCue>(pieces.Count);
        var shortest = TimeSpan.FromMilliseconds(300);
        TimeSpan previousEnd = TimeSpan.Zero;
        for (int i = 0; i < pieces.Count; i++)
        {
            var (start, end, text) = pieces[i];
            start += offset;
            end += offset;
            if (start < previousEnd) start = previousEnd;           // never before the previous cue ends
            if (end < start + shortest) end = start + shortest;
            TimeSpan? next = i + 1 < pieces.Count ? pieces[i + 1].Start + offset : null;
            if (end - start < o.MinDuration) end = next is { } n && n > start + shortest ? Min(start + o.MinDuration, n) : start + o.MinDuration;
            if (next is { } after && end > after && after >= start + shortest) end = after;
            cues.Add(new SubtitleCue { Index = cues.Count + 1, Start = start, End = end, Text = Wrap(text, o.MaxLineColumns) });
            previousEnd = end;
        }
        return new ShapeResult(cues, droppedNotSpeech, droppedRepeats, droppedPhrases);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>The longest a cue with this text stays up: reading time, within the minimum and maximum duration.</summary>
    public static TimeSpan HoldFor(string text, CueShapeOptions o)
    {
        var t = o.HoldBase + TimeSpan.FromTicks(o.HoldPerColumn.Ticks * Columns(text));
        return t < o.MinDuration ? o.MinDuration : t > o.MaxDuration ? o.MaxDuration : t;
    }

    private static bool SameRun(List<string> keys, int a, int b, int length)
    {
        for (int k = 0; k < length; k++) if (keys[a + k] != keys[b + k]) return false;
        return true;
    }

    /// <summary>
    /// Lines Whisper is known to invent over music and silence, learned from the end of online videos
    /// and from caption credits: "see you in the next video", "thanks for watching", "subtitles by ...".
    /// Compared as letters and digits only. Everyday words on their own ("감사합니다", "Thank you") are
    /// not on the list: those are real lines too.
    /// </summary>
    private static readonly HashSet<string> KnownPhrases = new(StringComparer.Ordinal)
    {
        // Korean
        "다음영상에서만나요", "다음영상에서뵙겠습니다", "다음시간에만나요", "시청해주셔서감사합니다", "오늘도시청해주셔서감사합니다",
        "영상시청해주셔서감사합니다", "끝까지시청해주셔서감사합니다", "구독과좋아요부탁드립니다", "구독과좋아요", "구독좋아요알림설정",
        "구독과좋아요알림설정부탁드립니다", "좋아요와구독부탁드립니다", "구독부탁드립니다",
        // English
        "thankyouforwatching", "thanksforwatching", "thankyouforwatchingpleasesubscribe", "pleasesubscribe", "likeandsubscribe",
        "pleaselikeandsubscribe", "seeyouinthenextvideo", "seeyounexttime", "dontforgettolikeandsubscribe",
        // Japanese
        "ご視聴ありがとうございました", "ご視聴ありがとうございます", "チャンネル登録お願いします", "チャンネル登録よろしくお願いします",
        // Chinese
        "谢谢观看", "感谢观看", "谢谢大家观看", "请不吝点赞订阅转发打赏支持明镜与点点栏目", "字幕由amaraorg社区提供",
    };

    /// <summary>Caption credits: "한글자막 by ...", "Subtitles by the Amara.org community", "자막 제공: ...".</summary>
    private static readonly string[] CreditStarts = { "한글자막", "자막제공", "자막제작", "자막by", "subtitlesby", "captionsby", "subtitledby", "transcriptionby", "字幕由", "字幕制作" };

    private static bool IsKnownPhrase(string key, bool acrossSegments)
    {
        if (key.Length == 0) return false;
        if (KnownPhrases.Contains(key)) return true;
        if (acrossSegments) return false; // credits are matched within one segment only
        return key.Contains("amaraorg", StringComparison.Ordinal) || CreditStarts.Any(c => key.StartsWith(c, StringComparison.Ordinal))
               || (key.Contains("자막", StringComparison.Ordinal) && key.Contains("by", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whisper often lets a line run on into the pause after it (or start early). When the start or the
    /// end of a cue has <see cref="CueShapeOptions.SilenceTrim"/> or more without sound, that part is cut
    /// off, keeping a short lead-in and hold. A cue with no sound at all is left as it is.
    /// </summary>
    private static (TimeSpan, TimeSpan, string) TrimToSound((TimeSpan Start, TimeSpan End, string Text) piece, bool[] active, CueShapeOptions o)
    {
        int a = Math.Clamp((int)(piece.Start.TotalMilliseconds / 10), 0, active.Length);
        int b = Math.Clamp((int)Math.Ceiling(piece.End.TotalMilliseconds / 10), 0, active.Length);
        int first = a, last = b - 1;
        while (first < b && !active[first]) first++;
        while (last >= a && !active[last]) last--;
        if (first >= b) return piece;

        int trim = (int)(o.SilenceTrim.TotalMilliseconds / 10);
        const int edgeBurst = 100; // 1 s

        // A short burst of sound right at the end, after a long silence, is the next line starting: end before it.
        int runStart = last;
        while (runStart > a && active[runStart - 1]) runStart--;
        if (last - runStart < edgeBurst && runStart > first)
        {
            int before = runStart - 1;
            while (before >= first && !active[before]) before--;
            if (runStart - 1 - before >= trim) last = before;
        }
        // Likewise a short burst at the start, then a long silence, is the previous line ending.
        int runEnd = first;
        while (runEnd < b - 1 && active[runEnd + 1]) runEnd++;
        if (runEnd - first < edgeBurst && runEnd < last)
        {
            int after = runEnd + 1;
            while (after <= last && !active[after]) after++;
            if (after - runEnd - 1 >= trim) first = after;
        }

        var start = piece.Start;
        var end = piece.End;
        if (first - a >= trim) start = TimeSpan.FromMilliseconds(Math.Max(a, first - 15) * 10.0);
        if (b - 1 - last >= trim) end = TimeSpan.FromMilliseconds(Math.Min(b, last + 1 + 30) * 10.0);
        return (start, end, piece.Text);
    }

    /// <summary>Whitespace collapsed to single spaces, trimmed.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Key(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Display width: characters of East Asian scripts are two columns wide.</summary>
    public static int Columns(string text, int from = 0, int to = -1)
    {
        if (to < 0) to = text.Length;
        int n = 0;
        for (int i = from; i < to; i++) n += IsWide(text[i]) ? 2 : 1;
        return n;
    }

    private static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᇿ'      // Hangul Jamo
          or >= '⺀' and <= '鿿'      // CJK radicals, kana, CJK ideographs
          or >= '가' and <= '힣'      // Hangul syllables
          or >= '豈' and <= '﫿'      // CJK compatibility ideographs
          or >= '＀' and <= '｠'      // full-width forms
          or >= '㄰' and <= '㆏';     // Hangul compatibility Jamo

    /// <summary>
    /// Time at a character position of the cleaned text. From the tokens' own times when the tokens spell
    /// exactly that text (not always: a Korean syllable can be split across two tokens, garbling both);
    /// otherwise shared out by text width across the segment.
    /// </summary>
    private static Func<int, TimeSpan> TimeMap(WhisperSegment segment, string text, bool[]? active, CueShapeOptions o)
    {
        TimeSpan start = segment.Start, end = segment.End > segment.Start ? segment.End : segment.Start + TimeSpan.FromSeconds(1);
        int total = Math.Max(1, Columns(text));
        var paced = PacedMap(start, end, text, total, active, o);
        TimeSpan Proportional(int i) => paced(Columns(text, 0, i) / (double)total);

        var words = segment.Tokens.Where(t => !t.Special).ToList();
        if (words.Count == 0) return Proportional;
        var joined = string.Concat(words.Select(t => t.Text));
        if (joined.Trim() != text || joined.Contains('�')) return Proportional;

        // Token times must be usable: inside the segment (with a little slack) and not all zero.
        var slack = TimeSpan.FromSeconds(0.5);
        if (words.All(t => t.Start == TimeSpan.Zero && t.End == TimeSpan.Zero)
            || words.Any(t => t.Start < start - slack || t.Start > end + slack))
            return Proportional;

        int lead = joined.Length - joined.TrimStart().Length;
        var starts = new List<(int Char, TimeSpan Time)>(words.Count);
        int pos = -lead;
        foreach (var t in words)
        {
            // The token's first visible character (tokens usually start with the space before a word).
            int first = Math.Max(0, pos), last = Math.Min(text.Length, pos + t.Text.Length);
            while (first < last && text[first] == ' ') first++;
            starts.Add((first, t.Start));
            pos += t.Text.Length;
        }
        // The first word's time is often the segment's start, long before the word is said (Whisper's
        // segment began at the end of the previous line): a first word far ahead of the second is moved up.
        if (starts.Count >= 2 && starts[1].Time - starts[0].Time > LeadingGap)
            starts[0] = (starts[0].Char, starts[1].Time - TimeSpan.FromSeconds(0.3));
        var lastEnd = words[^1].End > words[^1].Start ? words[^1].End : words[^1].Start;
        return i =>
        {
            for (int k = 0; k < starts.Count; k++)
                if (starts[k].Char >= i) return Clamp(starts[k].Time, start, end);
            // The end of the text: where its last word ends.
            return i >= text.Length && lastEnd > starts[^1].Time ? Clamp(lastEnd, start, end) : Proportional(i);
        };
    }

    /// <summary>A first word this far ahead of the second isn't where it was said.</summary>
    private static readonly TimeSpan LeadingGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Time at a share of the text (0..1) when there are no usable word times: the text is laid over the
    /// speech heard from the segment's start (<paramref name="active"/>, 10 ms steps) at a speaking pace, not
    /// spread over everything the segment spans. A segment can span a minute of music with one short line
    /// in it; spreading put one syllable on each cue. Without speech information, the pace alone limits it.
    /// </summary>
    private static Func<double, TimeSpan> PacedMap(TimeSpan start, TimeSpan end, string text, int columns, bool[]? active, CueShapeOptions o)
    {
        var needed = TimeSpan.FromTicks(o.SpeechPerColumn.Ticks * columns) + TimeSpan.FromSeconds(0.3);
        var span = end - start;
        // Short enough for the text: spread evenly, as Whisper gave it.
        if (span <= needed + needed / 2) return f => start + TimeSpan.FromTicks((long)(span.Ticks * f));

        if (active is null)
        {
            var limit = needed + needed / 2;
            return f => start + TimeSpan.FromTicks((long)(limit.Ticks * f));
        }

        // The speech frames from the start on, until the text has had its time (or the segment ends).
        int a = Math.Clamp((int)(start.TotalMilliseconds / 10), 0, active.Length), b = Math.Clamp((int)Math.Ceiling(end.TotalMilliseconds / 10), 0, active.Length);
        int want = Math.Max(1, (int)(needed.TotalMilliseconds / 10));
        var frames = new List<int>(want);
        int gap = 0, pause = Math.Max(1, (int)(o.SilenceTrim.TotalMilliseconds / 10));
        for (int f = a; f < b && frames.Count < want; f++)
        {
            if (active[f]) { frames.Add(f); gap = 0; continue; }
            // A long pause once most of the text has had its time: the line ended (what follows is the next one).
            if (frames.Count > 0 && ++gap >= pause && frames.Count >= want / 2) break;
        }
        if (frames.Count == 0)
        {
            var limit = needed + needed / 2;
            return f => start + TimeSpan.FromTicks((long)(limit.Ticks * f));
        }
        return f =>
        {
            if (f <= 0) return TimeSpan.FromMilliseconds(frames[0] * 10.0);
            if (f >= 1) return TimeSpan.FromMilliseconds((frames[^1] + 1) * 10.0);
            return TimeSpan.FromMilliseconds(frames[Math.Min(frames.Count - 1, (int)(f * frames.Count))] * 10.0);
        };
    }

    private static TimeSpan Clamp(TimeSpan t, TimeSpan lo, TimeSpan hi) => t < lo ? lo : t > hi ? hi : t;

    /// <summary>Splits text[from..to) until every piece fits two lines and the longest duration.</summary>
    private static void Split(string text, int from, int to, Func<int, TimeSpan> timeAt, CueShapeOptions o, List<(TimeSpan, TimeSpan, string)> output)
    {
        while (from < to && text[from] == ' ') from++;
        while (to > from && text[to - 1] == ' ') to--;
        if (from >= to) return;

        TimeSpan start = timeAt(from), end = timeAt(to);
        bool tooWide = !FitsLines(text[from..to], o);
        bool tooLong = end - start > o.MaxDuration;
        // Too wide: split where needed (between characters in Chinese or Japanese). Only too long: only
        // between words, and not into scraps: a short line stays whole and is shortened in time instead.
        int cut = tooWide ? BestBreak(text, from, to)
            : tooLong && Columns(text, from, to) >= 2 * MinTimeSplitColumns ? BestBreak(text, from, to, wordsOnly: true) : -1;
        if (cut < 0)
        {
            output.Add((start, end, text[from..to]));
            return;
        }
        Split(text, from, cut, timeAt, o, output);
        Split(text, cut, to, timeAt, o, output);
    }

    /// <summary>Where to split text[from..to): near the middle, preferring sentence ends, then commas. -1: nowhere.</summary>
    /// <summary>A split made only for time leaves at least this many columns on each side.</summary>
    private const int MinTimeSplitColumns = 8;

    private static int BestBreak(string text, int from, int to, bool wordsOnly = false)
    {
        int total = Math.Max(1, Columns(text, from, to));
        bool hasSpace = text.IndexOf(' ', from, to - from) >= 0;
        // Without spaces, only East Asian text may be split between characters; a single word never is.
        if (!hasSpace && (wordsOnly || !HasWide(text, from, to))) return -1;
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = from + 1; i < to; i++)
        {
            bool candidate = hasSpace ? text[i] == ' ' : !char.IsPunctuation(text[i]);
            if (!candidate) continue;
            int left = i;
            while (left > from && text[left - 1] == ' ') left--;
            if (left <= from) continue;
            if (!HasWord(text, from, left) || !HasWord(text, i, to)) continue;
            if (wordsOnly && (Columns(text, from, left) < MinTimeSplitColumns || Columns(text, i, to) < MinTimeSplitColumns)) continue;
            double f = Columns(text, from, i) / (double)total;
            char before = text[left - 1];
            double bonus = StrongEnds.IndexOf(before) >= 0 ? 0.3 : SoftEnds.IndexOf(before) >= 0 ? 0.15 : 0;
            double score = Math.Abs(f - 0.5) - bonus;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    private static bool HasWide(string text, int from, int to)
    {
        for (int i = from; i < to; i++) if (IsWide(text[i])) return true;
        return false;
    }

    private static bool HasWord(string text, int from, int to)
    {
        for (int i = from; i < to; i++) if (char.IsLetterOrDigit(text[i])) return true;
        return false;
    }

    /// <summary>Whether the text wraps into at most MaxLines lines that each fit the width.</summary>
    private static bool FitsLines(string text, CueShapeOptions o)
    {
        if (Columns(text) > o.MaxLineColumns * o.MaxLines) return false;
        var lines = Wrap(text, o.MaxLineColumns).Split('\n');
        return lines.Length <= o.MaxLines && lines.All(l => Columns(l) <= o.MaxLineColumns);
    }

    /// <summary>Two balanced lines when the text is wider than one line.</summary>
    public static string Wrap(string text, int maxColumns)
    {
        if (Columns(text) <= maxColumns) return text;
        bool hasSpace = text.IndexOf(' ') >= 0;
        int best = -1;
        double bestScore = double.MaxValue;
        int total = Columns(text);
        for (int i = 1; i < text.Length; i++)
        {
            if (hasSpace ? text[i] != ' ' : char.IsPunctuation(text[i])) continue;
            int left = Columns(text, 0, i), right = total - left - (hasSpace ? 1 : 0);
            char before = text[i - 1];
            double bonus = StrongEnds.IndexOf(before) >= 0 || SoftEnds.IndexOf(before) >= 0 ? 4 : 0;
            double score = Math.Max(left, right) - bonus;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return text;
        return text[..best].TrimEnd() + "\n" + text[best..].TrimStart();
    }
}
