using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SubtitleStudio.Services.Transcription;

/// <summary>An audio stream of a file (Number: its order among the audio streams, as ffmpeg's "0:a:N").</summary>
public sealed record AudioTrack(int Number, string? Language, string? Title, string? Codec, int Channels)
{
    public string Label
    {
        get
        {
            var parts = new List<string> { $"Track {Number + 1}" };
            if (!string.IsNullOrWhiteSpace(Language) && Language != "und") parts.Add(Language!);
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title!);
            if (Channels > 0) parts.Add(Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", var c => $"{c} ch" });
            return string.Join("  ·  ", parts);
        }
    }

    public override string ToString() => Label;
}

/// <summary>Speech for Whisper: the audio decoded by FFmpeg to 16 kHz mono 32-bit float samples.</summary>
public static class AudioExtractor
{
    public const int SampleRate = 16000;

    public static async Task<IReadOnlyList<AudioTrack>> ListTracksAsync(string ffprobe, string mediaPath, CancellationToken ct)
    {
        var (exit, stdout, _) = await RunTextAsync(ffprobe, new[]
        {
            "-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name,channels:stream_tags=language,title", "-of", "json", mediaPath,
        }, ct).ConfigureAwait(false);
        if (exit != 0) return Array.Empty<AudioTrack>();
        return ParseTracks(stdout);
    }

    public static IReadOnlyList<AudioTrack> ParseTracks(string json)
    {
        var tracks = new List<AudioTrack>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return tracks;
            foreach (var s in streams.EnumerateArray())
            {
                string? Tag(string name) => s.TryGetProperty("tags", out var tags)
                    ? tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null
                    : null;
                tracks.Add(new AudioTrack(tracks.Count, Tag("language"), Tag("title"),
                    s.TryGetProperty("codec_name", out var c) ? c.GetString() : null,
                    s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var n) ? n : 0));
            }
        }
        catch (JsonException)
        {
        }
        return tracks;
    }

    /// <summary>
    /// Decodes one audio track (or a part of it) to 16 kHz mono float samples. <paramref name="expected"/>
    /// (the length, if known) sizes the buffer and drives <paramref name="progress"/> (0..1).
    /// </summary>
    public static async Task<float[]> ReadAsync(string ffmpeg, string mediaPath, int track, TimeSpan? start, TimeSpan? length,
        TimeSpan? expected, IProgress<double>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (start is { } s && s > TimeSpan.Zero) args.AddRange(new[] { "-ss", Seconds(s) });
        args.AddRange(new[] { "-i", mediaPath });
        if (length is { } l) args.AddRange(new[] { "-t", Seconds(l) });
        args.AddRange(new[] { "-map", $"0:a:{track}", "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" });

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

        var expectedSamples = expected is { } e && e > TimeSpan.Zero ? (long)(e.TotalSeconds * SampleRate) : 0;
        var samples = new float[(int)Math.Clamp(expectedSamples + SampleRate, SampleRate * 60L, Array.MaxLength)];
        long count = 0;
        var bytes = new byte[1 << 20];
        int carry = 0, read;
        double lastReport = -1;
        var output = process.StandardOutput.BaseStream;
        while ((read = await output.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), ct).ConfigureAwait(false)) > 0)
        {
            int total = carry + read, whole = total / 4;
            if (count + whole > samples.Length)
            {
                long grown = Math.Max(samples.Length * 3L / 2, count + whole);
                if (grown > Array.MaxLength) throw new InvalidOperationException("The audio is too long to transcribe in one go (over 37 hours).");
                Array.Resize(ref samples, (int)grown);
            }
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole * 4)).CopyTo(samples.AsSpan((int)count));
            count += whole;
            carry = total - whole * 4;
            if (carry > 0) Buffer.BlockCopy(bytes, whole * 4, bytes, 0, carry);

            if (expectedSamples > 0)
            {
                double f = Math.Min(1, count / (double)expectedSamples);
                if (f - lastReport >= 0.01) { lastReport = f; progress?.Report(f); }
            }
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var first = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {process.ExitCode}";
            throw new InvalidOperationException(first.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                ? "This file has no audio track to transcribe."
                : $"FFmpeg couldn't read the audio: {first}");
        }
        if (count == 0) throw new InvalidOperationException("No audio came out of this file (the track is empty or silent).");
        if (count != samples.Length) Array.Resize(ref samples, (int)count);
        return samples;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<(int Exit, string Out, string Err)> RunTextAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
