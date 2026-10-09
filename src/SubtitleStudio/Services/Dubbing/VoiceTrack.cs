using SubtitleStudio.Models;
using SubtitleStudio.Services.Abstractions;

namespace SubtitleStudio.Services.Dubbing;

/// <summary>A line whose speech takes longer than the time it has before the next line.</summary>
public sealed record LongLine(int Number, TimeSpan Speech, TimeSpan Slot)
{
    public TimeSpan Over => Speech - Slot;
}

/// <summary>What making a voice track did.</summary>
public sealed record VoiceTrackResult(int Spoken, int Silent, IReadOnlyList<LongLine> TooLong, TimeSpan Length, TimeSpan LongestDelay, TimeSpan SpeechTime);

/// <summary>
/// One English voice track from subtitles: each line is spoken and placed at the time its subtitle
/// appears, silence between. A line that runs into the next one's time pushes that one back until it
/// has finished (two lines are never spoken over each other), and it's reported, so it can be shortened
/// (fitting speech to the time comes in the next build). Written as a 24 kHz WAV as it goes.
/// </summary>
public static class VoiceTrack
{
    /// <summary>Shortest pause kept between two lines that had to be pushed back.</summary>
    public static readonly TimeSpan MinPause = TimeSpan.FromMilliseconds(100);

    /// <param name="speak">Speech for a line's text (24 kHz samples; empty: nothing to say).</param>
    /// <param name="length">The video's length, so the track is as long as it (null: ends after the last line).</param>
    /// <param name="concurrency">Lines spoken at the same time (they are still placed in order).</param>
    public static async Task<VoiceTrackResult> BuildAsync(IReadOnlyList<SubtitleCue> cues, Func<string, float[]> speak, string outputPath,
        TimeSpan? length, IProgress<EngineProgress>? progress, CancellationToken ct, int concurrency = 1)
    {
        const int rate = KokoroTts.SampleRate;
        var ordered = cues.Where(c => c.End > c.Start).OrderBy(c => c.Start).ThenBy(c => c.Index).ToList();
        var tooLong = new List<LongLine>();
        int spoken = 0, silent = 0;
        long longestDelay = 0, speech = 0;
        long minPause = (long)(MinPause.TotalSeconds * rate);
        var tmp = outputPath + ".part";
        VoiceTrackResult result;
        // Lines are spoken ahead, up to `concurrency` at once, and written in order as each is ready.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ahead = new Queue<Task<float[]>>();
        int started = 0;
        void Fill()
        {
            while (ahead.Count < Math.Max(1, concurrency) && started < ordered.Count)
            {
                var text = ordered[started++].Text;
                ahead.Enqueue(Task.Run(() =>
                {
                    stop.Token.ThrowIfCancellationRequested();
                    return speak(text);
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
                    var samples = await ahead.Dequeue().ConfigureAwait(false);
                    progress?.Report(new EngineProgress((i + 1) / (double)ordered.Count, $"Line {i + 1} of {ordered.Count}", cue.Text));
                    if (samples.Length == 0) { silent++; continue; }
                    spoken++;
                    speech += samples.Length;

                    long at = (long)(cue.Start.TotalSeconds * rate);
                    long earliest = wav.SamplesWritten + (wav.SamplesWritten > 0 && at < wav.SamplesWritten ? minPause : 0);
                    long start = Math.Max(at, earliest);
                    longestDelay = Math.Max(longestDelay, start - at);
                    wav.WriteSilence(start - wav.SamplesWritten);
                    wav.Write(samples);

                    // Time before the next line starts (to the end of the track for the last one).
                    var next = i + 1 < ordered.Count ? ordered[i + 1].Start : length ?? TimeSpan.MaxValue;
                    var slot = next - cue.Start;
                    var said = TimeSpan.FromSeconds(samples.Length / (double)rate);
                    if (said > slot) tooLong.Add(new LongLine(cue.Index, said, slot));
                }
                if (length is { } total) wav.WriteSilence((long)(total.TotalSeconds * rate) - wav.SamplesWritten);
                result = new VoiceTrackResult(spoken, silent, tooLong, TimeSpan.FromSeconds(wav.SamplesWritten / (double)rate),
                    TimeSpan.FromSeconds(longestDelay / (double)rate), TimeSpan.FromSeconds(speech / (double)rate));
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
