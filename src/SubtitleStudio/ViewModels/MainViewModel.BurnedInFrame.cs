using System.Globalization;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.BurnedIn;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Burned-in tab, frame picker: step or drag to any moment of the video, see the subtitle area as the
/// text reader gets it, and what it reads there (kept as subtitle, and everything it found). This is how
/// to check the area, the clean-up and the OCR language on a real frame before a long extraction.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(1);

    private TimeSpan _frameTime;
    private CancellationTokenSource? _frameCts;
    private string _testReadText = string.Empty, _testReadAll = string.Empty;
    private bool _frameBusy;

    /// <summary>Moment shown in the frame picker, in seconds (slider).</summary>
    public double FrameSeconds
    {
        get => _frameTime.TotalSeconds;
        set
        {
            var t = TimeSpan.FromSeconds(Math.Clamp(value, 0, VideoSeconds));
            if (Math.Abs((t - _frameTime).TotalSeconds) < 0.04) return;
            _frameTime = t;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameTimeText));
            ScheduleFrameRead(grab: true);
        }
    }

    public double VideoSeconds => Math.Max(1, _detection?.Video.Duration.TotalSeconds ?? 1);

    public string FrameTimeText => _frameTime.ToString(_frameTime.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>What extraction would keep from this frame.</summary>
    public string TestReadText
    {
        get => _testReadText;
        private set => SetProperty(ref _testReadText, value);
    }

    /// <summary>Everything the reader found in the strip, before the subtitle filters.</summary>
    public string TestReadAll
    {
        get => _testReadAll;
        private set
        {
            if (SetProperty(ref _testReadAll, value)) OnPropertyChanged(nameof(KeepCheckText));
        }
    }

    public bool FrameBusy
    {
        get => _frameBusy;
        private set => SetProperty(ref _frameBusy, value);
    }

    public RelayCommand PreviousFrameCommand { get; private set; } = null!;
    public RelayCommand NextFrameCommand { get; private set; } = null!;
    public RelayCommand ShowExampleCommand { get; private set; } = null!;
    public RelayCommand SaveFrameImagesCommand { get; private set; } = null!;

    /// <summary>Where "Save images" writes (next to the EXE, like the settings).</summary>
    public static string FrameImagesFolder => Path.Combine(Services.AppPaths.ExeDirectory, "subt_frames");

    private void InitBurnedInFrame()
    {
        PreviousFrameCommand = new RelayCommand(() => FrameSeconds = (_frameTime - FrameStep).TotalSeconds, () => _detection is not null && !BurnedInBusy);
        NextFrameCommand = new RelayCommand(() => FrameSeconds = (_frameTime + FrameStep).TotalSeconds, () => _detection is not null && !BurnedInBusy);
        SaveFrameImagesCommand = new RelayCommand(SaveFrameImages, () => PreviewFrame is not null && OcrPreview is not null && !BurnedInBusy);
        ShowExampleCommand = new RelayCommand(p =>
        {
            if (p is DetectedLine line) FrameSeconds = line.Time.TotalSeconds;
        }, p => p is DetectedLine && _detection is not null && !BurnedInBusy);
    }

    /// <summary>
    /// Saves the frame, the subtitle area and what the reader gets as PNGs, plus the readings and
    /// settings in a text file, to <see cref="FrameImagesFolder"/>, and shows the folder. For
    /// troubleshooting: the images can be shared to tune the reading on a real video.
    /// </summary>
    private void SaveFrameImages()
    {
        if (PreviewFrame is not { } frame || OcrPreview is not { } reader || _detection is not { } detection) return;
        try
        {
            var folder = Directory.CreateDirectory(FrameImagesFolder).FullName;
            var name = BurnedInVideo is { } v ? Path.GetFileNameWithoutExtension(v.Name) : "frame";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var stem = Path.Combine(folder, $"{name} {_frameTime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");

            Services.Video.PngWriter.Save(frame, stem + " 1 frame.png");
            Services.Video.PngWriter.Save(CropBand(frame), stem + " 2 area.png");
            Services.Video.PngWriter.Save(reader, stem + " 3 reader.png");
            var r = detection.Result;
            File.WriteAllText(stem + " 4 readings.txt", string.Join(Environment.NewLine,
                $"Subtitle Studio {Services.AppInfo.DisplayVersion}",
                $"Video: {BurnedInVideo?.Name} ({detection.Video.Width}x{detection.Video.Height}, {detection.Video.FrameRate:0.###} fps)",
                $"Time: {FrameTimeText}",
                $"Area: {_bandTopPercent:0.#}% to {_bandBottomPercent:0.#}% (detected {r.BandTop:P1} to {r.BandBottom:P1})",
                $"Line height: {r.LineHeight:0.0000} of the frame; reading scale {detection.Scale:0.00}; light text {r.LightText}",
                $"Clean-up: {(_burnedInCleanup ? "on" : "off")}; OCR language: {_s.BurnedIn.Ocr.LanguageTag}",
                $"Reads as subtitle: {TestReadText}",
                $"All text found: {TestReadAll}"));
            SetStatus($"Saved the frame images to {folder}.", StatusKind.Success);
            _s.Dialogs.RevealInExplorer(stem + " 1 frame.png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Could not save the frame images: " + ex.Message, StatusKind.Error);
        }
    }

    /// <summary>After Detect: the picker starts at the frame Detect chose, and reads it.</summary>
    private void ResetFramePicker(TimeSpan at)
    {
        _frameTime = at;
        OnPropertyChanged(nameof(FrameSeconds));
        OnPropertyChanged(nameof(VideoSeconds));
        OnPropertyChanged(nameof(FrameTimeText));
        ScheduleFrameRead(grab: false);
    }

    private void CancelFrameRead()
    {
        _frameCts?.Cancel();
        _frameCts = null;
        FrameBusy = false;
    }

    private void ClearTestRead()
    {
        CancelFrameRead();
        TestReadText = string.Empty;
        TestReadAll = string.Empty;
    }

    /// <summary>Debounced: dragging the slider or the area settles for a moment before anything is read.</summary>
    private void ScheduleFrameRead(bool grab)
    {
        if (_detection is null || BurnedInBusy || !OcrAvailable) return;
        _frameCts?.Cancel();
        var cts = new CancellationTokenSource();
        _frameCts = cts;
        _ = FrameReadAsync(grab, cts);
    }

    private async Task FrameReadAsync(bool grab, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await Task.Delay(grab ? 250 : 150, ct);
            var detection = _detection;
            var video = BurnedInVideo;
            if (detection is null || video is null) return;
            FrameBusy = true;

            if (grab)
            {
                if (FfmpegStatus.FfmpegPath is not { } ffmpeg) return;
                var frame = await _s.BurnedIn.GrabFrameAsync(ffmpeg, video.FullPath, detection.Video, _frameTime, detection.Scale, ct);
                ct.ThrowIfCancellationRequested();
                if (frame is null)
                {
                    TestReadText = "This moment could not be read from the video.";
                    TestReadAll = string.Empty;
                    return;
                }
                RunOnUi(() => PreviewFrame = frame); // also refreshes the strip
            }

            var strip = OcrPreview;
            var previewFrame = PreviewFrame;
            if (strip is null || previewFrame is null) return;
            double? linePx = detection.Result.SamplesWithSubtitles > 0 ? detection.Result.LineHeight * previewFrame.Height : null;
            var read = await _s.BurnedIn.TestReadAsync(strip, linePx, ct);
            ct.ThrowIfCancellationRequested();
            RunOnUi(() =>
            {
                TestReadText = read.Subtitle.Length > 0 ? read.Subtitle : "(nothing kept as subtitle text)";
                TestReadAll = read.Everything.Length > 0 ? read.Everything : "(no text found)";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            RunOnUi(() => TestReadText = "Could not read this frame: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_frameCts, cts))
            {
                RunOnUi(() => FrameBusy = false);
                _frameCts = null;
            }
            cts.Dispose();
        }
    }
}
