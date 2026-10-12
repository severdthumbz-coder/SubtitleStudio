using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SubtitleStudio.Models;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>How loud the original voice was in a line, and on which side (from the separated voices).</summary>
/// <param name="Db">Level of the louder half of the line (dB below full scale), so pauses inside the line don't lower it.</param>
public sealed record LineLevel(double Db, double LeftRms, double RightRms);

/// <summary>
/// Each English line at the level and on the side of the original voice it replaces: a line spoken quietly or
/// off to one side (someone across the room, a voice off screen) stays quieter or to that side. Levels follow
/// the original's ups and downs around its usual speaking level, softened (80%) and kept within 12 dB below and
/// 3 dB above; the side is softened too (70%) and never more than 25° off centre, as the original mix rarely
/// puts speech hard to one side. Lines where no voice was found are at the usual level, in the centre.
/// </summary>
public sealed class VoicePlacement
{
    public const double Follow = 0.8, MostBelow = 12, MostAbove = 3, PanFollow = 0.7, MostPanDegrees = 25;

    /// <summary>A line this far below the usual level had no voice found (subtitle timing off, or a whisper lost in music).</summary>
    public const double NoVoiceBelow = 30;

    private readonly IReadOnlyDictionary<int, LineLevel> _levels;

    public VoicePlacement(IReadOnlyDictionary<int, LineLevel> levels)
    {
        _levels = levels;
        var heard = levels.Values.Where(l => l.Db > -70).Select(l => l.Db).OrderBy(d => d).ToList();
        Usual = heard.Count > 0 ? heard[heard.Count / 2] : -24;
    }

    /// <summary>The usual (median) level of the original voices, dB.</summary>
    public double Usual { get; }

    /// <summary>Lines measured.</summary>
    public int Count => _levels.Count;

    /// <summary>The level an English line should have and its side (radians, π/4 = centre), by the line's number.</summary>
    public (double Db, double Angle) Target(int index)
    {
        if (!_levels.TryGetValue(index, out var level) || level.Db < Usual - NoVoiceBelow) return (Usual, Math.PI / 4);
        double db = Usual + Math.Clamp(Follow * (level.Db - Usual), -MostBelow, MostAbove);
        double angle = Math.Atan2(level.RightRms, level.LeftRms);
        double most = MostPanDegrees * Math.PI / 180;
        angle = Math.PI / 4 + Math.Clamp(PanFollow * (angle - Math.PI / 4), -most, most);
        return (db, angle);
    }

    /// <summary>Gains for a line's speech, left and right (equal power: in the centre both are the gain itself).</summary>
    public (float Left, float Right) Gains(SubtitleCue cue, float[] speech)
    {
        var (db, angle) = Target(cue.Index);
        double own = DubBed.ActiveDb(speech, speech, KokoroTts.SampleRate);
        double gainDb = own < -90 ? 0 : Math.Clamp(db - own, -24, 12);
        double g = Math.Pow(10, gainDb / 20);
        return ((float)(g * Math.Cos(angle) * Math.Sqrt(2)), (float)(g * Math.Sin(angle) * Math.Sqrt(2)));
    }

    /// <summary>"usual level −22 dB; 14 lines quieter by 6 dB or more, 9 off to one side".</summary>
    public string Describe()
    {
        int quiet = 0, side = 0;
        foreach (var index in _levels.Keys)
        {
            var (db, angle) = Target(index);
            if (db <= Usual - 6) quiet++;
            if (Math.Abs(angle - Math.PI / 4) >= 10 * Math.PI / 180) side++;
        }
        return $"the original voices' usual level {Usual:0} dB; {quiet} line{(quiet == 1 ? "" : "s")} quieter by 6 dB or more, {side} off to one side";
    }
}

/// <summary>
/// The sound under the English voices when the original voices are removed: the voices are separated from the
/// original sound (VoiceSeparator, kept for the episode in the temporary folder, as it takes minutes), and taken
/// out only where an English line replaces them, with short fades. Elsewhere the original sound is untouched:
/// laughs, gasps, background voices with no subtitle and songs stay as they were, and the separation can't
/// colour the music between lines.
/// </summary>
public static class DubBed
{
    public const int SampleRate = 44100;

    /// <summary>The voices are taken out from this long before an English line's time to this long after.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMilliseconds(120), After = TimeSpan.FromMilliseconds(200);

    /// <summary>Fades in and out of the removal (no clicks).</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(40);

    /// <summary>Lines closer than this are taken out together (the voices don't come back for a moment between them).</summary>
    public static readonly TimeSpan Bridge = TimeSpan.FromMilliseconds(400);

    /// <summary>Separated voices kept: the most recent episodes (about 0.7 GB an hour each).</summary>
    public const int Kept = 2;

    public static string CacheFolder => Path.Combine(Path.GetTempPath(), "SubtitleStudio", "separated");

    /// <summary>The separated voices of a video's audio track, by the file (path, size, date), the track and the model.</summary>
    public static string CachePath(string video, int track, string modelFile)
    {
        var info = new FileInfo(video);
        var key = $"{Path.GetFullPath(video).ToLowerInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{track}|{Path.GetFileName(modelFile)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var name = string.Concat(Path.GetFileNameWithoutExtension(video).Take(40).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(CacheFolder, $"{name}.{hash}.voices.wav");
    }

    /// <summary>Removes all but the newest separated voices (and any left half-made).</summary>
    public static void TrimCache(string keep)
    {
        try
        {
            if (!Directory.Exists(CacheFolder)) return;
            var files = new DirectoryInfo(CacheFolder).GetFiles("*.voices.wav").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Skip(Kept).Where(f => !string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)))
                try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var f in new DirectoryInfo(CacheFolder).GetFiles("*.part"))
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-12))
                    try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>FFmpeg's arguments for an audio track (or a stretch of it) as 44.1 kHz stereo floats on its output.</summary>
    public static IReadOnlyList<string> DecodeArgs(string video, int track, TimeSpan? start = null, TimeSpan? length = null)
    {
        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (start is { } s && s > TimeSpan.Zero) args.AddRange(new[] { "-ss", Seconds(s) });
        args.AddRange(new[] { "-i", video });
        if (length is { } l) args.AddRange(new[] { "-t", Seconds(l) });
        args.AddRange(new[] { "-map", $"0:a:{track}", "-vn", "-sn", "-dn", "-ac", "2", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" });
        return args;
    }

    /// <summary>Decodes with FFmpeg and hands over the sound in blocks of stereo frames as it comes.</summary>
    public static async Task DecodeAsync(string ffmpeg, IReadOnlyList<string> args, Action<float[], float[]> block, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var bytes = new byte[1 << 20];
        int carry = 0, read;
        var output = process.StandardOutput.BaseStream;
        while ((read = await output.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), ct).ConfigureAwait(false)) > 0)
        {
            int total = carry + read, frames = total / 8;
            if (frames > 0)
            {
                var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, frames * 8));
                var l = new float[frames];
                var r = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    l[i] = f[2 * i];
                    r[i] = f[2 * i + 1];
                }
                block(l, r);
            }
            carry = total - frames * 8;
            if (carry > 0) Buffer.BlockCopy(bytes, frames * 8, bytes, 0, carry);
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {process.ExitCode}";
            throw new InvalidOperationException($"FFmpeg couldn't read the sound: {last}");
        }
    }

    /// <summary>A stretch of the sound in memory (a preview).</summary>
    public static async Task<(float[] Left, float[] Right)> ReadAsync(string ffmpeg, string video, int track, TimeSpan start, TimeSpan length, CancellationToken ct)
    {
        var left = new List<float>((int)(length.TotalSeconds * SampleRate) + SampleRate);
        var right = new List<float>(left.Capacity);
        await DecodeAsync(ffmpeg, DecodeArgs(video, track, start, length), (l, r) => { left.AddRange(l); right.AddRange(r); }, ct).ConfigureAwait(false);
        return (left.ToArray(), right.ToArray());
    }

    /// <summary>
    /// Separates the voices of a whole audio track into a WAV (44.1 kHz stereo), written under a temporary name and
    /// renamed when complete. <paramref name="progress"/>: the share of <paramref name="expected"/> done.
    /// </summary>
    public static async Task SeparateToFileAsync(string ffmpeg, string video, int track, VoiceSeparator separator, string voicesPath,
        TimeSpan? expected, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(voicesPath)!);
        var part = voicesPath + ".part";
        long total = expected is { } e && e > TimeSpan.Zero ? (long)(e.TotalSeconds * SampleRate) : 0;
        try
        {
            using (var wav = new WavWriter(part, SampleRate, 2))
            {
                var stream = new VoiceSeparator.Stream(separator);
                long input = 0;
                void Drain()
                {
                    while (stream.TryTake(out var l, out var r))
                    {
                        int n = (int)Math.Min(l.Length, input - wav.SamplesWritten);
                        if (n <= 0) continue;
                        wav.WriteStereo(l.AsSpan(0, n), r.AsSpan(0, n));
                        if (total > 0) progress?.Report(Math.Min(1, wav.SamplesWritten / (double)total));
                    }
                }
                // The model's work happens in Feed, on this thread, while FFmpeg keeps decoding ahead into its pipe.
                await DecodeAsync(ffmpeg, DecodeArgs(video, track), (l, r) =>
                {
                    ct.ThrowIfCancellationRequested();
                    input += l.Length;
                    stream.Feed(l, r);
                    Drain();
                }, ct).ConfigureAwait(false);
                stream.Finish();
                Drain();
                if (input == 0) throw new InvalidOperationException("No sound came out of the video's audio track.");
            }
            File.Move(part, voicesPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The level of the louder half of a stretch (20 ms frames), dB below full scale, both sides together; −120 for
    /// silence. The same measure for the original voice and the English line, so pauses don't skew either.
    /// </summary>
    public static double ActiveDb(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int rate)
    {
        int frame = Math.Max(1, rate / 50), count = left.Length / frame;
        if (count == 0) count = left.Length > 0 ? 1 : 0;
        if (count == 0) return -120;
        var powers = new double[count];
        for (int f = 0; f < count; f++)
        {
            int start = f * frame, end = Math.Min(left.Length, start + frame);
            double sum = 0;
            for (int i = start; i < end; i++) sum += (left[i] * (double)left[i] + right[i] * (double)right[i]) / 2;
            powers[f] = sum / Math.Max(1, end - start);
        }
        Array.Sort(powers);
        double loud = 0;
        int from = count / 2;
        for (int f = from; f < count; f++) loud += powers[f];
        loud /= count - from;
        return loud <= 1e-12 ? -120 : 10 * Math.Log10(loud);
    }

    /// <summary>The original voice's level and side in one line, from the separated voices (at most its first 10 s).</summary>
    public static LineLevel Measure(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double l = 0, r = 0;
        for (int i = 0; i < left.Length; i++)
        {
            l += left[i] * (double)left[i];
            r += right[i] * (double)right[i];
        }
        int n = Math.Max(1, left.Length);
        return new LineLevel(ActiveDb(left, right, SampleRate), Math.Sqrt(l / n), Math.Sqrt(r / n));
    }

    /// <summary>Each line's original voice, from a separated-voices file (the episode's).</summary>
    public static Dictionary<int, LineLevel> MeasureLines(string voicesPath, IEnumerable<SubtitleCue> lines)
    {
        using var wav = new WavReader(voicesPath);
        var result = new Dictionary<int, LineLevel>();
        foreach (var c in lines.Where(c => c.End > c.Start))
        {
            long start = (long)(c.Start.TotalSeconds * wav.SampleRate);
            int count = (int)(Math.Min((c.End - c.Start).TotalSeconds, 10) * wav.SampleRate);
            if (count <= 0 || start >= wav.Frames) continue;
            var l = new float[count];
            var r = new float[count];
            wav.Read(start, l, r);
            result[c.Index] = Measure(l, r);
        }
        return result;
    }

    /// <summary>Each line's original voice, from separated voices in memory that start at <paramref name="offset"/> (a preview).</summary>
    public static Dictionary<int, LineLevel> MeasureLines(float[] left, float[] right, TimeSpan offset, IEnumerable<SubtitleCue> lines)
    {
        var result = new Dictionary<int, LineLevel>();
        foreach (var c in lines.Where(c => c.End > c.Start))
        {
            int start = (int)((c.Start - offset).TotalSeconds * SampleRate);
            int end = (int)Math.Min((c.End - offset).TotalSeconds * SampleRate, start + 10.0 * SampleRate);
            start = Math.Max(0, start);
            end = Math.Min(left.Length, end);
            if (end <= start) continue;
            result[c.Index] = Measure(left.AsSpan(start, end - start), right.AsSpan(start, end - start));
        }
        return result;
    }

    /// <summary>
    /// How much of the original voices is taken out at each moment: 1 where an English line replaces them (with
    /// the margins and bridges above), 0 elsewhere, with fades. <paramref name="covered"/> in time order is not required.
    /// </summary>
    public sealed class Removal
    {
        private readonly List<(long Start, long End)> _spans = new();
        private readonly long _fade;
        private int _at;

        /// <param name="activity">
        /// Where the original voices are speaking (from the separated voices, in the same timeline as the lines less
        /// <paramref name="offset"/>): a removal is stretched to cover a voice that starts before the line's subtitle,
        /// or carries on after it (at most GrowBefore and GrowAfter). Null: the subtitle times and margins alone.
        /// </param>
        public Removal(IEnumerable<(TimeSpan Start, TimeSpan End)> covered, TimeSpan offset, int rate = SampleRate, VoiceActivity? activity = null)
        {
            _fade = Math.Max(1, (long)(Fade.TotalSeconds * rate));
            long bridge = (long)(Bridge.TotalSeconds * rate);
            foreach (var (s, e) in covered.OrderBy(c => c.Start))
            {
                var from = s - Before - offset;
                var to = e + After - offset;
                if (activity is not null)
                {
                    var (grownFrom, grownTo) = activity.Grow(from, to);
                    if (grownFrom < from) { Extended++; ExtendedBefore += from - grownFrom; }
                    if (grownTo > to) { Extended++; ExtendedAfter += grownTo - to; }
                    from = grownFrom;
                    to = grownTo;
                }
                long start = (long)(from.TotalSeconds * rate), end = (long)(to.TotalSeconds * rate);
                if (end <= start) continue;
                if (_spans.Count > 0 && start - _spans[^1].End <= bridge) _spans[^1] = (_spans[^1].Start, Math.Max(_spans[^1].End, end));
                else _spans.Add((start, end));
            }
        }

        public IReadOnlyList<(long Start, long End)> Spans => _spans;

        /// <summary>Line ends stretched to the original voice (before or after), and by how much in all.</summary>
        public int Extended { get; private set; }
        public TimeSpan ExtendedBefore { get; private set; }
        public TimeSpan ExtendedAfter { get; private set; }

        /// <summary>The share of the voices removed at a frame. Frames must be asked in increasing order (it moves forward).</summary>
        public float At(long frame)
        {
            while (_at < _spans.Count && _spans[_at].End + _fade <= frame) _at++;
            if (_at >= _spans.Count) return 0;
            var (start, end) = _spans[_at];
            if (frame < start - _fade) return 0;
            if (frame < start) return 1 - (start - frame) / (float)_fade;
            if (frame <= end) return 1;
            return 1 - (frame - end) / (float)_fade;
        }
    }

    /// <summary>
    /// Where the separated voices are speaking, in 10 ms steps: louder than the usual speaking level less 20 dB,
    /// with dips under 80 ms bridged (the gaps between syllables). Lets the removal follow the voice rather than the
    /// subtitle times: Whisper's subtitles often start after the speaker does (a breath, an "uh", a quiet first
    /// word), and on E03 (1.0.0.67) those first moments of Korean came through.
    /// </summary>
    public sealed class VoiceActivity
    {
        public const int PerSecond = 100;
        public const double BelowUsual = 20;

        /// <summary>The furthest a removal is stretched before a line, and after it.</summary>
        public static readonly TimeSpan GrowBefore = TimeSpan.FromSeconds(1.5), GrowAfter = TimeSpan.FromSeconds(1.0);

        private readonly bool[] _active;

        /// <param name="levels">Level of each 10 ms step, dB (Levels).</param>
        /// <param name="usual">The usual level of the voices in lines (VoicePlacement.Usual).</param>
        public VoiceActivity(float[] levels, double usual)
        {
            double threshold = Math.Max(usual - BelowUsual, -60);
            var raw = levels.Select(l => l > threshold).ToArray();
            _active = (bool[])raw.Clone();
            // Bridge short dips: inactive runs under 80 ms between active steps.
            const int gap = 8;
            int last = -1;
            for (int i = 0; i < raw.Length; i++)
            {
                if (!raw[i]) continue;
                if (last >= 0 && i - last > 1 && i - last <= gap) for (int k = last + 1; k < i; k++) _active[k] = true;
                last = i;
            }
        }

        public bool Active(TimeSpan t)
        {
            long i = (long)Math.Floor(t.TotalSeconds * PerSecond);
            return i >= 0 && i < _active.Length && _active[i];
        }

        /// <summary>A removal span stretched over the voice that runs into it at either end (at most GrowBefore and GrowAfter).</summary>
        public (TimeSpan From, TimeSpan To) Grow(TimeSpan from, TimeSpan to)
        {
            var step = TimeSpan.FromSeconds(1.0 / PerSecond);
            var f = from;
            while (from - f < GrowBefore && Active(f - step)) f -= step;
            var t = to;
            while (t - to < GrowAfter && Active(t)) t += step;
            return (f, t);
        }

        /// <summary>The level of each 10 ms step of stereo sound, dB (−120 for silence).</summary>
        public static float[] Levels(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int rate = SampleRate)
        {
            int step = rate / PerSecond, count = left.Length / step;
            var levels = new float[count];
            for (int f = 0; f < count; f++)
            {
                double sum = 0;
                for (int i = f * step; i < (f + 1) * step; i++) sum += (left[i] * (double)left[i] + right[i] * (double)right[i]) / 2;
                double p = sum / step;
                levels[f] = p <= 1e-12 ? -120 : (float)(10 * Math.Log10(p));
            }
            return levels;
        }

        /// <summary>The levels of a separated-voices file (an episode: one pass, a few seconds).</summary>
        public static float[] Levels(string voicesPath)
        {
            using var wav = new WavReader(voicesPath);
            int step = wav.SampleRate / PerSecond;
            var levels = new float[(int)(wav.Frames / step)];
            const int chunk = PerSecond * 60; // a minute at a time
            var l = new float[chunk * step];
            var r = new float[chunk * step];
            for (int f = 0; f < levels.Length; f += chunk)
            {
                int n = Math.Min(chunk, levels.Length - f);
                wav.Read((long)f * step, l.AsSpan(0, n * step), r.AsSpan(0, n * step));
                Levels(l.AsSpan(0, n * step), r.AsSpan(0, n * step), wav.SampleRate).CopyTo(levels, f);
            }
            return levels;
        }
    }

    /// <summary>The sound under the English voices, in memory (a preview): the original less the voices where they're replaced.</summary>
    public static (float[] Left, float[] Right) Bed(float[] mixLeft, float[] mixRight, float[] voicesLeft, float[] voicesRight, Removal removal)
    {
        var l = new float[mixLeft.Length];
        var r = new float[mixRight.Length];
        for (int i = 0; i < l.Length; i++)
        {
            float m = removal.At(i);
            l[i] = mixLeft[i] - m * (i < voicesLeft.Length ? voicesLeft[i] : 0);
            r[i] = mixRight[i] - m * (i < voicesRight.Length ? voicesRight[i] : 0);
        }
        return (l, r);
    }

    /// <summary>
    /// The sound under the English voices for the whole video, into a WAV (44.1 kHz stereo): the original decoded
    /// again, with the separated voices taken out where an English line replaces them.
    /// </summary>
    public static async Task WriteBedAsync(string ffmpeg, string video, int track, string voicesPath, Removal removal, string bedPath,
        TimeSpan? expected, IProgress<double>? progress, CancellationToken ct)
    {
        long total = expected is { } e && e > TimeSpan.Zero ? (long)(e.TotalSeconds * SampleRate) : 0;
        using var voices = new WavReader(voicesPath);
        using var wav = new WavWriter(bedPath, SampleRate, 2);
        long at = 0;
        await DecodeAsync(ffmpeg, DecodeArgs(video, track), (l, r) =>
        {
            ct.ThrowIfCancellationRequested();
            var vl = new float[l.Length];
            var vr = new float[l.Length];
            voices.Read(at, vl, vr);
            for (int i = 0; i < l.Length; i++)
            {
                float m = removal.At(at + i);
                if (m <= 0) continue;
                l[i] -= m * vl[i];
                r[i] -= m * vr[i];
            }
            wav.WriteStereo(l, r);
            at += l.Length;
            if (total > 0) progress?.Report(Math.Min(1, at / (double)total));
        }, ct).ConfigureAwait(false);
    }
}
