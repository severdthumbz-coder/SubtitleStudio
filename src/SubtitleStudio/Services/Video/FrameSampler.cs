using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SubtitleStudio.Services.Video;

/// <summary>An uncompressed frame (or crop of one): 32-bit BGRA, top-down rows, no padding.</summary>
public sealed class FrameSample
{
    public FrameSample(int width, int height, byte[] bgra, TimeSpan time)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("Pixel buffer does not match the frame size.");
        Width = width;
        Height = height;
        Bgra = bgra;
        Time = time;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public TimeSpan Time { get; }

    /// <summary>Copy of a horizontal band (rows <paramref name="top"/>..top+height).</summary>
    public FrameSample CropRows(int top, int height)
    {
        top = Math.Clamp(top, 0, Height - 1);
        height = Math.Clamp(height, 1, Height - top);
        var buffer = new byte[Width * height * 4];
        Buffer.BlockCopy(Bgra, top * Width * 4, buffer, 0, buffer.Length);
        return new FrameSample(Width, height, buffer, Time);
    }

    /// <summary>
    /// Mean absolute luma difference to another frame of the same size (0..255), sampled on a grid.
    /// Cheap enough to run on every frame; used to skip OCR when the picture hasn't changed.
    /// </summary>
    public double DifferenceTo(FrameSample other)
    {
        if (other.Width != Width || other.Height != Height) return 255;
        long sum = 0;
        int count = 0;
        for (int y = 0; y < Height; y += 3)
        {
            int row = y * Width * 4;
            for (int x = 0; x < Width; x += 3)
            {
                int i = row + x * 4;
                int a = (Bgra[i] * 29 + Bgra[i + 1] * 150 + Bgra[i + 2] * 77) >> 8;
                int b = (other.Bgra[i] * 29 + other.Bgra[i + 1] * 150 + other.Bgra[i + 2] * 77) >> 8;
                sum += Math.Abs(a - b);
                count++;
            }
        }
        return count == 0 ? 0 : (double)sum / count;
    }
}

public sealed record VideoInfo(int Width, int Height, TimeSpan Duration, double FrameRate);

/// <summary>
/// Pulls frames out of a video with ffmpeg as raw BGRA on stdout (no temp files).
/// All sizes are computed here and passed to ffmpeg explicitly, so the byte count per frame is exact.
/// </summary>
public sealed class FrameSampler
{
    /// <summary>Hardware decoding: d3d11va works on NVIDIA, AMD and Intel; ffmpeg falls back to CPU if it can't.</summary>
    public bool UseHardwareDecoding { get; set; } = true;

    /// <summary>Optional: long-running ffmpeg commands and their errors are written here.</summary>
    public ActivityLog? Log { get; set; }

    public async Task<VideoInfo> ProbeAsync(string ffprobePath, string videoPath, CancellationToken ct)
    {
        var psi = NewStartInfo(ffprobePath);
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate:format=duration", "-of", "json", videoPath })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe could not be started.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        try
        {
            using var json = JsonDocument.Parse(stdout);
            var streams = json.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0) throw new InvalidOperationException("This file has no video stream.");
            var v = streams[0];
            int w = v.GetProperty("width").GetInt32();
            int h = v.GetProperty("height").GetInt32();
            double fps = ParseRate(v.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
            double seconds = json.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
            return new VideoInfo(w, h, TimeSpan.FromSeconds(seconds), fps);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException($"Could not read the video's size: {FirstLine(stderr)}", ex);
        }
    }

    /// <summary>Output size for a given target width, keeping aspect ratio and an even height.</summary>
    public static (int Width, int Height) ScaledSize(VideoInfo info, int targetWidth)
    {
        int w = Math.Min(targetWidth, info.Width) & ~1;
        int h = (int)Math.Round(w * (double)info.Height / info.Width / 2.0) * 2;
        return (Math.Max(2, w), Math.Max(2, h));
    }

    /// <summary>One frame at <paramref name="time"/>, scaled down to <paramref name="targetWidth"/> (never up).</summary>
    public Task<FrameSample?> GrabAsync(string ffmpegPath, string videoPath, VideoInfo info, TimeSpan time, int targetWidth, CancellationToken ct)
    {
        var (w, h) = ScaledSize(info, targetWidth);
        return GrabAtSizeAsync(ffmpegPath, videoPath, time, w, h, ct);
    }

    /// <summary>One frame at <paramref name="time"/>, resized by <paramref name="scale"/> (can enlarge).</summary>
    public Task<FrameSample?> GrabScaledAsync(string ffmpegPath, string videoPath, VideoInfo info, TimeSpan time, double scale, CancellationToken ct)
    {
        int w = Math.Max(2, (int)Math.Round(info.Width * scale) & ~1);
        int h = Math.Max(2, (int)Math.Round(info.Height * scale) & ~1);
        return GrabAtSizeAsync(ffmpegPath, videoPath, time, w, h, ct);
    }

    private async Task<FrameSample?> GrabAtSizeAsync(string ffmpegPath, string videoPath, TimeSpan time, int w, int h, CancellationToken ct)
    {
        var psi = NewStartInfo(ffmpegPath);
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        foreach (var a in new[] { "-frames:v", "1", "-an", "-sn", "-vf", $"scale={w}:{h}:flags=lanczos", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var buffer = new byte[w * h * 4];
        int filled = await ReadFullAsync(process.StandardOutput.BaseStream, buffer, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return filled == buffer.Length ? new FrameSample(w, h, buffer, time) : null;
    }

    /// <summary>Rows of the source frame covered by a band: even start and height, as crop needs for 4:2:0 video.</summary>
    public static (int Y, int Height) BandRows(VideoInfo info, double bandTop, double bandBottom)
    {
        int y = Math.Clamp((int)(bandTop * info.Height) & ~1, 0, info.Height - 2);
        int height = Math.Clamp(((int)Math.Ceiling(bandBottom * info.Height) - y + 1) & ~1, 2, info.Height - y);
        return (y, height);
    }

    /// <summary>
    /// Streams frames at <paramref name="fps"/> through the whole video in one decode pass, cropped to
    /// the rows [bandTop, bandBottom) (fractions of the frame height) at full resolution, then resized by
    /// <paramref name="scale"/> (above 1 enlarges small text for OCR). Frame i is at i / fps seconds.
    /// </summary>
    public async IAsyncEnumerable<FrameSample> StreamBandAsync(
        string ffmpegPath,
        string videoPath,
        VideoInfo info,
        double fps,
        double scale,
        double bandTop,
        double bandBottom,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (cropY, cropH) = BandRows(info, bandTop, bandBottom);
        int w = info.Width, h = cropH;
        string resize = string.Empty;
        if (Math.Abs(scale - 1) > 0.01)
        {
            w = Math.Max(2, (int)Math.Round(info.Width * scale) & ~1);
            h = Math.Max(2, (int)Math.Round(cropH * scale) & ~1);
            resize = $",scale={w}:{h}:flags=lanczos";
        }

        var psi = NewStartInfo(ffmpegPath);
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        if (UseHardwareDecoding) { psi.ArgumentList.Add("-hwaccel"); psi.ArgumentList.Add("auto"); }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        var filter = string.Create(CultureInfo.InvariantCulture, $"fps={fps},crop={info.Width}:{cropH}:0:{cropY}{resize}");
        foreach (var a in new[] { "-an", "-sn", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(a);

        Log?.Detail("ffmpeg", ActivityLog.CommandLine(ffmpegPath, psi.ArgumentList));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stream = process.StandardOutput.BaseStream;
        int frameBytes = w * h * 4;
        long index = 0;

        try
        {
            while (true)
            {
                var buffer = new byte[frameBytes];
                int filled = await ReadFullAsync(stream, buffer, ct).ConfigureAwait(false);
                if (filled < frameBytes) break;
                yield return new FrameSample(w, h, buffer, TimeSpan.FromSeconds(index / fps));
                index++;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (stderr.Trim().Length > 0) Log?.Detail("ffmpeg", "decoder said: " + stderr.Trim());
        if (index == 0 && process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg could not decode the video: {FirstLine(stderr)}");
    }

    private static ProcessStartInfo NewStartInfo(string exe) => new(exe)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "unknown error";
}
