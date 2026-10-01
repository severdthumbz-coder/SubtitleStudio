using System.Globalization;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.ViewModels;

public sealed record ExtractSpeedOption(double FramesPerSecond, string Label);

/// <summary>
/// Burned-in subs tab: detect subtitles that are part of the picture, show where they are, and
/// extract them to a real subtitle via OCR (hardsub to softsub). Removal arrives in a later build.
/// Work is in Services/BurnedIn (BurnedInService, BurnedInAnalyzer) and Services/Gpu.
/// </summary>
public sealed partial class MainViewModel
{
    private MediaItem? _burnedInVideo;
    private GpuInfo? _gpu;
    private bool _burnedInBusy;
    private double _burnedInProgress;
    private string _burnedInStatus = "Pick a video and press Detect.";
    private CancellationTokenSource? _burnedInCts;
    private DetectionRun? _detection;
    private FrameSample? _previewFrame;
    private double _bandTopPercent = 78, _bandBottomPercent = 96;
    private ExtractSpeedOption _extractSpeed = null!;
    private bool _burnedInCleanup = true;
    private FrameSample? _ocrPreview;

    public MediaItem? BurnedInVideo
    {
        get => _burnedInVideo;
        set
        {
            if (!SetProperty(ref _burnedInVideo, value)) return;
            ClearTestRead();
            ShowCompare = false;
            _compare?.Unload();
            OnPropertyChanged(nameof(HasComparison));
            _detection = null;
            PreviewFrame = null;
            RefreshOcrPreview();
            OnPropertyChanged(nameof(HasDetection));
            OnPropertyChanged(nameof(DetectionHeadline));
            OnPropertyChanged(nameof(DetectionDetails));
            OnPropertyChanged(nameof(DetectionExamples));
            OnPropertyChanged(nameof(IgnoredStaticTexts));
            BurnedInStatus = value is null ? "Pick a video and press Detect." : "Press Detect to look for burned-in subtitles.";
        }
    }

    // ---------------- Hardware / OCR ----------------

    public string GpuSummary => _gpu?.Summary ?? "Detecting graphics hardware...";

    public IReadOnlyList<GpuAdapter> GpuAdapters => _gpu?.Adapters ?? Array.Empty<GpuAdapter>();

    public string OcrStatusText => _s.BurnedIn.Ocr.IsAvailable
        ? $"Windows OCR, language {_s.BurnedIn.Ocr.LanguageTag}"
        : _s.BurnedIn.Ocr.UnavailableReason ?? "Windows OCR is not available.";

    public bool OcrAvailable => _s.BurnedIn.Ocr.IsAvailable;

    public IReadOnlyList<string> OcrLanguages => _s.BurnedIn.Ocr.AvailableLanguages;

    public string? SelectedOcrLanguage
    {
        get => _s.BurnedIn.Ocr.LanguageTag;
        set
        {
            if (value is null || value == _s.BurnedIn.Ocr.LanguageTag) return;
            if (!_s.BurnedIn.Ocr.SetLanguage(value))
                SetStatus($"OCR language {value} could not be loaded.", StatusKind.Warning);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OcrStatusText));
            ScheduleFrameRead(grab: false);
        }
    }

    // ---------------- Progress ----------------

    public bool BurnedInBusy
    {
        get => _burnedInBusy;
        private set => SetProperty(ref _burnedInBusy, value);
    }

    public double BurnedInProgress
    {
        get => _burnedInProgress;
        private set => SetProperty(ref _burnedInProgress, value);
    }

    public string BurnedInStatus
    {
        get => _burnedInStatus;
        private set => SetProperty(ref _burnedInStatus, value);
    }

    // ---------------- Detection result ----------------

    public bool HasDetection => _detection is not null;

    public bool DetectionLikely => _detection?.Result.Likely ?? false;

    public string DetectionHeadline => _detection is null
        ? string.Empty
        : _detection.Result.Likely
            ? $"Burned-in subtitles found ({_detection.Result.Position.ToString().ToLowerInvariant()} of the picture)"
            : _detection.Result.SamplesWithSubtitles > 0
                ? "Some text found, but it doesn't look like regular subtitles"
                : "No burned-in subtitles found";

    public string DetectionDetails
    {
        get
        {
            if (_detection is null) return string.Empty;
            var r = _detection.Result;
            var v = _detection.Video;
            var details = $"Text in {r.SamplesWithSubtitles} of {r.SampleCount} sampled frames ({r.HitRatio:P0}). "
                        + $"Subtitle area: {r.BandTop:P0} to {r.BandBottom:P0} of the height. "
                        + $"Video {v.Width}x{v.Height}, {v.FrameRate:0.###} fps.";
            if (r.SamplesWithSubtitles > 0)
                details += $" Text about {r.LineHeight * v.Height:0} px high"
                         + (_detection.Scale > 1.05 ? $", enlarged {_detection.Scale:0.#}x for reading" : string.Empty)
                         + (r.LightText ? "; white or yellow, so the background clean-up is on." : "; not white or yellow, so the background clean-up is off.");
            return details;
        }
    }

    public IReadOnlyList<DetectedLine> DetectionExamples => _detection?.Result.Examples ?? Array.Empty<DetectedLine>();

    public IReadOnlyList<string> IgnoredStaticTexts => _detection?.Result.IgnoredStaticTexts ?? Array.Empty<string>();

    // ---------------- Preview frame + band ----------------

    /// <summary>Converted to an image by FrameImageConverter in the view.</summary>
    public FrameSample? PreviewFrame
    {
        get => _previewFrame;
        private set
        {
            if (!SetProperty(ref _previewFrame, value)) return;
            OnPropertyChanged(nameof(FrameWidth));
            OnPropertyChanged(nameof(FrameHeight));
            OnPropertyChanged(nameof(HasPreviewFrame));
            RaiseBandChanged();
        }
    }

    public bool HasPreviewFrame => _previewFrame is not null;
    public double FrameWidth => _previewFrame?.Width ?? 1280;
    public double FrameHeight => _previewFrame?.Height ?? 720;

    /// <summary>Top of the subtitle area in % of the frame height. Adjustable by hand.</summary>
    public double BandTopPercent
    {
        get => _bandTopPercent;
        set
        {
            value = Math.Clamp(Math.Round(value, 1), 0, _bandBottomPercent - 2);
            if (SetProperty(ref _bandTopPercent, value)) RaiseBandChanged();
        }
    }

    public double BandBottomPercent
    {
        get => _bandBottomPercent;
        set
        {
            value = Math.Clamp(Math.Round(value, 1), _bandTopPercent + 2, 100);
            if (SetProperty(ref _bandBottomPercent, value)) RaiseBandChanged();
        }
    }

    public double BandTopPx => FrameHeight * _bandTopPercent / 100;
    public double BandHeightPx => FrameHeight * (_bandBottomPercent - _bandTopPercent) / 100;
    public string BandText => string.Create(CultureInfo.CurrentCulture, $"{_bandTopPercent:0.#}% to {_bandBottomPercent:0.#}% of the height");

    private void RaiseBandChanged()
    {
        OnPropertyChanged(nameof(BandTopPx));
        OnPropertyChanged(nameof(BandHeightPx));
        OnPropertyChanged(nameof(BandText));
        RefreshOcrPreview();
    }

    /// <summary>
    /// Isolate white / yellow subtitle text from the picture before reading it. Set by Detect from the
    /// colour of the text it found; turn off for coloured subtitles.
    /// </summary>
    public bool BurnedInCleanup
    {
        get => _burnedInCleanup;
        set
        {
            if (SetProperty(ref _burnedInCleanup, value)) RefreshOcrPreview();
        }
    }

    /// <summary>The subtitle area of the preview frame exactly as the text reader will get it.</summary>
    public FrameSample? OcrPreview
    {
        get => _ocrPreview;
        private set
        {
            if (SetProperty(ref _ocrPreview, value)) OnPropertyChanged(nameof(HasOcrPreview));
        }
    }

    public bool HasOcrPreview => _ocrPreview is not null;

    /// <summary>The subtitle area of a frame, as set by the sliders.</summary>
    private FrameSample CropBand(FrameSample frame)
    {
        int top = (int)(frame.Height * _bandTopPercent / 100);
        int height = Math.Max(2, (int)Math.Ceiling(frame.Height * _bandBottomPercent / 100) - top);
        return frame.CropRows(top, height);
    }

    private void RefreshOcrPreview()
    {
        if (_previewFrame is not { } frame || _detection is not { } detection)
        {
            OcrPreview = null;
            return;
        }
        var band = CropBand(frame);
        double? linePx = detection.Result.SamplesWithSubtitles > 0 ? detection.Result.LineHeight * frame.Height : null;
        OcrPreview = BurnedInService.PrepareForOcr(band, linePx, _burnedInCleanup);
        ScheduleFrameRead(grab: false);
    }

    // ---------------- Extraction ----------------

    private bool _leaveOutAds = true;

    /// <summary>Extract: leave out readings with a web address in them (website ads burned in with the subtitles).</summary>
    public bool LeaveOutAds
    {
        get => _leaveOutAds;
        set => SetProperty(ref _leaveOutAds, value);
    }

    public IReadOnlyList<ExtractSpeedOption> ExtractSpeeds { get; } = new[]
    {
        new ExtractSpeedOption(2, "Fast: 2 checks per second"),
        new ExtractSpeedOption(4, "Accurate: 4 checks per second"),
        new ExtractSpeedOption(8, "Precise timing: 8 checks per second (slow)"),
        new ExtractSpeedOption(0, "Frame-exact: every frame (slowest)"),
    };

    public ExtractSpeedOption SelectedExtractSpeed
    {
        get => _extractSpeed;
        set => SetProperty(ref _extractSpeed, value ?? ExtractSpeeds[1]);
    }

    public AsyncRelayCommand DetectBurnedInCommand { get; private set; } = null!;
    public AsyncRelayCommand ExtractBurnedInCommand { get; private set; } = null!;
    public RelayCommand CancelBurnedInCommand { get; private set; } = null!;

    private void InitBurnedIn()
    {
        _extractSpeed = ExtractSpeeds[1];
        _s.BurnedIn.Log = _s.Log;
        InitBurnedInFrame();
        InitBurnedInRemove();
        DetectBurnedInCommand = new AsyncRelayCommand(DetectBurnedInAsync, () => CanRunBurnedIn);
        ExtractBurnedInCommand = new AsyncRelayCommand(ExtractBurnedInAsync, () => CanRunBurnedIn && _detection is not null);
        CancelBurnedInCommand = new RelayCommand(() => _burnedInCts?.Cancel(), () => BurnedInBusy);

        Files.CollectionChanged += (_, _) =>
        {
            if (BurnedInVideo is not null && !Files.Contains(BurnedInVideo)) BurnedInVideo = null;
            BurnedInVideo ??= Files.FirstOrDefault(f => f.Kind == MediaKind.Video);
        };

        // DXGI enumeration is quick but touches the driver; keep it off the UI thread.
        _ = Task.Run(GpuDetector.Detect).ContinueWith(t => RunOnUi(() =>
        {
            _gpu = t.IsCompletedSuccessfully ? t.Result : new GpuInfo(Array.Empty<GpuAdapter>(), null, AiBackend.Cpu, t.Exception?.GetBaseException().Message);
            OnPropertyChanged(nameof(GpuSummary));
            OnPropertyChanged(nameof(GpuAdapters));
            foreach (var a in _gpu.Adapters)
                _s.Log.Detail("Hardware", $"Graphics adapter {a.AdapterIndex}: {a.Name} ({a.MemoryText}{(a.IsSoftware ? ", software" : string.Empty)})");
            DetectHardware(_gpu);
        }), TaskScheduler.Default);
    }

    private bool CanRunBurnedIn => BurnedInVideo is not null && !BurnedInBusy && OcrAvailable && FfmpegStatus.IsComplete;

    private async Task DetectBurnedInAsync()
    {
        var video = BurnedInVideo;
        if (video is null || FfmpegStatus.FfmpegPath is not { } ffmpeg || FfmpegStatus.FfprobePath is not { } ffprobe) return;

        ClearTestRead();
        var detectStarted = DateTime.UtcNow;
        await RunBurnedInAsync("Looking for burned-in subtitles", async ct =>
        {
            _s.Log.Info("Detect", $"{video.Name}: reading 36 sample frames (OCR {_s.BurnedIn.Ocr.LanguageTag}).");
            var progress = new Progress<double>(f =>
            {
                BurnedInProgress = f * 100;
                BurnedInStatus = $"Reading sample frames... {f:P0}";
            });
            var run = await _s.BurnedIn.DetectAsync(ffmpeg, ffprobe, video.FullPath, 36, progress, ct);

            _detection = run;
            _burnedInCleanup = run.Result.SamplesWithSubtitles == 0 || run.Result.LightText;
            OnPropertyChanged(nameof(BurnedInCleanup));
            PreviewFrame = run.PreviewFrame;
            _bandTopPercent = Math.Round(run.Result.BandTop * 100, 1);
            _bandBottomPercent = Math.Round(run.Result.BandBottom * 100, 1);
            OnPropertyChanged(nameof(BandTopPercent));
            OnPropertyChanged(nameof(BandBottomPercent));
            RaiseBandChanged();
            OnPropertyChanged(nameof(HasDetection));
            OnPropertyChanged(nameof(DetectionLikely));
            OnPropertyChanged(nameof(DetectionHeadline));
            OnPropertyChanged(nameof(DetectionDetails));
            OnPropertyChanged(nameof(DetectionExamples));
            OnPropertyChanged(nameof(IgnoredStaticTexts));

            BurnedInStatus = run.Result.Likely
                ? "Check the highlighted area on the frame, adjust it if needed, then Extract."
                : "You can still set the area by hand and Extract if you know there are subtitles.";
            SetStatus(DetectionHeadline + ".", run.Result.Likely ? StatusKind.Success : StatusKind.Info);
            var r = run.Result;
            _s.Log.Info("Detect", $"{video.Name}: {run.Video.Width}x{run.Video.Height}, {run.Video.FrameRate:0.###} fps, {run.Video.Duration:hh\\:mm\\:ss}. "
                + $"Text in {r.SamplesWithSubtitles}/{r.SampleCount} samples; area {r.BandTop:P1}-{r.BandBottom:P1} ({r.Position.ToString().ToLowerInvariant()}); "
                + $"line height {r.LineHeight * run.Video.Height:0} px; {(r.LightText ? "white/yellow text" : "coloured text")}; reading scale {run.Scale:0.00}; "
                + $"took {(DateTime.UtcNow - detectStarted).TotalSeconds:0.0} s.");
        });
        if (_detection is { } done && done.PreviewFrame is not null)
            ResetFramePicker(done.PreviewFrame.Time);
    }

    private async Task ExtractBurnedInAsync()
    {
        var video = BurnedInVideo;
        var detection = _detection;
        if (video is null || detection is null || FfmpegStatus.FfmpegPath is not { } ffmpeg) return;
        if (!ConfirmDiscardChanges()) return;
        CancelFrameRead();

        // 0 = every frame: cue times exact to the frame (1/24 s at 24 fps).
        var fps = SelectedExtractSpeed.FramesPerSecond > 0 ? SelectedExtractSpeed.FramesPerSecond
            : detection.Video.FrameRate > 1 ? detection.Video.FrameRate : 24;
        BeginSessionJob(SessionJobKind.Extract, video.FullPath, null);
        await RunBurnedInAsync("Extracting burned-in subtitles", async ct =>
        {
            var started = DateTime.UtcNow;
            ExtractionProgress? last = null;
            _s.Log.Info("Extract", $"{video.Name}: {fps:0.###} checks per second, area {_bandTopPercent:0.#}%-{_bandBottomPercent:0.#}%, clean-up {(_burnedInCleanup ? "on" : "off")}, OCR {_s.BurnedIn.Ocr.LanguageTag}.");
            var progress = new Progress<ExtractionProgress>(p =>
            {
                last = p;
                BurnedInProgress = p.Fraction * 100;
                var elapsed = DateTime.UtcNow - started;
                var eta = p.Fraction > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) : (TimeSpan?)null;
                BurnedInStatus = $"Reading subtitles... {p.Fraction:P0}"
                    + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty)
                    + (p.LastText is { } t ? $"  ·  “{t.Replace('\n', ' ')}”" : string.Empty);
            });

            var options = new ExtractionOptions(_bandTopPercent / 100, _bandBottomPercent / 100, fps,
                detection.Result.SamplesWithSubtitles > 0 ? detection.Result.LineHeight : null, _burnedInCleanup, LeaveOutAds);
            var cues = await _s.BurnedIn.ExtractAsync(ffmpeg, video.FullPath, detection.Video, options, progress, ct);

            if (cues.Count == 0)
            {
                BurnedInStatus = "No subtitle text could be read in that area. Check the highlighted area and the OCR language.";
                SetStatus(BurnedInStatus, StatusKind.Warning);
                return;
            }

            var took = DateTime.UtcNow - started;
            _s.Log.Success("Extract", $"{video.Name}: {cues.Count} cues from {last?.FramesRead ?? 0} frames checked ({last?.FramesRecognized ?? 0} read by OCR), "
                + $"took {FormatEta(took)} ({(last?.FramesRead ?? 0) / Math.Max(0.1, took.TotalSeconds):0} frames/s).");
            var language = _s.BurnedIn.Ocr.LanguageTag?.Split('-')[0].ToLowerInvariant();
            OpenExtractedSubtitle(cues, video, language);
            BurnedInStatus = $"Extracted {cues.Count} cues. They are open in the Subtitles tab, unsaved: review and save.";
            SetStatus($"Extracted {cues.Count} cues from the picture of {video.Name}. Review them, then save.", StatusKind.Success);
        });
        EndSessionJob();
    }

    private async Task RunBurnedInAsync(string what, Func<CancellationToken, Task> work)
    {
        _burnedInCts = new CancellationTokenSource();
        BurnedInBusy = true;
        BurnedInProgress = 0;
        BurnedInStatus = what + "...";
        RelayCommand.Refresh();
        try
        {
            await work(_burnedInCts.Token);
        }
        catch (OperationCanceledException)
        {
            BurnedInStatus = "Cancelled.";
            SetStatus(what + " cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            BurnedInStatus = ex.Message;
            SetStatus($"{what} failed: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            BurnedInBusy = false;
            _burnedInCts.Dispose();
            _burnedInCts = null;
            RelayCommand.Refresh();
        }
    }

    private static string FormatEta(TimeSpan t) => t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes)} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";
}
