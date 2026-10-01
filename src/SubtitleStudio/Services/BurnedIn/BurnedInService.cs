using SubtitleStudio.Models;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Services.BurnedIn;

/// <param name="Scale">Resize factor used for reading: the preview frame is at this scale, and so is extraction.</param>
public sealed record DetectionRun(DetectionResult Result, VideoInfo Video, FrameSample? PreviewFrame, double Scale = 1);

/// <param name="Subtitle">What extraction would keep as subtitle text.</param>
/// <param name="Everything">All text the reader found, before filtering.</param>
public sealed record TestRead(string Subtitle, string Everything);

public sealed record ExtractionProgress(double Fraction, int FramesRead, int FramesRecognized, string? LastText);

/// <param name="LineHeight">Expected line height as a fraction of the frame height (from Detect), or null if unknown.</param>
/// <param name="CleanUp">Isolate white / yellow text from the picture before OCR.</param>
/// <param name="SkipAds">Leave out readings with a web address in them (website ads burned in with the subtitles).</param>
public sealed record ExtractionOptions(double BandTop, double BandBottom, double FramesPerSecond, double? LineHeight, bool CleanUp, bool SkipAds = true);

/// <summary>
/// Orchestrates ffmpeg frame sampling + OCR for the Burned-in tab:
///  - Detect: OCR a few dozen full frames spread over the video, find the subtitle area, line height and colour.
///  - Extract: decode the subtitle area at full resolution (small text enlarged), clean the picture away
///    from the text, OCR when the text changes, re-read each subtitle from several frames combined,
///    and build cues from the best readings.
/// </summary>
public sealed class BurnedInService
{
    private readonly FrameSampler _sampler;
    private readonly ITextRecognizer _ocr;

    public BurnedInService(FrameSampler sampler, ITextRecognizer ocr)
    {
        _sampler = sampler;
        _ocr = ocr;
    }

    public ITextRecognizer Ocr => _ocr;

    /// <summary>Where ffmpeg commands and decoder errors go (the app's activity log).</summary>
    public ActivityLog? Log
    {
        get => _sampler.Log;
        set => _sampler.Log = value;
    }

    /// <summary>Diagnostics: every OCR reading during extraction (tests and troubleshooting).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Detection reads whole frames scaled to this width: enough to find where the text is.</summary>
    public const int AnalysisWidth = 1280;

    /// <summary>Subtitle lines are resized to about this height (pixels) for reading.</summary>
    public const double TargetLinePixels = 44;

    /// <summary>A band counts as changed when this share of its pixels switched between text and not text.</summary>
    public const double MaskChangeThreshold = 0.004;

    /// <summary>
    /// Resize factor for reading: small subtitles are enlarged (up to 3x), very large ones reduced
    /// (down to half), and the width is kept within what the OCR engine accepts.
    /// </summary>
    public double ScaleFor(VideoInfo info, double? lineHeight)
    {
        double k = lineHeight is { } lh && lh > 0 && info.Height > 0
            ? TargetLinePixels / (lh * info.Height)
            : Math.Max(1, AnalysisWidth / (double)Math.Max(1, info.Width));
        k = Math.Clamp(k, 0.5, 3);
        int maxDim = Math.Min(_ocr.MaxImageDimension, 4096);
        if (info.Width * k > maxDim) k = maxDim / (double)Math.Max(1, info.Width);
        return k;
    }

    /// <summary>What the OCR engine gets for one band image (shown in the tab as a preview).</summary>
    public static FrameSample PrepareForOcr(FrameSample band, double? lineHeightPx, bool cleanUp)
        => cleanUp ? SubtitleImageCleaner.Clean(band, lineHeightPx ?? band.Height / 3.0) : band;

    /// <summary>One frame at the reading scale (the Burned-in tab's frame picker).</summary>
    public Task<FrameSample?> GrabFrameAsync(string ffmpeg, string videoPath, VideoInfo info, TimeSpan time, double scale, CancellationToken ct)
        => _sampler.GrabScaledAsync(ffmpeg, videoPath, info, time, scale, ct);

    /// <summary>
    /// Reads one prepared strip (see <see cref="PrepareForOcr"/>) the way extraction does, and also
    /// returns everything the reader found before the subtitle filters, to show why text was kept or not.
    /// </summary>
    public async Task<TestRead> TestReadAsync(FrameSample prepared, double? lineHeightPx, CancellationToken ct)
    {
        var lines = await _ocr.RecognizeAsync(prepared, ct).ConfigureAwait(false);
        var everything = string.Join("  /  ", lines.OrderBy(l => l.Y).ThenBy(l => l.X).Select(l => l.Text.Trim()).Where(t => t.Length > 0));
        return new TestRead(BurnedInAnalyzer.SubtitleTextOf(lines, prepared.Width, lineHeightPx), everything);
    }

    public async Task<DetectionRun> DetectAsync(string ffmpeg, string ffprobe, string videoPath, int sampleCount, IProgress<double>? progress, CancellationToken ct)
    {
        var info = await _sampler.ProbeAsync(ffprobe, videoPath, ct).ConfigureAwait(false);
        var duration = info.Duration > TimeSpan.Zero ? info.Duration : TimeSpan.FromMinutes(1);

        var samples = new List<FrameText>();
        for (int i = 0; i < sampleCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Spread samples over 5%..95% of the running time (skips intros and credits a little).
            var t = TimeSpan.FromSeconds(duration.TotalSeconds * (0.05 + 0.9 * (i + 0.5) / sampleCount));
            var frame = await _sampler.GrabAsync(ffmpeg, videoPath, info, t, AnalysisWidth, ct).ConfigureAwait(false);
            if (frame is not null)
            {
                var lines = await _ocr.RecognizeAsync(frame, ct).ConfigureAwait(false);
                var light = lines.Select(l => SubtitleImageCleaner.LightRatio(frame, l)).ToList();
                samples.Add(new FrameText(t, frame.Width, frame.Height, lines, light));
            }
            progress?.Report((i + 1) / (double)(sampleCount + 1));
        }

        if (samples.Count == 0) throw new InvalidOperationException("No frames could be read from this video.");
        var result = BurnedInAnalyzer.Detect(samples);

        // Preview at the reading scale, so the "what the reader sees" strip matches extraction exactly.
        double scale = ScaleFor(info, result.SamplesWithSubtitles > 0 ? result.LineHeight : null);
        var previewTime = result.PreviewTime ?? samples[samples.Count / 2].Time;
        var preview = await _sampler.GrabScaledAsync(ffmpeg, videoPath, info, previewTime, scale, ct).ConfigureAwait(false);
        if (result.SamplesWithSubtitles > 0)
            result = result with { Examples = await ReadExamplesAsync(ffmpeg, videoPath, info, result, scale, ct).ConfigureAwait(false) };
        progress?.Report(1);
        return new DetectionRun(result, info, preview, scale);
    }

    /// <summary>
    /// The sample readings above are of whole frames, taken before the subtitle area and colour were
    /// known, so they include the picture behind the letters ("T e surgery as as c ss"). Once Detect
    /// knows the area, each example moment is read again the way extraction reads: the subtitle area
    /// at the reading scale, with the background clean-up for white or yellow text. Moments that don't
    /// read as a subtitle that way (a sign, a stray reading) are left out; one line per moment.
    /// </summary>
    private async Task<IReadOnlyList<DetectedLine>> ReadExamplesAsync(string ffmpeg, string videoPath, VideoInfo info, DetectionResult result, double scale, CancellationToken ct)
    {
        var lines = new List<DetectedLine>();
        foreach (var time in result.Examples.Select(e => e.Time).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var frame = await _sampler.GrabScaledAsync(ffmpeg, videoPath, info, time, scale, ct).ConfigureAwait(false);
            if (frame is null) continue;
            int top = Math.Clamp((int)(frame.Height * result.BandTop), 0, frame.Height - 2);
            int height = Math.Clamp((int)Math.Ceiling(frame.Height * result.BandBottom) - top, 2, frame.Height - top);
            double linePx = result.LineHeight * frame.Height;
            var prepared = PrepareForOcr(frame.CropRows(top, height), linePx, result.LightText);
            var read = await TestReadAsync(prepared, linePx, ct).ConfigureAwait(false);
            if (read.Subtitle.Length > 0 && !BurnedInAnalyzer.LooksLikeWebAddress(read.Everything)) lines.Add(new DetectedLine(time, read.Subtitle));
        }
        return lines;
    }

    /// <summary>
    /// Reads the band at <paramref name="options"/>.FramesPerSecond and returns cues.
    /// Per frame: build the text mask (cheap). OCR only runs when the text changed; while a subtitle
    /// stays up its masks are combined and re-read after 4 and 12 frames and when it ends, so every
    /// subtitle gets several readings and the best-agreeing one wins.
    /// </summary>
    public async Task<List<SubtitleCue>> ExtractAsync(
        string ffmpeg,
        string videoPath,
        VideoInfo info,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        CancellationToken ct)
    {
        double scale = ScaleFor(info, options.LineHeight);
        double? linePx = options.LineHeight is { } lh ? lh * info.Height * scale : null;
        bool cleanUp = options.CleanUp;
        double fps = options.FramesPerSecond;

        var frames = new List<(TimeSpan Time, string Text)>();
        var accumulator = new MaskAccumulator();
        var reads = new List<string>();
        TextMask? lastMask = null;
        FrameSample? lastRaw = null;
        int segmentStart = -1, segmentFrames = 0, lastReadAt = 0;
        double segmentCentre = double.NaN, lastCentre = double.NaN;
        bool segmentMoving = false;
        int read = 0, recognized = 0;
        double totalFrames = Math.Max(1, info.Duration.TotalSeconds * fps);

        int adsSkipped = 0;
        async Task<string> RecognizeAsync(FrameSample image)
        {
            recognized++;
            var all = await _ocr.RecognizeAsync(image, ct).ConfigureAwait(false);
            if (options.SkipAds && all.Any(l => BurnedInAnalyzer.LooksLikeWebAddress(l.Text)))
            {
                // A website ad (its web address line is dropped as not-a-sentence anyway, but the line
                // with it would become a cue): the whole reading counts as no subtitle.
                adsSkipped++;
                lastCentre = double.NaN;
                Trace?.Invoke($"{image.Time:hh\\:mm\\:ss\\.ff} (ad left out) {string.Join(" / ", all.Select(l => l.Text))}");
                return string.Empty;
            }
            var lines = BurnedInAnalyzer.SubtitleLinesOf(all, image.Width, linePx);
            lastCentre = lines.Count == 0 ? double.NaN : (lines.Min(l => l.Y) + lines.Max(l => l.Bottom)) / 2;
            var text = BurnedInAnalyzer.TextOf(lines);
            Trace?.Invoke($"{image.Time:hh\\:mm\\:ss\\.ff} {text.Replace('\n', '/')}");
            return text;
        }

        async Task<string> ReadMaskAsync(TextMask mask, double maskLine, TimeSpan time)
        {
            var cleaned = SubtitleImageCleaner.RemoveNonText(mask, maskLine);
            return cleaned.Count == 0 ? string.Empty : await RecognizeAsync(SubtitleImageCleaner.Render(cleaned, time)).ConfigureAwait(false);
        }

        async Task ReReadAsync(FrameSample current, double maskLine)
        {
            lastReadAt = segmentFrames;
            string text;
            if (cleanUp)
            {
                var consensus = accumulator.Consensus();
                if (consensus is null) return;
                text = await ReadMaskAsync(consensus, maskLine, current.Time).ConfigureAwait(false);
                if (text.Length > 0) reads.Add(text); // the combined image is the cleanest: counts twice
            }
            else
            {
                text = await RecognizeAsync(current).ConfigureAwait(false);
            }
            if (text.Length > 0) reads.Add(text);
        }

        async Task FinishSegmentAsync(FrameSample current, double maskLine)
        {
            if (segmentStart < 0) return;
            if (cleanUp && segmentFrames >= 3 && segmentFrames != lastReadAt)
                await ReReadAsync(current, maskLine).ConfigureAwait(false);
            // Text that moved up or down while "the same" (rolling credits) is not a subtitle.
            var best = segmentMoving ? string.Empty : BurnedInAnalyzer.BestReading(reads);
            for (int k = segmentStart; k < frames.Count; k++) frames[k] = (frames[k].Time, best);
            segmentStart = -1;
            segmentMoving = false;
            segmentCentre = double.NaN;
            segmentFrames = 0;
            lastReadAt = 0;
            reads.Clear();
            accumulator.Reset();
        }

        FrameSample? previous = null;
        double maskLineHeight = 0;
        await foreach (var frame in _sampler.StreamBandAsync(ffmpeg, videoPath, info, fps, scale, options.BandTop, options.BandBottom, ct).ConfigureAwait(false))
        {
            read++;
            if (maskLineHeight == 0) maskLineHeight = linePx ?? frame.Height / 3.0;
            var mask = cleanUp ? SubtitleImageCleaner.LightTextMask(frame, maskLineHeight) : null;
            bool changed = cleanUp
                ? lastMask is null || mask!.DifferenceTo(lastMask) > MaskChangeThreshold
                : lastRaw is null || frame.DifferenceTo(lastRaw) > 3.0;

            if (changed)
            {
                var text = cleanUp
                    ? await ReadMaskAsync(mask!, maskLineHeight, frame.Time).ConfigureAwait(false)
                    : await RecognizeAsync(frame).ConfigureAwait(false);
                lastMask = mask;
                lastRaw = frame;

                bool sameSubtitle = segmentStart >= 0 && text.Length > 0 && BurnedInAnalyzer.Matches(text, BurnedInAnalyzer.BestReading(reads));
                if (sameSubtitle)
                {
                    reads.Add(text);
                    if (!double.IsNaN(segmentCentre) && !double.IsNaN(lastCentre) && Math.Abs(lastCentre - segmentCentre) > maskLineHeight * 0.35)
                        segmentMoving = true;
                }
                else
                {
                    await FinishSegmentAsync(previous ?? frame, maskLineHeight).ConfigureAwait(false);
                    if (text.Length > 0)
                    {
                        segmentStart = frames.Count;
                        segmentCentre = lastCentre;
                        reads.Add(text);
                    }
                }
            }

            if (segmentStart >= 0)
            {
                if (cleanUp) accumulator.Add(mask!);
                segmentFrames++;
            }
            frames.Add((frame.Time, segmentStart >= 0 ? reads[^1] : string.Empty));

            if (segmentStart >= 0 && segmentFrames is 4 or 12 && lastReadAt != segmentFrames)
                await ReReadAsync(frame, maskLineHeight).ConfigureAwait(false);

            previous = frame;
            if (read % 10 == 0)
                progress?.Report(new ExtractionProgress(Math.Min(1, read / totalFrames), read, recognized, segmentStart >= 0 ? reads[^1] : null));
        }
        if (previous is not null) await FinishSegmentAsync(previous, maskLineHeight).ConfigureAwait(false);

        progress?.Report(new ExtractionProgress(1, read, recognized, null));
        if (adsSkipped > 0) Log?.Info("Extract", $"Left out {adsSkipped} reading(s) with a web address in them (website ads burned into the video).");
        return BurnedInAnalyzer.BuildCues(frames, TimeSpan.FromSeconds(1 / fps));
    }
}
