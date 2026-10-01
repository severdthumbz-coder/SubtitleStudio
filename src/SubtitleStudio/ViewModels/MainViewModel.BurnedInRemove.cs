using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.BurnedIn;
using SubtitleStudio.Services.Video;

using SubtitleStudio.Services;

namespace SubtitleStudio.ViewModels;

public sealed record RemovalMethodOption(RemovalMethod Method, string Label, string Description);

/// <summary>
/// Burned-in tab, step 4: remove the subtitles from the picture into a new video (never over the
/// original), with a 10-second preview and a side-by-side before/after player.
/// Work is in Services/BurnedIn/SubtitleRemover and Services/Video/VideoEncoders.
/// </summary>
public sealed partial class MainViewModel
{
    public static readonly TimeSpan PreviewLength = TimeSpan.FromSeconds(10);

    private RemovalMethodOption _removalMethod = null!;
    private VideoEncoder? _encoder;
    private ComparePlayerViewModel? _compare;
    private bool _showCompare;
    private string? _lastOutput;
    private IInpaintModel? _aiModel;
    private static readonly System.Net.Http.HttpClient Http = CreateHttp();

    public IReadOnlyList<RemovalMethodOption> RemovalMethods { get; } = new[]
    {
        new RemovalMethodOption(RemovalMethod.Fill, "Fill in (sharpest)",
            "Letters, outline and shadow are painted over from the picture around them."),
        new RemovalMethodOption(RemovalMethod.FillSoften, "Fill in + soften (smoothest)",
            "Fill in, then a light blur over the area: hides remnants on busy pictures, slightly softer."),
        new RemovalMethodOption(RemovalMethod.AiFill, "AI fill on the graphics card (best, slowest)",
            "An AI model repaints the picture behind the text where it has detail (grass, buildings, faces); smooth areas use Fill in. Much slower: try Preview 10 s first."),
    };

    public RemovalMethodOption SelectedRemovalMethod
    {
        get => _removalMethod;
        set
        {
            if (!SetProperty(ref _removalMethod, value ?? RemovalMethods[0])) return;
            OnPropertyChanged(nameof(IsAiSelected));
            OnPropertyChanged(nameof(AiNeedsModel));
            RelayCommand.Refresh();
        }
    }

    // ---------------- AI fill model ----------------

    public bool IsAiSelected => _removalMethod.Method == RemovalMethod.AiFill;

    public bool AiAvailable => _s.LoadInpaintModel is not null;

    public bool AiModelInstalled => _s.InpaintModels.IsInstalled;

    /// <summary>AI fill is chosen but its model file isn't there yet.</summary>
    public bool AiNeedsModel => IsAiSelected && !AiModelInstalled;

    public string AiStatusText => !AiAvailable
        ? "AI fill is not available in this build."
        : !AiModelInstalled
            ? "AI fill needs its model: LaMa, 90 MB, downloaded once from the OpenCV model zoo (GitHub) into the models folder next to the app."
            : _aiModel is { } m ? $"AI model ready: runs with {m.DeviceLabel}." : "AI model installed. It loads onto the graphics card at the first AI preview.";

    public AsyncRelayCommand DownloadAiModelCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportAiModelCommand { get; private set; } = null!;

    private static System.Net.Http.HttpClient CreateHttp()
    {
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SubtitleStudio/{Services.AppInfo.Version}");
        return http;
    }

    private void RaiseAiState()
    {
        OnPropertyChanged(nameof(AiModelInstalled));
        OnPropertyChanged(nameof(AiNeedsModel));
        OnPropertyChanged(nameof(AiStatusText));
        RelayCommand.Refresh();
    }

    private async Task DownloadAiModelAsync()
    {
        await RunBurnedInAsync("Downloading the AI model", async ct =>
        {
            var progress = new Progress<double>(f =>
            {
                BurnedInProgress = f * 100;
                BurnedInStatus = $"Downloading the AI model... {f:P0} of 90 MB";
            });
            await _s.InpaintModels.DownloadAsync(Http, progress, ct);
            BurnedInStatus = "AI model downloaded and checked. AI fill is ready.";
            SetStatus("AI model downloaded (LaMa, OpenCV model zoo).", StatusKind.Success);
        });
        RaiseAiState();
    }

    private async Task ImportAiModelAsync()
    {
        var file = _s.Dialogs.PickFile("Choose the LaMa model file (inpainting_lama_2025jan.onnx)", "ONNX model (*.onnx)|*.onnx");
        if (file is null) return;
        await RunBurnedInAsync("Checking the model file", async ct =>
        {
            await _s.InpaintModels.ImportAsync(file, ct);
            BurnedInStatus = "AI model checked and copied into the models folder. AI fill is ready.";
            SetStatus("AI model added.", StatusKind.Success);
        });
        RaiseAiState();
    }

    /// <summary>Loads the model once (onto the graphics card); later runs reuse it.</summary>
    private async Task<AiInpainter?> AiInpainterAsync(CancellationToken ct)
    {
        if (!IsAiSelected) return null;
        if (_s.LoadInpaintModel is not { } load || !_s.InpaintModels.IsInstalled)
            throw new InvalidOperationException("AI fill needs its model first: use Download model in step 4.");
        if (_aiModel is null)
        {
            // The first time on a PC the load also measures the graphics card's setups (about a minute).
            BurnedInStatus = "Loading the AI model onto the graphics card (the first time, it also measures the card: about a minute)...";
            var path = _s.InpaintModels.ModelPath;
            var gpu = _gpu;
            int batch = _plan.AiPatchBatch;
            var started = DateTime.UtcNow;
            _aiModel = await Task.Run(() => load(path, gpu, batch), ct);
            var took = DateTime.UtcNow - started;
            if (_aiModel.DeviceLabel.StartsWith("processor", StringComparison.OrdinalIgnoreCase))
                _s.Log.Warning("AI", $"AI model loaded on the {_aiModel.DeviceLabel} in {took.TotalSeconds:0.0} s: AI fill will be very slow.");
            else
                _s.Log.Info("AI", $"AI model loaded: {_aiModel.DeviceLabel}, {_aiModel.BatchSize} patches per call, in {took.TotalSeconds:0.0} s.");
            OnPropertyChanged(nameof(AiStatusText));
        }
        return new AiInpainter(_aiModel);
    }

    /// <summary>App exit: players and the AI model hold native resources.</summary>
    public void ReleaseResources()
    {
        _compare?.Dispose();
        _aiModel?.Dispose();
        _aiModel = null;
    }

    public string EncoderText => _encoder is null
        ? "Encoder: chosen at the first preview (graphics card when it works, else processor)."
        : $"Encoder: {_encoder.Label}";

    /// <summary>The before / after players (null when a second player can't be created).</summary>
    public ComparePlayerViewModel? Compare => _compare;

    /// <summary>Show the before / after players instead of the still frame.</summary>
    public bool ShowCompare
    {
        get => _showCompare;
        set
        {
            if (value && _compare?.IsLoaded != true) value = false;
            if (!SetProperty(ref _showCompare, value)) return;
            if (!value) _compare?.Detach();
            OnPropertyChanged(nameof(ShowFrame));
        }
    }

    public bool ShowFrame => !_showCompare;

    public bool HasComparison => _compare?.IsLoaded == true;

    public bool HasRemovalOutput => _lastOutput is not null && File.Exists(_lastOutput);

    public AsyncRelayCommand PreviewRemovalCommand { get; private set; } = null!;
    public AsyncRelayCommand CreateCleanVideoCommand { get; private set; } = null!;
    public RelayCommand ShowCompareCommand { get; private set; } = null!;
    public RelayCommand ShowFrameCommand { get; private set; } = null!;
    public RelayCommand RevealRemovalOutputCommand { get; private set; } = null!;

    private void InitBurnedInRemove()
    {
        _removalMethod = RemovalMethods[0];
        PreviewRemovalCommand = new AsyncRelayCommand(PreviewRemovalAsync, () => CanRunBurnedIn && _detection is not null && !AiNeedsModel);
        CreateCleanVideoCommand = new AsyncRelayCommand(() => CreateCleanVideoAsync(null), () => CanRunBurnedIn && _detection is not null && !AiNeedsModel);
        DownloadAiModelCommand = new AsyncRelayCommand(DownloadAiModelAsync, () => !BurnedInBusy && AiAvailable && !AiModelInstalled);
        ImportAiModelCommand = new AsyncRelayCommand(ImportAiModelAsync, () => !BurnedInBusy && AiAvailable);
        ShowCompareCommand = new RelayCommand(() => ShowCompare = true, () => HasComparison);
        ShowFrameCommand = new RelayCommand(() => ShowCompare = false);
        RevealRemovalOutputCommand = new RelayCommand(() => { if (_lastOutput is { } o) _s.Dialogs.RevealInExplorer(o); }, () => HasRemovalOutput);

        if (_s.CreatePlayer is { } create)
        {
            _compare = new ComparePlayerViewModel(create(), create(), RunOnUi);
            _compare.Failed += m => SetStatus(m, StatusKind.Error);
        }
    }

    private RemovalOptions CurrentRemovalOptions(DetectionRun detection, TimeSpan? start, TimeSpan? duration)
    {
        var r = detection.Result;
        double lineHeight = r.SamplesWithSubtitles > 0 ? r.LineHeight : Services.BurnedIn.BurnedInAnalyzer.DefaultLineHeight;
        return new RemovalOptions(_bandTopPercent / 100, _bandBottomPercent / 100, lineHeight, _burnedInCleanup,
            SelectedRemovalMethod.Method, start, duration);
    }

    private async Task<VideoEncoder> EncoderAsync(string ffmpeg, CancellationToken ct)
    {
        if (_encoder is null)
        {
            BurnedInStatus = "Checking which video encoder works on this PC...";
            _encoder = await VideoEncoders.PickAsync(ffmpeg, _gpu?.Primary?.Vendor, ct, _s.Log);
            OnPropertyChanged(nameof(EncoderText));
        }
        return _encoder;
    }

    private static string PreviewFolder => Path.Combine(Path.GetTempPath(), "SubtitleStudio", "previews");

    /// <summary>Renders 10 s from the frame picker's moment to a temporary file and shows it next to the original.</summary>
    private async Task PreviewRemovalAsync()
    {
        var video = BurnedInVideo;
        var detection = _detection;
        if (video is null || detection is null || FfmpegStatus.FfmpegPath is not { } ffmpeg) return;
        CancelFrameRead();
        ShowCompare = false;
        _compare?.Unload();

        var start = _frameTime;
        if (start + PreviewLength > detection.Video.Duration)
            start = detection.Video.Duration > PreviewLength ? detection.Video.Duration - PreviewLength : TimeSpan.Zero;

        await RunBurnedInAsync("Rendering a 10-second preview", async ct =>
        {
            var encoder = await EncoderAsync(ffmpeg, ct);
            var folder = Directory.CreateDirectory(PreviewFolder).FullName;
            foreach (var old in Directory.GetFiles(folder, "preview-*.mkv"))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var output = Path.Combine(folder, $"preview-{DateTime.Now:HHmmss}.mkv");

            var ai = await AiInpainterAsync(ct);
            var options = CurrentRemovalOptions(detection, start, PreviewLength);
            LogRemovalStart("Preview", video.Name, options, encoder, output);
            var started = DateTime.UtcNow;
            var result = await new SubtitleRemover(ai, _plan, _s.Log).RenderAsync(ffmpeg, video.FullPath, detection.Video, output, encoder,
                options, RemovalProgressReporter("Preview"), ct);
            LogRemovalDone("Preview", result, DateTime.UtcNow - started);

            OpenComparison(video.FullPath, result.OutputPath, start, PreviewLength, start);
            BurnedInStatus = $"Preview ready: text found in {result.FramesWithText} of {result.Frames} frames{AiSummary(result)}. Play it side by side; if it looks right, Create video.";
            SetStatus($"Preview of the removal ready ({encoder.Label}).", StatusKind.Success);
        });
    }

    /// <summary>
    /// Renders the whole video to a new file chosen by the user (suggested: "Name (no subs).ext"), or to
    /// <paramref name="restartOutput"/> when restarting a Create video interrupted by a crash.
    /// </summary>
    private async Task CreateCleanVideoAsync(string? restartOutput)
    {
        var video = BurnedInVideo;
        var detection = _detection;
        if (video is null || detection is null || FfmpegStatus.FfmpegPath is not { } ffmpeg) return;

        var suggested = SubtitleRemover.SuggestedOutputName(video.FullPath);
        var ext = Path.GetExtension(suggested);
        var output = restartOutput ?? _s.Dialogs.SaveFile("Save the video without burned-in subtitles",
            $"Video (*{ext})|*{ext}", 1, Path.GetDirectoryName(video.FullPath), suggested);
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(video.FullPath), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Choose a new file name: the original video is never overwritten.", StatusKind.Warning);
            return;
        }

        if (!PreflightDrives(video.FullPath, output)) return;

        CancelFrameRead();
        ShowCompare = false;
        _compare?.Unload();
        var at = _frameTime;

        BeginSessionJob(SessionJobKind.CreateVideo, video.FullPath, output);
        await RunBurnedInAsync("Removing the subtitles from the video", async ct =>
        {
            var encoder = await EncoderAsync(ffmpeg, ct);
            var started = DateTime.UtcNow;
            var ai = await AiInpainterAsync(ct);
            var options = CurrentRemovalOptions(detection, null, null);
            LogRemovalStart("Create video", video.Name, options, encoder, output);
            var result = await new SubtitleRemover(ai, _plan, _s.Log).RenderAsync(ffmpeg, video.FullPath, detection.Video, output, encoder,
                options, RemovalProgressReporter("Removing"), ct);
            LogRemovalDone("Create video", result, DateTime.UtcNow - started);

            _lastOutput = result.OutputPath;
            OnPropertyChanged(nameof(HasRemovalOutput));
            OpenComparison(video.FullPath, result.OutputPath, TimeSpan.Zero, detection.Video.Duration, at);
            var took = DateTime.UtcNow - started;
            BurnedInStatus = $"Done in {FormatEta(took)}: {Path.GetFileName(output)}. Text removed in {result.FramesWithText} of {result.Frames} frames{AiSummary(result)}.";
            SetStatus($"Saved {Path.GetFileName(output)} ({encoder.Label}). The original is unchanged.", StatusKind.Success);
            await ImportAsync(new[] { result.OutputPath }, "removal");
        });
        EndSessionJob();
        RelayCommand.Refresh();
    }

    /// <summary>
    /// Before a long render: enough free space for the new video (asks if not), and a note when the
    /// original or the output is on a network, USB or optical drive (slow, can stall).
    /// </summary>
    private bool PreflightDrives(string input, string output)
    {
        var source = Services.Hardware.HardwareDetector.DriveOf(input);
        var target = Services.Hardware.HardwareDetector.DriveOf(output);
        if (source is not null) _s.Log.Info("Remove", $"Original on {source.Summary}.");
        if (target is not null) _s.Log.Info("Remove", $"Output to {target.Summary}.");

        long inputBytes = File.Exists(input) ? new FileInfo(input).Length : 0;
        long needed = Services.Hardware.PerformancePlan.EstimateOutputBytes(inputBytes);
        if (target is { FreeBytes: > 0 } t && t.FreeBytes < needed)
        {
            _s.Log.Warning("Remove", $"Only {Services.Hardware.HardwareProfile.Bytes(t.FreeBytes)} free on {t.Root}; the new video needs about {Services.Hardware.HardwareProfile.Bytes(needed)}.");
            if (!_s.Dialogs.Confirm("Not much free space",
                    $"Only {Services.Hardware.HardwareProfile.Bytes(t.FreeBytes)} is free on {t.Root.TrimEnd('\\')}, and the new video will need about {Services.Hardware.HardwareProfile.Bytes(needed)}. "
                    + "If the drive fills up the video fails near the end. Continue anyway?"))
                return false;
        }
        foreach (var (drive, what) in new[] { (source, "The original video"), (target, "The output") })
            if (drive is { IsSlowOrRemote: true } d)
                _s.Log.Warning("Remove", $"{what} is on a {d.KindText} ({d.Root}): reading or writing can be slow or stall. A local drive is faster.");
        return true;
    }

    private void LogRemovalStart(string what, string name, RemovalOptions o, VideoEncoder encoder, string output)
        => _s.Log.Info("Remove", $"{what} {name}: {RemovalMethods.First(m => m.Method == o.Method).Label}, area {o.BandTop:P1}-{o.BandBottom:P1}, "
            + $"{(o.LightText ? "text found per frame" : "coloured text: whole area blurred")}, encoder {encoder.Name}"
            + (o.Start is { } st ? $", from {st:hh\\:mm\\:ss\\.f} for {(o.Duration ?? TimeSpan.Zero).TotalSeconds:0} s" : string.Empty)
            + $" -> {output}. {_plan.Summary}.");

    private void LogRemovalDone(string what, RemovalResult r, TimeSpan took)
    {
        long size = File.Exists(r.OutputPath) ? new FileInfo(r.OutputPath).Length : 0;
        _s.Log.Success("Remove", $"{what} done in {FormatEta(took)}: {r.Frames} frames ({r.Frames / Math.Max(0.1, took.TotalSeconds):0.0} frames/s), "
            + $"text in {r.FramesWithText}" + (r.AiPatches + r.AiPatchesSkipped + r.AiPatchesReused > 0 ? $", AI patches {r.AiPatches} (+{r.AiPatchesReused} reused from earlier frames, +{r.AiPatchesSkipped} smooth, ordinary fill)" : string.Empty)
            + $", encoder {r.Encoder.Name}, output {Services.Hardware.HardwareProfile.Bytes(size)}.");
    }

    private static string AiSummary(RemovalResult r)
        => r.AiPatches + r.AiPatchesSkipped + r.AiPatchesReused == 0 ? string.Empty : $"; AI repainted {r.AiPatches} patches, reused {r.AiPatchesReused} from earlier frames where the picture hadn't changed ({r.AiPatchesSkipped} smooth ones used Fill in)";

    private IProgress<RemovalProgress> RemovalProgressReporter(string what)
    {
        var started = DateTime.UtcNow;
        return new Progress<RemovalProgress>(p =>
        {
            BurnedInProgress = p.Fraction * 100;
            var elapsed = DateTime.UtcNow - started;
            var eta = p.Fraction > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) : (TimeSpan?)null;
            var speed = elapsed.TotalSeconds > 1 ? $", {p.Frames / elapsed.TotalSeconds:0} frames/s" : string.Empty;
            BurnedInStatus = $"{what}... {p.Fraction:P0}{speed}" + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty);
        });
    }

    private void OpenComparison(string original, string result, TimeSpan offset, TimeSpan length, TimeSpan at)
    {
        if (_compare is null || !_compare.Load(original, result, offset, length, at)) return;
        OnPropertyChanged(nameof(HasComparison));
        ShowCompare = true;
        RelayCommand.Refresh();
    }
}
