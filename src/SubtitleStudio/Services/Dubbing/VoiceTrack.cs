using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>A line whose speech takes longer than the time it has before the next line.</summary>
public sealed record LongLine(int Number, TimeSpan Speech, TimeSpan Slot)
{
    public TimeSpan Over => Speech - Slot;
}

/// <summary>What making a voice track did.</summary>
/// <param name="SpedUp">Lines Kokoro said faster to fit their time (and nothing more).</param>
/// <param name="Stretched">Lines said faster and also made shorter afterwards (pitch kept).</param>
/// <param name="TooLong">Lines still longer than their time: the next line waits for them.</param>
public sealed record VoiceTrackResult(int Spoken, int Silent, IReadOnlyList<LongLine> TooLong, TimeSpan Length, TimeSpan LongestDelay, TimeSpan SpeechTime,
    int SpedUp = 0, int Stretched = 0);

/// <param name="Speed">The chosen speaking speed (1 = normal).</param>
/// <param name="Fit">Make lines that are too long fit their time (faster speech, then a little time-stretch).</param>
/// <param name="Length">The video's length, so the track is as long as it (null: ends after the last line).</param>
/// <param name="Concurrency">Lines spoken at the same time (they are still placed in order).</param>
public sealed record VoiceTrackOptions(float Speed = 1f, bool Fit = true, TimeSpan? Length = null, int Concurrency = 1);

/// <summary>How a line was made to fit.</summary>
public enum FitKind { Natural, SpedUp, Stretched, TooLong }

public sealed record FittedLine(float[] Samples, float Speed, double Stretch, FitKind Kind);

/// <summary>
/// Makes a line's speech fit the time before the next line. First Kokoro says it faster (up to 30% faster
/// than the chosen speed): that sounds like someone speaking quickly, not like a sped-up recording. If
/// it's still too long, it is made up to 15% shorter with pitch kept (TimeStretch). A line that needs more
/// than that stays as long as it is and is reported.
/// </summary>
public static class LineFitter
{
    public const double MaxSpeedUp = 1.3;
    public const double MaxStretch = 1.15;

    /// <summary>Lines with less time than this aren't squeezed (two subtitles at the same moment): they're reported.</summary>
    public const double ShortestSlot = 0.3;

    /// <param name="speak">Speech at a given Kokoro speed (24 kHz samples).</param>
    /// <param name="slot">Seconds the line has.</param>
    public static FittedLine Fit(Func<float, float[]> speak, float speed, double slot)
    {
        const double rate = KokoroTts.SampleRate;
        var natural = speak(speed);
        double length = natural.Length / rate;
        if (natural.Length == 0 || length <= slot) return new FittedLine(natural, speed, 1, FitKind.Natural);
        if (slot < ShortestSlot) return new FittedLine(natural, speed, 1, FitKind.TooLong);

        // Kokoro's length isn't exactly inversely proportional to its speed: aim 3% short.
        double needed = length / slot * 1.03;
        float faster = (float)Math.Min(speed * Math.Min(needed, MaxSpeedUp), 2.0);
        var quick = faster > speed + 0.01f ? speak(faster) : natural;
        if (quick.Length == 0) return new FittedLine(natural, speed, 1, FitKind.Natural);
        double quickLength = quick.Length / rate;
        if (quickLength <= slot) return new FittedLine(quick, faster, 1, FitKind.SpedUp);

        double stretch = quickLength / slot;
        if (stretch <= MaxStretch) return new FittedLine(TimeStretch.Faster(quick, stretch), faster, stretch, FitKind.Stretched);
        return new FittedLine(TimeStretch.Faster(quick, MaxStretch), faster, MaxStretch, FitKind.TooLong);
    }
}

/// <summary>
/// One English voice track from subtitles: each line is spoken and placed at the time its subtitle
/// appears, silence between. With fitting on, a line too long for the time before the next one is said
/// faster (LineFitter). A line that still runs into the next one's time pushes that one back until it
/// has finished (two lines are never spoken over each other), and it's reported. Written as a 24 kHz
/// WAV as it goes.
/// </summary>
public static class VoiceTrack
{
    /// <summary>Shortest pause kept between two lines that had to be pushed back.</summary>
    public static readonly TimeSpan MinPause = TimeSpan.FromMilliseconds(100);

    /// <summary>A line is fitted to end this much before the next one starts.</summary>
    public static readonly TimeSpan FitGap = TimeSpan.FromMilliseconds(80);

    /// <param name="speak">Speech for a line's text at a Kokoro speed (24 kHz samples; empty: nothing to say).</param>
    public static async Task<VoiceTrackResult> BuildAsync(IReadOnlyList<SubtitleCue> cues, Func<string, float, float[]> speak, VoiceTrackOptions options,
        string outputPath, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        const int rate = KokoroTts.SampleRate;
        var ordered = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start).ThenBy(c => c.Index).ToList();
        var tooLong = new List<LongLine>();
        int spoken = 0, silent = 0, spedUp = 0, stretched = 0;
        long longestDelay = 0, speech = 0;
        long minPause = (long)(MinPause.TotalSeconds * rate);

        // Time before the next line starts (to the end of the video for the last one).
        TimeSpan Slot(int i) => (i + 1 < ordered.Count ? ordered[i + 1].Start : options.Length ?? TimeSpan.MaxValue) - ordered[i].Start;

        var tmp = outputPath + ".part";
        VoiceTrackResult result;
        // Lines are spoken ahead, up to `Concurrency` at once, and written in order as each is ready.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ahead = new Queue<Task<FittedLine>>();
        int started = 0;
        void Fill()
        {
            while (ahead.Count < Math.Max(1, options.Concurrency) && started < ordered.Count)
            {
                int index = started++;
                var text = ordered[index].Text;
                var slot = Slot(index);
                ahead.Enqueue(Task.Run(() =>
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (!options.Fit || slot == TimeSpan.MaxValue)
                        return new FittedLine(speak(text, options.Speed), options.Speed, 1, FitKind.Natural);
                    return LineFitter.Fit(s => speak(text, s), options.Speed, (slot - FitGap).TotalSeconds);
                }, stop.Token));
            }
        }
        try
        {
            using (var wav = new WavWriter(tmp, rate))
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var cue = ordered[i];
                    Fill();
                    var line = await ahead.Dequeue().ConfigureAwait(false);
                    var samples = line.Samples;
                    progress?.Report(new EngineProgress((i + 1) / (double)ordered.Count, $"Line {i + 1} of {ordered.Count}", cue.Text));
                    if (samples.Length == 0) { silent++; continue; }
                    spoken++;
                    speech += samples.Length;
                    if (line.Stretch > 1) stretched++;
                    else if (line.Speed > options.Speed + 0.001f) spedUp++;

                    long at = (long)(cue.Start.TotalSeconds * rate);
                    long earliest = wav.SamplesWritten + (wav.SamplesWritten > 0 && at < wav.SamplesWritten ? minPause : 0);
                    long start = Math.Max(at, earliest);
                    longestDelay = Math.Max(longestDelay, start - at);
                    wav.WriteSilence(start - wav.SamplesWritten);
                    wav.Write(samples);

                    var slot = Slot(i);
                    var said = TimeSpan.FromSeconds(samples.Length / (double)rate);
                    if (said > slot) tooLong.Add(new LongLine(cue.Index, said, slot));
                }
                if (options.Length is { } total) wav.WriteSilence((long)(total.TotalSeconds * rate) - wav.SamplesWritten);
                result = new VoiceTrackResult(spoken, silent, tooLong, TimeSpan.FromSeconds(wav.SamplesWritten / (double)rate),
                    TimeSpan.FromSeconds(longestDelay / (double)rate), TimeSpan.FromSeconds(speech / (double)rate), spedUp, stretched);
            }
            File.Move(tmp, outputPath, overwrite: true);
            return result;
        }
        finally
        {
            // Stopped early (cancelled or failed): let the lines still being spoken finish before cleaning up.
            stop.Cancel();
            if (ahead.Count > 0) await Task.WhenAll(ahead).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
