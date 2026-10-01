using System.Diagnostics;

namespace SubtitleStudio.Services.Audio;

/// <summary>Loudness over time at a fixed frame size (10 ms), used to find where speech is.</summary>
public sealed class AudioEnvelope
{
    public const int FrameMilliseconds = 10;

    public AudioEnvelope(float[] rmsDb) => RmsDb = rmsDb;

    /// <summary>RMS level per 10 ms frame, in dBFS (silence is about -100).</summary>
    public float[] RmsDb { get; }

    public TimeSpan Duration => TimeSpan.FromMilliseconds((double)RmsDb.Length * FrameMilliseconds);

    public static int FrameOf(TimeSpan t) => (int)Math.Round(t.TotalMilliseconds / FrameMilliseconds);

    public static TimeSpan TimeOf(int frame) => TimeSpan.FromMilliseconds((double)frame * FrameMilliseconds);
}

/// <summary>Which 10 ms frames contain speech-like energy.</summary>
public sealed class SpeechActivity
{
    private SpeechActivity(bool[] active, float thresholdDb)
    {
        Active = active;
        ThresholdDb = thresholdDb;
    }

    public bool[] Active { get; }
    public float ThresholdDb { get; }
    public int FrameCount => Active.Length;

    /// <summary>
    /// Adaptive threshold: between the noise floor (15th percentile) and loud speech (95th percentile).
    /// Then a 150 ms hangover bridges short dips between words, and blips under 120 ms are dropped.
    /// A heuristic, not a speech model: music and effects count as "activity" too.
    /// </summary>
    public static SpeechActivity FromEnvelope(AudioEnvelope envelope, double sensitivity = 0.35)
    {
        var db = envelope.RmsDb;
        if (db.Length == 0) return new SpeechActivity(Array.Empty<bool>(), 0);

        var sorted = (float[])db.Clone();
        Array.Sort(sorted);
        float floor = sorted[(int)(sorted.Length * 0.15)];
        float loud = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))];
        float threshold = floor + (float)((loud - floor) * sensitivity);
        if (loud - floor < 6) threshold = loud + 1; // no dynamics at all: treat as silence

        var active = new bool[db.Length];
        for (int i = 0; i < db.Length; i++) active[i] = db[i] >= threshold;

        const int hangover = 15; // 150 ms
        for (int i = 0, lastActive = -hangover - 1; i < active.Length; i++)
        {
            if (active[i])
            {
                if (i - lastActive <= hangover && lastActive >= 0)
                    for (int j = lastActive + 1; j < i; j++) active[j] = true;
                lastActive = i;
            }
        }

        const int minRun = 12; // 120 ms
        for (int i = 0; i < active.Length;)
        {
            if (!active[i]) { i++; continue; }
            int start = i;
            while (i < active.Length && active[i]) i++;
            if (i - start < minRun)
                for (int j = start; j < i; j++) active[j] = false;
        }

        return new SpeechActivity(active, threshold);
    }

    /// <summary>Test / advanced use: build from a ready-made activity mask.</summary>
    public static SpeechActivity FromMask(bool[] active) => new(active, 0);

    public bool IsOnset(int i) => i >= 0 && i < Active.Length && Active[i] && (i == 0 || !Active[i - 1]);

    public bool IsOffset(int i) => i > 0 && i <= Active.Length && Active[i - 1] && (i == Active.Length || !Active[i]);
}

/// <summary>
/// Decodes a media file's audio with ffmpeg (16 kHz mono PCM on stdout) and reduces it to a 10 ms
/// loudness envelope. Streams the audio, so a feature-length film needs only a few MB of memory.
/// </summary>
public sealed class AudioEnvelopeService
{
    private const int SampleRate = 16000;
    private const int SamplesPerFrame = SampleRate * AudioEnvelope.FrameMilliseconds / 1000; // 160

    public async Task<AudioEnvelope> ExtractAsync(
        string ffmpegPath,
        string mediaPath,
        TimeSpan? knownDuration,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-i", mediaPath, "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("ffmpeg could not be started.");

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var frames = new List<float>(knownDuration is { } d ? (int)(d.TotalMilliseconds / AudioEnvelope.FrameMilliseconds) + 16 : 1 << 16);
        long totalSamples = 0;
        long expectedSamples = knownDuration is { } kd ? (long)(kd.TotalSeconds * SampleRate) : 0;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[SamplesPerFrame * 2 * 256];
            int carry = 0;
            double sumSquares = 0;
            int samplesInFrame = 0;
            double lastReported = -1;

            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
                if (read == 0) break;
                int available = carry + read;
                int usable = available - (available % 2);

                for (int i = 0; i < usable; i += 2)
                {
                    double s = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768.0;
                    sumSquares += s * s;
                    if (++samplesInFrame == SamplesPerFrame)
                    {
                        frames.Add(ToDb(sumSquares / SamplesPerFrame));
                        sumSquares = 0;
                        samplesInFrame = 0;
                    }
                }

                totalSamples += usable / 2;
                carry = available - usable;
                if (carry > 0) buffer[0] = buffer[usable];

                if (expectedSamples > 0)
                {
                    double fraction = Math.Min(1.0, (double)totalSamples / expectedSamples);
                    if (fraction - lastReported >= 0.01)
                    {
                        progress?.Report(fraction);
                        lastReported = fraction;
                    }
                }
            }

            if (samplesInFrame > 0) frames.Add(ToDb(sumSquares / samplesInFrame));
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (frames.Count == 0)
        {
            bool noAudio = stderr.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase)
                           || stderr.Contains("matches no streams", StringComparison.OrdinalIgnoreCase);
            throw new InvalidOperationException(process.ExitCode != 0 && !noAudio
                ? $"ffmpeg could not read the audio: {FirstLine(stderr)}"
                : "This file has no audio track.");
        }

        progress?.Report(1.0);
        return new AudioEnvelope(frames.ToArray());
    }

    private static float ToDb(double meanSquare) => (float)(10 * Math.Log10(meanSquare + 1e-10));

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
}
