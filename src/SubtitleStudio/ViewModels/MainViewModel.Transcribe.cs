using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.ViewModels;

public sealed record TranscribeLanguageOption(string? Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record TranscribePartOption(string Label, TimeSpan? Length)
{
    public override string ToString() => Label;
}

public sealed record WhisperDeviceOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// AI Transcribe tab: speech in a video or audio file to timed subtitles, opened in the Subtitles editor.
/// The work is in Services/Transcription (whisper.cpp in the app); this file holds the tab's state.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly System.Net.Http.HttpClient ModelHttp = CreateModelHttp();

    private MediaItem? _transcribeFile;
    private IReadOnlyList<AudioTrack> _transcribeTracks = Array.Empty<AudioTrack>();
    private AudioTrack? _transcribeTrack;
    private TranscribePartOption _transcribePart = null!;
    private bool _transcribeBusy;
    private double _transcribeProgress;
    private string _transcribeStatus = "Pick a video or audio file, then press Transcribe.";
    private CancellationTokenSource? _transcribeCts;
    /// <summary>A run is going: late progress reports after it ended are ignored (they'd overwrite the result).</summary>
    private volatile bool _transcribeLive;
    private IReadOnlyList<InstalledWhisperModel> _whisperModels = Array.Empty<InstalledWhisperModel>();
    private WhisperCatalogEntry _catalogEntry = WhisperCatalog.Entries[0];
    private VulkanReport? _vulkan;
    private bool _vulkanRequested;
    private string? _lastRunText;

    public IReadOnlyList<ITranscriptionService> TranscriptionEngines => _s.TranscriptionEngines;

    public bool HasTranscriptionEngines => _s.TranscriptionEngines.Count > 0;

    private LocalWhisperEngine? LocalWhisper => _s.TranscriptionEngines.OfType<LocalWhisperEngine>().FirstOrDefault();

    // ---------------- File ----------------

    public IReadOnlyList<MediaItem> TranscribableFiles => Files.Where(f => f.Kind is MediaKind.Video or MediaKind.Audio).ToList();

    public MediaItem? TranscribeFile
    {
        get => _transcribeFile;
        set
        {
            if (!SetProperty(ref _transcribeFile, value)) return;
            TranscribeTracks = Array.Empty<AudioTrack>();
            if (!TranscribeBusy)
                TranscribeStatus = value is null ? "Pick a video or audio file, then press Transcribe." : "Ready. Press Transcribe.";
            _ = LoadTranscribeTracksAsync(value);
            OnPropertyChanged(nameof(TranscribeBlockedText));
            RelayCommand.Refresh();
        }
    }

    public IReadOnlyList<AudioTrack> TranscribeTracks
    {
        get => _transcribeTracks;
        private set
        {
            _transcribeTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSeveralAudioTracks));
            TranscribeTrack = value.FirstOrDefault();
        }
    }

    public bool HasSeveralAudioTracks => _transcribeTracks.Count > 1;

    public AudioTrack? TranscribeTrack
    {
        get => _transcribeTrack;
        set => SetProperty(ref _transcribeTrack, value);
    }

    private async Task LoadTranscribeTracksAsync(MediaItem? file)
    {
        if (file is null || FfmpegStatus.FfprobePath is not { } ffprobe) return;
        try
        {
            var tracks = await AudioExtractor.ListTracksAsync(ffprobe, file.FullPath, CancellationToken.None);
            if (ReferenceEquals(file, _transcribeFile)) TranscribeTracks = tracks;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _s.Log.Detail("Transcribe", $"Couldn't list the audio tracks of {file.Name}: {ex.Message}");
        }
    }

    // ---------------- What to write ----------------

    public IReadOnlyList<TranscribeLanguageOption> TranscribeLanguages { get; } =
        new[] { new TranscribeLanguageOption(null, "Detect automatically") }
            .Concat(WhisperLanguages.All.Select(l => new TranscribeLanguageOption(l.Code, l.Name))).ToList();

    public TranscribeLanguageOption SelectedTranscribeLanguage
    {
        get => TranscribeLanguages.FirstOrDefault(l => (l.Code ?? "auto") == _s.Config.TranscribeLanguage) ?? TranscribeLanguages[0];
        set
        {
            var code = value?.Code ?? "auto";
            if (_s.Config.TranscribeLanguage == code) return;
            _s.Config.TranscribeLanguage = code;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Write English subtitles for speech in another language (Whisper translates as it listens).</summary>
    public bool TranslateToEnglish
    {
        get => _s.Config.TranscribeTranslate;
        set
        {
            if (_s.Config.TranscribeTranslate == value) return;
            _s.Config.TranscribeTranslate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WriteSpokenLanguage));
            OnPropertyChanged(nameof(WhisperModelNote));
            OnPropertyChanged(nameof(HasWhisperModelNote));
            SaveSettings();
        }
    }

    public bool WriteSpokenLanguage
    {
        get => !TranslateToEnglish;
        set => TranslateToEnglish = !value;
    }

    public IReadOnlyList<TranscribePartOption> TranscribeParts { get; } = new[]
    {
        new TranscribePartOption("The whole file", null),
        new TranscribePartOption("First 2 minutes (quick test)", TimeSpan.FromMinutes(2)),
        new TranscribePartOption("First 5 minutes", TimeSpan.FromMinutes(5)),
        new TranscribePartOption("First 15 minutes", TimeSpan.FromMinutes(15)),
    };

    public TranscribePartOption SelectedTranscribePart
    {
        get => _transcribePart;
        set => SetProperty(ref _transcribePart, value ?? TranscribeParts[0]);
    }

    // ---------------- Model ----------------

    public IReadOnlyList<InstalledWhisperModel> WhisperModels
    {
        get => _whisperModels;
        private set
        {
            _whisperModels = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedWhisperModel));
            RaiseWhisperModelState();
        }
    }

    public InstalledWhisperModel? SelectedWhisperModel
    {
        get => _whisperModels.FirstOrDefault(m => string.Equals(m.FileName, _s.Config.WhisperModel, StringComparison.OrdinalIgnoreCase))
               ?? _whisperModels.FirstOrDefault();
        set
        {
            if (value is null || string.Equals(_s.Config.WhisperModel, value.FileName, StringComparison.OrdinalIgnoreCase)) return;
            _s.Config.WhisperModel = value.FileName;
            OnPropertyChanged();
            RaiseWhisperModelState();
            SaveSettings();
        }
    }

    public bool HasWhisperModel => _whisperModels.Count > 0;

    public bool NeedsWhisperModel => _whisperModels.Count == 0;

    public IReadOnlyList<WhisperCatalogEntry> WhisperCatalogEntries => WhisperCatalog.Entries;

    public WhisperCatalogEntry SelectedCatalogEntry
    {
        get => _catalogEntry;
        set
        {
            if (!SetProperty(ref _catalogEntry, value ?? WhisperCatalog.Entries[0])) return;
            OnPropertyChanged(nameof(DownloadModelLabel));
        }
    }

    public string DownloadModelLabel
    {
        get
        {
            long partial = _s.WhisperModels.PartialBytes(_catalogEntry);
            return partial > 0 ? $"Continue download ({_catalogEntry.SizeText})" : $"Download ({_catalogEntry.SizeText})";
        }
    }

    /// <summary>Warnings about the chosen model for the chosen task.</summary>
    public string WhisperModelNote
    {
        get
        {
            if (SelectedWhisperModel is not { } m) return string.Empty;
            var h = m.Header;
            if (!h.Multilingual)
                return "This model understands English only. For other languages use a multilingual model (any without \".en\").";
            if (TranslateToEnglish && h.IsTurbo)
                return "Large v3 Turbo translates to English poorly (it wasn't trained to). For English subtitles of other languages use Large v3 or Medium.";
            if (h.Size is WhisperSize.Tiny or WhisperSize.Base)
                return "A small model: fast, but expect mistakes. Large v3 Turbo is much more accurate.";
            return string.Empty;
        }
    }

    public bool HasWhisperModelNote => WhisperModelNote.Length > 0;

    public string WhisperModelsFolder => _s.WhisperModels.Folder;

    public AsyncRelayCommand DownloadWhisperModelCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportWhisperModelCommand { get; private set; } = null!;
    public RelayCommand OpenWhisperModelsFolderCommand { get; private set; } = null!;

    private void RaiseWhisperModelState()
    {
        OnPropertyChanged(nameof(HasWhisperModel));
        OnPropertyChanged(nameof(NeedsWhisperModel));
        OnPropertyChanged(nameof(WhisperModelNote));
        OnPropertyChanged(nameof(HasWhisperModelNote));
        OnPropertyChanged(nameof(DownloadModelLabel));
        OnPropertyChanged(nameof(TranscribeBlockedText));
        RelayCommand.Refresh();
    }

    private void RefreshWhisperModels()
    {
        try
        {
            WhisperModels = _s.WhisperModels.List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WhisperModels = Array.Empty<InstalledWhisperModel>();
            _s.Log.Warning("Transcribe", "The Whisper models folder couldn't be read: " + ex.Message);
        }
    }

    private static System.Net.Http.HttpClient CreateModelHttp()
    {
        // Models are large (up to 3 GB): no overall timeout; Cancel stops it, and it resumes later.
        var http = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SubtitleStudio/{AppInfo.Version}");
        return http;
    }

    private async Task DownloadWhisperModelAsync()
    {
        var entry = _catalogEntry;
        await RunTranscribeWorkAsync($"Downloading {entry.Title}", async ct =>
        {
            var started = DateTime.UtcNow;
            long startBytes = _s.WhisperModels.PartialBytes(entry);
            if (startBytes > 0) _s.Log.Info("Transcribe", $"Continuing the download of {entry.FileName} from {startBytes / (1024.0 * 1024):0} MB.");
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (!_transcribeLive) return;
                TranscribeProgress = p.Fraction * 100;
                if (p.Phase == ModelDownloadPhase.Checking)
                {
                    TranscribeStatus = $"Checking the download (SHA-256)... {p.Fraction:P0}";
                    return;
                }
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                double rate = elapsed > 1 ? (p.Done - startBytes) / elapsed : 0;
                var eta = rate > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / rate) : (TimeSpan?)null;
                TranscribeStatus = $"Downloading {entry.Title}... {p.Done / (1024.0 * 1024):0} of {p.Total / (1024.0 * 1024):0} MB"
                    + (rate > 0 ? $", {rate / (1024 * 1024):0.0} MB/s" : string.Empty)
                    + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty);
            });
            var model = await _s.WhisperModels.DownloadAsync(entry, ModelHttp, progress, ct);
            _s.Config.WhisperModel = model.FileName;
            SaveSettings();
            RefreshWhisperModels();
            TranscribeStatus = $"{model.Header.Name} downloaded and checked. Ready to transcribe.";
            _s.Log.Success("Transcribe", $"Model downloaded and checked: {model.FileName} ({model.SizeText}).");
            SetStatus($"Whisper model {model.Header.Name} is ready.", StatusKind.Success);
        });
        OnPropertyChanged(nameof(DownloadModelLabel));
    }

    private async Task ImportWhisperModelAsync()
    {
        var file = _s.Dialogs.PickFile("Choose a whisper.cpp model (ggml-*.bin)", "Whisper model (ggml-*.bin)|*.bin|All files|*.*");
        if (file is null) return;
        await RunTranscribeWorkAsync("Adding the model", async ct =>
        {
            TranscribeStatus = "Checking and copying the model into the models folder...";
            var model = await _s.WhisperModels.ImportAsync(file, ct);
            _s.Config.WhisperModel = model.FileName;
            SaveSettings();
            RefreshWhisperModels();
            TranscribeStatus = $"{model.Header.Name} added. Ready to transcribe.";
            _s.Log.Success("Transcribe", $"Model added: {model.FileName} ({model.Header.Name}, {model.SizeText}).");
            SetStatus($"Whisper model {model.Header.Name} is ready.", StatusKind.Success);
        });
    }

    // ---------------- Device ----------------

    public IReadOnlyList<WhisperDeviceOption> WhisperDeviceOptions
    {
        get
        {
            var list = new List<WhisperDeviceOption>();
            if (_vulkan is null)
                list.Add(new WhisperDeviceOption("auto", "Automatic (checking the graphics hardware...)"));
            else
            {
                list.Add(new WhisperDeviceOption("auto", _vulkan.Best is { } best
                    ? $"Automatic: {best.Name}"
                    : "Automatic: processor (no Vulkan graphics device found)"));
                foreach (var d in _vulkan.Gpus) list.Add(new WhisperDeviceOption(d.Name, d.Label));
            }
            list.Add(new WhisperDeviceOption("cpu", "Processor only (slow)"));
            var saved = _s.Config.WhisperDevice;
            if (list.All(o => o.Key != saved)) list.Insert(1, new WhisperDeviceOption(saved, $"{saved} (not found now)"));
            return list;
        }
    }

    public WhisperDeviceOption SelectedWhisperDevice
    {
        get => WhisperDeviceOptions.FirstOrDefault(o => o.Key == _s.Config.WhisperDevice) ?? WhisperDeviceOptions[0];
        set
        {
            var key = value?.Key ?? "auto";
            if (_s.Config.WhisperDevice == key) return;
            _s.Config.WhisperDevice = key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WhisperDeviceText));
            SaveSettings();
        }
    }

    /// <summary>What the Vulkan driver reports, and what the last run used.</summary>
    public string WhisperDeviceText
    {
        get
        {
            if (_vulkan is null) return "Checking the graphics hardware...";
            var lines = new List<string>();
            if (_vulkan.Problem is { } problem) lines.Add(problem);
            else if (!_vulkan.Gpus.Any()) lines.Add("Vulkan found no graphics card or built-in GPU: Whisper runs on the processor (much slower).");
            else lines.Add("Whisper runs on the graphics card through Vulkan (AMD, NVIDIA and Intel all work).");
            if (_lastRunText is not null) lines.Add(_lastRunText);
            return string.Join(" ", lines);
        }
    }

    /// <summary>Lists the Vulkan devices once, the first time Transcribe (or its settings) is shown.</summary>
    private void EnsureVulkanListed()
    {
        if (_vulkanRequested) return;
        _vulkanRequested = true;
        var list = _s.ListVulkanDevices;
        _ = Task.Run(() => list()).ContinueWith(t => RunOnUi(() =>
        {
            _vulkan = t.IsCompletedSuccessfully ? t.Result : new VulkanReport(Array.Empty<VulkanDevice>(), "The graphics hardware couldn't be checked: " + t.Exception?.GetBaseException().Message);
            foreach (var d in _vulkan.Devices)
                _s.Log.Detail("Transcribe", $"Vulkan device {d.Index}: {d.Label}.");
            if (_vulkan.Problem is { } p) _s.Log.Detail("Transcribe", p);
            OnPropertyChanged(nameof(WhisperDeviceOptions));
            OnPropertyChanged(nameof(SelectedWhisperDevice));
            OnPropertyChanged(nameof(WhisperDeviceText));
        }), TaskScheduler.Default);
    }

    /// <summary>The device to use now: the saved choice, the best GPU for "auto", else the processor.</summary>
    private WhisperDevice ResolveWhisperDevice()
    {
        var key = _s.Config.WhisperDevice;
        var report = _vulkan ?? _s.ListVulkanDevices();
        _vulkan ??= report;
        if (key == "cpu") return WhisperDevice.Processor;
        var chosen = key == "auto" ? report.Best : report.Gpus.FirstOrDefault(d => d.Name == key) ?? report.Best;
        return chosen is null ? WhisperDevice.Processor : WhisperDevice.Gpu(chosen);
    }

    // ---------------- Engine choice ----------------

    /// <summary>Remembered answer to "which engine?" (Settings).</summary>
    public string RememberedEngineText
    {
        get
        {
            var id = _s.Config.TranscriptionEngine;
            if (string.IsNullOrEmpty(id)) return "When more than one engine can transcribe (this PC, and online services with a saved API key), the app asks which to use.";
            var name = _s.TranscriptionEngines.FirstOrDefault(e => e.Id == id)?.DisplayName ?? id;
            return $"Remembered choice: {name}. The app uses it without asking while it's available.";
        }
    }

    public bool HasRememberedEngine => !string.IsNullOrEmpty(_s.Config.TranscriptionEngine);

    public RelayCommand ForgetEngineChoiceCommand { get; private set; } = null!;

    /// <summary>The engine to use: the only usable one, the remembered one, or the user's pick. Null: cancelled.</summary>
    private ITranscriptionService? ChooseTranscriptionEngine()
    {
        var decision = EngineChooser.Decide(_s.TranscriptionEngines, provider => _s.ApiKeys.GetKey(provider) is not null, _s.Config.TranscriptionEngine);
        if (decision.Engine is { } engine) return engine;
        if (!decision.MustAsk) return null;
        var picked = _s.Dialogs.ChooseEngine("Transcribe", decision.Choices.Select(e => new EngineChoice(e.Id, e.DisplayName, e.Description)).ToList(), out bool remember);
        if (picked is null) return null;
        if (remember)
        {
            _s.Config.TranscriptionEngine = picked.Id;
            SaveSettings();
            OnPropertyChanged(nameof(RememberedEngineText));
            OnPropertyChanged(nameof(HasRememberedEngine));
            RelayCommand.Refresh();
        }
        return decision.Choices.First(e => e.Id == picked.Id);
    }

    // ---------------- Run ----------------

    public bool TranscribeBusy
    {
        get => _transcribeBusy;
        private set
        {
            if (!SetProperty(ref _transcribeBusy, value)) return;
            RelayCommand.Refresh();
        }
    }

    public double TranscribeProgress
    {
        get => _transcribeProgress;
        private set => SetProperty(ref _transcribeProgress, value);
    }

    public string TranscribeStatus
    {
        get => _transcribeStatus;
        private set => SetProperty(ref _transcribeStatus, value);
    }

    /// <summary>The last lines heard while transcribing (a live preview).</summary>
    public ObservableCollection<string> TranscribeLiveLines { get; } = new();

    public AsyncRelayCommand TranscribeCommand { get; private set; } = null!;
    public RelayCommand CancelTranscribeCommand { get; private set; } = null!;

    private bool CanTranscribe => TranscribeFile is not null && !TranscribeBusy && !TranslateBusy && !BatchBusy && FfmpegStatus.HasFfmpeg
                                  && (HasWhisperModel || _s.TranscriptionEngines.Any(e => e.RequiresApiKey));

    /// <summary>Why Transcribe can't start (empty when it can).</summary>
    public string TranscribeBlockedText => !FfmpegStatus.HasFfmpeg
        ? "FFmpeg is needed to read the audio: set it in Settings > Engines and tools."
        : !HasWhisperModel ? "Add a Whisper model first (step 2)."
        : TranscribeFile is null ? "Pick a file."
        : TranslateBusy ? "Wait for Translate to finish."
        : BatchBusy ? "Wait for the Batch queue to finish." : string.Empty;

    private async Task TranscribeAsync()
    {
        var file = TranscribeFile;
        if (file is null) return;
        if (!ConfirmDiscardChanges()) return;
        var engine = ChooseTranscriptionEngine();
        if (engine is null)
        {
            if (!_s.TranscriptionEngines.Any()) SetStatus("No transcription engine is available in this build.", StatusKind.Error);
            return;
        }

        string? modelPath = null;
        if (engine is LocalWhisperEngine local)
        {
            if (SelectedWhisperModel is not { } model)
            {
                SetStatus("Add a Whisper model first: download one in step 2 of AI Transcribe.", StatusKind.Warning);
                return;
            }
            modelPath = model.Path;
            local.Device = ResolveWhisperDevice();
            // One model in graphics memory at a time: the translator's language model goes first.
            LocalTranslator?.Runner.Release();
        }

        var part = SelectedTranscribePart;
        var language = SelectedTranscribeLanguage.Code;
        var options = new TranscriptionOptions(language, language is null, modelPath, TranslateToEnglish,
            TranscribeTrack?.Number ?? 0, null, part.Length, file.Duration);

        BeginSessionJob(SessionJobKind.Transcribe, file.FullPath, null);
        await RunTranscribeWorkAsync("Transcribing", async ct =>
        {
            TranscribeLiveLines.Clear();
            var started = DateTime.UtcNow;
            var progress = new Progress<EngineProgress>(p =>
            {
                if (!_transcribeLive) return;
                TranscribeProgress = p.Fraction * 100;
                var elapsed = DateTime.UtcNow - started;
                var eta = p.Fraction > 0.15 && elapsed.TotalSeconds > 5
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) : (TimeSpan?)null;
                TranscribeStatus = p.Message + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty);
                if (p.LatestText is { Length: > 0 } text && (TranscribeLiveLines.Count == 0 || TranscribeLiveLines[^1] != text))
                {
                    TranscribeLiveLines.Add(text);
                    while (TranscribeLiveLines.Count > 8) TranscribeLiveLines.RemoveAt(0);
                }
            });

            var doc = await engine.TranscribeAsync(file.FullPath, options, progress, ct);
            if (engine is LocalWhisperEngine { LastStats: { } st })
            {
                _lastRunText = $"Last run: {st.Device}, {st.Speed:0.0}x real time.";
                OnPropertyChanged(nameof(WhisperDeviceText));
                if (st.DeviceProblem is { } problem) SetStatus(problem, StatusKind.Warning);
            }
            if (doc.Cues.Count == 0)
            {
                TranscribeStatus = "No speech was found" + (part.Length is null ? "." : " in that part.") + " Check the audio track and the language.";
                SetStatus(TranscribeStatus, StatusKind.Warning);
                return;
            }
            OpenExtractedSubtitle(doc.Cues, file, doc.Language);
            var what = TranslateToEnglish ? "English subtitles" : $"{WhisperLanguages.NameOf(doc.Language)} subtitles";
            TranscribeStatus = $"Done: {doc.Cues.Count} cues ({what}). They are open in the Subtitles tab, unsaved: review and save.";
            SetStatus($"Transcribed {file.Name}: {doc.Cues.Count} cues ({what}). Review them in the Subtitles tab, then save.", StatusKind.Success);
        });
        EndSessionJob();
    }

    private async Task RestartTranscribeAsync(SessionJob job)
    {
        var file = Files.FirstOrDefault(f => string.Equals(f.FullPath, job.VideoPath, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            SetStatus($"The interrupted Transcribe couldn't be restarted: {Path.GetFileName(job.VideoPath)} is no longer available.", StatusKind.Warning);
            return;
        }
        SelectedTabIndex = Tabs.Transcribe;
        TranscribeFile = file;
        _s.Log.Info("Session", $"Restarting the interrupted Transcribe of {file.Name}.");
        await TranscribeAsync();
    }

    private async Task RunTranscribeWorkAsync(string what, Func<CancellationToken, Task> work)
    {
        _transcribeCts = new CancellationTokenSource();
        TranscribeBusy = true;
        TranscribeProgress = 0;
        TranscribeStatus = what + "...";
        try
        {
            _transcribeLive = true;
            try
            {
                await work(_transcribeCts.Token);
            }
            finally
            {
                _transcribeLive = false;
            }
        }
        catch (OperationCanceledException)
        {
            TranscribeStatus = "Cancelled.";
            SetStatus(what + " cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
        {
            TranscribeStatus = ex.Message;
            SetStatus($"{what} failed: {ex.Message}", StatusKind.Error);
            _s.Log.Error("Transcribe", $"{what} failed: {ex}");
        }
        finally
        {
            TranscribeBusy = false;
            _transcribeCts.Dispose();
            _transcribeCts = null;
            OnPropertyChanged(nameof(TranscribeBlockedText));
        }
    }

    private void OnTabSelected(int tab)
    {
        if (tab == Tabs.Transcribe || tab == Tabs.Translate || tab == Tabs.Settings) EnsureVulkanListed();
        if (tab == Tabs.Translate) RaiseTranslateSource();
    }

    private void InitTranscribe()
    {
        _transcribePart = TranscribeParts[0];
        _catalogEntry = WhisperCatalog.Entries.First(e => e.Recommended);
        RefreshWhisperModels();

        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, () => CanTranscribe);
        CancelTranscribeCommand = new RelayCommand(() => _transcribeCts?.Cancel(), () => TranscribeBusy);
        DownloadWhisperModelCommand = new AsyncRelayCommand(DownloadWhisperModelAsync, () => !TranscribeBusy);
        ImportWhisperModelCommand = new AsyncRelayCommand(ImportWhisperModelAsync, () => !TranscribeBusy);
        OpenWhisperModelsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_s.WhisperModels.Folder);
            _s.Dialogs.OpenFolder(_s.WhisperModels.Folder);
        });
        ForgetEngineChoiceCommand = new RelayCommand(() =>
        {
            _s.Config.TranscriptionEngine = string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(RememberedEngineText));
            OnPropertyChanged(nameof(HasRememberedEngine));
            RelayCommand.Refresh();
        }, () => HasRememberedEngine);

        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TranscribableFiles));
            if (TranscribeFile is not null && !Files.Contains(TranscribeFile)) TranscribeFile = null;
            TranscribeFile ??= TranscribableFiles.FirstOrDefault();
        };
    }
}
