using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>How far the original sound goes down under the voice.</summary>
public sealed record DubbingDuckOption(DuckLevel Level, string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A speaking speed to choose.</summary>
public sealed record DubbingSpeedOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Dubbing tab: the Kokoro model download, the voice list with a preview of any line, an English voice
/// track made from the open subtitles (lines fitted to their time), and the voice mixed over the
/// original sound into a copy of the video. The work is in Services/Dubbing (EnglishG2P, KokoroTts,
/// VoiceTrack, LineFitter, DubMixer, TtsModelStore).
/// </summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _dubbingCts;
    private bool _dubbingBusy, _dubbingLive;
    private double _dubbingProgress;
    private string _dubbingStatus = "Ready.";
    private string _dubbingReport = string.Empty;
    private string _previewText = string.Empty;
    private bool _previewTextEdited;
    private KokoroTts? _kokoro;

    public IReadOnlyList<ITtsService> TtsEngines => _s.TtsEngines;

    public bool HasTtsEngines => _s.TtsEngines.Count > 0;

    // ---------------- Model ----------------

    public TtsCatalogEntry TtsModel => TtsModelStore.Model;

    public string DownloadTtsModelLabel
        => HasTtsModel ? "Downloaded" : $"Download ({_s.TtsModels.BytesToFetch / (1024.0 * 1024):0} MB)";

    public bool HasTtsModel => _s.TtsModels.IsReady;

    public bool NeedsTtsModel => !HasTtsModel;

    public string DubbingDeviceText
    {
        get
        {
            if (!HasTtsModel) return string.Empty;
            if (_kokoro is not null) return "Running on " + (_kokoro.DeviceLabel == "processor" ? "the processor" : _kokoro.DeviceLabel) + ".";
            return _gpu?.Primary is { IsSoftware: false } adapter && _gpu.Backend != AiBackend.Cpu
                ? $"Runs on the graphics card ({adapter.Name}), or the processor if the card can't run it. Loaded on first use."
                : "Runs on the processor. Loaded on first use.";
        }
    }

    public AsyncRelayCommand DownloadTtsModelCommand { get; private set; } = null!;
    public RelayCommand OpenTtsModelsFolderCommand { get; private set; } = null!;

    private void RefreshTtsModels()
    {
        OnPropertyChanged(nameof(HasTtsModel));
        OnPropertyChanged(nameof(NeedsTtsModel));
        OnPropertyChanged(nameof(DownloadTtsModelLabel));
        OnPropertyChanged(nameof(DubbingDeviceText));
        OnPropertyChanged(nameof(DubbingBlockedText));
        RelayCommand.Refresh();
    }

    private async Task DownloadTtsModelAsync()
    {
        var entry = TtsModelStore.Model;
        await RunDubbingWorkAsync($"Downloading {entry.Title}", async ct =>
        {
            var started = DateTime.UtcNow;
            long total = _s.TtsModels.BytesToFetch;
            var progress = new Progress<Services.Transcription.ModelDownloadProgress>(p =>
            {
                if (!_dubbingLive) return;
                DubbingProgress = p.Fraction * 100;
                if (p.Phase == Services.Transcription.ModelDownloadPhase.Checking)
                {
                    DubbingStatus = "Checking the download (SHA-256)...";
                    return;
                }
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                double rate = elapsed > 1 ? p.Done / elapsed : 0;
                DubbingStatus = $"Downloading {entry.Title}... {p.Done / (1024.0 * 1024):0} of {p.Total / (1024.0 * 1024):0} MB"
                    + (rate > 0 ? $", {rate / (1024 * 1024):0.0} MB/s" : string.Empty);
            });
            await _s.TtsModels.DownloadAsync(ModelHttp, progress, ct);
            RefreshTtsModels();
            DubbingStatus = $"{entry.Title} downloaded and checked. Choose a voice and press Preview.";
            _s.Log.Success("Dubbing", $"Voice model downloaded and checked: {entry.FileName} and {TtsModelStore.VoicesFile.FileName} ({total / (1024.0 * 1024):0} MB).");
            SetStatus("The dubbing voices are ready.", StatusKind.Success);
        });
        OnPropertyChanged(nameof(DownloadTtsModelLabel));
    }

    /// <summary>The voice model, loaded on first use.</summary>
    private async Task<KokoroTts> EnsureKokoroAsync()
    {
        if (_kokoro is not null) return _kokoro;
        if (!HasTtsModel) throw new InvalidOperationException("Download the voice model first.");
        if (_s.LoadKokoroModel is not { } load) throw new InvalidOperationException("Dubbing voices aren't available in this copy of the app.");
        DubbingStatus = "Loading the voice model...";
        string modelPath = _s.TtsModels.ModelPath, voicesPath = _s.TtsModels.VoicesPath;
        var gpu = _gpu ?? await Task.Run(GpuDetector.Detect);
        var started = DateTime.UtcNow;
        var tts = await Task.Run(() =>
        {
            var voices = KokoroVoices.Load(voicesPath);
            _ = EnglishG2P.Shared; // the pronunciation dictionary, loaded once
            return new KokoroTts(load(modelPath, n => voices.Style(KokoroVoices.English[0].Id, n), gpu), voices);
        });
        _kokoro = tts;
        _s.Log.Info("Dubbing", $"Voice model ready in {(DateTime.UtcNow - started).TotalSeconds:0.0} s ({tts.DeviceLabel}).");
        OnPropertyChanged(nameof(DubbingDeviceText));
        return tts;
    }

    // ---------------- Voice ----------------

    public IReadOnlyList<KokoroVoiceInfo> DubbingVoices => KokoroVoices.English;

    public KokoroVoiceInfo SelectedDubbingVoice
    {
        get => KokoroVoices.Find(_s.Config.DubbingVoice) ?? KokoroVoices.English[0];
        set
        {
            if (value is null || value.Id == _s.Config.DubbingVoice) return;
            _s.Config.DubbingVoice = value.Id;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<DubbingSpeedOption> DubbingSpeeds { get; } = new[]
    {
        new DubbingSpeedOption(0.9, "A little slower (0.9)"),
        new DubbingSpeedOption(1.0, "Normal"),
        new DubbingSpeedOption(1.1, "A little faster (1.1)"),
        new DubbingSpeedOption(1.2, "Faster (1.2)"),
        new DubbingSpeedOption(1.3, "Much faster (1.3)"),
    };

    public DubbingSpeedOption SelectedDubbingSpeed
    {
        get => DubbingSpeeds.FirstOrDefault(o => Math.Abs(o.Value - _s.Config.DubbingSpeed) < 0.01) ?? DubbingSpeeds[1];
        set
        {
            if (value is null || Math.Abs(value.Value - _s.Config.DubbingSpeed) < 0.01) return;
            _s.Config.DubbingSpeed = value.Value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>What Preview says: the selected subtitle line, or anything typed here.</summary>
    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (SetProperty(ref _previewText, value ?? string.Empty)) _previewTextEdited = true;
            RelayCommand.Refresh();
        }
    }

    public AsyncRelayCommand PreviewVoiceCommand { get; private set; } = null!;
    public RelayCommand StopPreviewCommand { get; private set; } = null!;
    public RelayCommand UseSelectedLineCommand { get; private set; } = null!;

    private void TakeSelectedLine(bool force)
    {
        if (!force && _previewTextEdited) return;
        var text = SelectedCue is { } cue ? Services.Subtitles.SubtitleText.StripTags(cue.Text).Replace("\r", string.Empty).Replace('\n', ' ').Trim() : null;
        if (string.IsNullOrEmpty(text)) return;
        SetProperty(ref _previewText, text, nameof(PreviewText));
        _previewTextEdited = false;
        RelayCommand.Refresh();
    }

    private async Task PreviewVoiceAsync()
    {
        var text = PreviewText.Trim();
        var voice = SelectedDubbingVoice;
        float speed = (float)SelectedDubbingSpeed.Value;
        await RunDubbingWorkAsync("Preview", async ct =>
        {
            var tts = await EnsureKokoroAsync();
            DubbingStatus = $"Speaking as {voice.Name}...";
            var started = DateTime.UtcNow;
            var samples = await Task.Run(() => tts.Speak(text, voice.Id, speed), ct);
            var took = DateTime.UtcNow - started;
            if (samples.Length == 0)
            {
                DubbingStatus = "Nothing to say in that line (only sound labels or punctuation).";
                return;
            }
            var path = Path.Combine(Path.GetTempPath(), "SubtitleStudio-voice-preview.wav");
            _s.AudioPreview.Stop();
            WavFile.Write(path, samples, KokoroTts.SampleRate);
            _s.AudioPreview.Play(path);
            var length = samples.Length / (double)KokoroTts.SampleRate;
            DubbingStatus = $"{voice.Name}: {length:0.0} s of speech, made in {took.TotalSeconds:0.0} s.";
            _s.Log.Detail("Dubbing", $"Preview ({voice.Id}, speed {speed:0.0#}): \"{text}\" → {tts.Phonemes(text)} ({length:0.0} s in {took.TotalSeconds:0.0} s).");
        });
    }

    // ---------------- Voice track ----------------

    public bool DubbingBusy
    {
        get => _dubbingBusy;
        private set
        {
            if (SetProperty(ref _dubbingBusy, value))
            {
                OnPropertyChanged(nameof(DubbingBlockedText));
                OnPropertyChanged(nameof(DubbedVideoBlockedText));
                RelayCommand.Refresh();
            }
        }
    }

    public double DubbingProgress
    {
        get => _dubbingProgress;
        private set => SetProperty(ref _dubbingProgress, value);
    }

    public string DubbingStatus
    {
        get => _dubbingStatus;
        private set => SetProperty(ref _dubbingStatus, value);
    }

    /// <summary>After a voice track: the lines that ran over their time.</summary>
    public string DubbingReport
    {
        get => _dubbingReport;
        private set
        {
            if (SetProperty(ref _dubbingReport, value)) OnPropertyChanged(nameof(HasDubbingReport));
        }
    }

    public bool HasDubbingReport => _dubbingReport.Length > 0;

    /// <summary>What will be spoken: the open subtitles, with a warning when they aren't English.</summary>
    public string DubbingSourceText
    {
        get
        {
            if (_doc is null || Cues.Count == 0) return "No subtitles are open. Open the English subtitles (or translate into English) in the Subtitles tab.";
            var name = _doc.SourcePath is { } p ? Path.GetFileName(p) : "the unsaved subtitles";
            var lines = Cues.Count == 1 ? "1 line" : $"{Cues.Count:N0} lines";
            var video = PairedVideo is { } v ? $", for {v.Name}" : string.Empty;
            return $"{name}: {lines}{video}.";
        }
    }

    /// <summary>Not English: the voices speak English only.</summary>
    public string? DubbingLanguageWarning
    {
        get
        {
            if (_doc is null || Cues.Count == 0) return null;
            if (DubbingScript is { } script)
                return $"These subtitles are written in {script} letters. The voices speak English: translate them into English first (Translate tab).";
            var language = EditorLanguage.Split('-', '_')[0];
            if (language.Length > 0 && language != "en")
                return $"These subtitles are tagged {WhisperLanguages.NameOf(language)}. The voices speak English, so other languages will sound wrong.";
            return null;
        }
    }

    public bool HasDubbingLanguageWarning => DubbingLanguageWarning is not null;

    /// <summary>The letters the subtitles are written in, when not Latin (a short file is judged by all its letters).</summary>
    private Script? DubbingScript
    {
        get
        {
            var all = string.Concat(Cues.Select(r => r.Text));
            var script = Scripts.Count(all).Values.Sum() < 8 ? Scripts.Dominant(all) : SubtitleTranslationPrompt.WrittenScript(Cues.Select(r => r.Text));
            return script is Script.Latin or Script.Other ? null : script;
        }
    }

    private bool DubbingSourceIsForeignScript => DubbingScript is not null;

    public string DubbingBlockedText
    {
        get
        {
            if (DubbingBusy) return string.Empty;
            if (!HasTtsModel) return "Download the voice model first (step 1).";
            if (_doc is null || Cues.Count == 0) return "Open English subtitles in the Subtitles tab first.";
            if (DubbingSourceIsForeignScript) return "Translate the subtitles into English first.";
            return string.Empty;
        }
    }

    public bool CanMakeVoiceTrack => !DubbingBusy && HasTtsModel && _doc is not null && Cues.Count > 0 && !DubbingSourceIsForeignScript;

    public bool CanMakeDubbedVideo => CanMakeVoiceTrack && PairedVideo is not null;

    /// <summary>Why "Make the dubbed video" can't run (empty when it can).</summary>
    public string DubbedVideoBlockedText
        => DubbingBlockedText.Length > 0 ? DubbingBlockedText
            : !DubbingBusy && PairedVideo is null && _doc is not null ? "The subtitles aren't paired with a video. Pair them in the Subtitles tab (or open the subtitles from the video's row in Files)." : string.Empty;

    public AsyncRelayCommand MakeVoiceTrackCommand { get; private set; } = null!;
    public AsyncRelayCommand MakeDubbedVideoCommand { get; private set; } = null!;
    public RelayCommand CancelDubbingCommand { get; private set; } = null!;

    /// <summary>Say lines faster to fit the time before the next one.</summary>
    public bool DubbingFit
    {
        get => _s.Config.DubbingFit;
        set
        {
            if (_s.Config.DubbingFit == value) return;
            _s.Config.DubbingFit = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<DubbingDuckOption> DubbingDuckOptions { get; } = new[]
    {
        new DubbingDuckOption(DuckLevel.Little, "little", "A little lower (about 8 dB)"),
        new DubbingDuckOption(DuckLevel.Lower, "lower", "Lower (about 12 dB)"),
        new DubbingDuckOption(DuckLevel.Much, "much", "Much lower (about 15 dB)"),
    };

    public DubbingDuckOption SelectedDubbingDuck
    {
        get => DubbingDuckOptions.FirstOrDefault(o => o.Id == _s.Config.DubbingDuck) ?? DubbingDuckOptions[1];
        set
        {
            if (value is null || value.Id == _s.Config.DubbingDuck) return;
            _s.Config.DubbingDuck = value.Id;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool DubbingDefaultTrack
    {
        get => _s.Config.DubbingDefaultTrack;
        set
        {
            if (_s.Config.DubbingDefaultTrack == value) return;
            _s.Config.DubbingDefaultTrack = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>"Episode 3.en.voice.wav" next to the video (or the subtitles).</summary>
    public string VoiceTrackFileName
    {
        get
        {
            var baseName = PairedVideo is { } v ? Path.GetFileNameWithoutExtension(v.Name)
                : _doc?.SourcePath is { } p ? StripLanguageSuffix(Path.GetFileNameWithoutExtension(p)) : "Untitled";
            return $"{baseName}.en.voice.wav";
        }
    }

    /// <summary>"Episode 3.en" to "Episode 3" (a subtitle's language part isn't part of the video's name).</summary>
    private static string StripLanguageSuffix(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot - 1 is 2 or 3 && name[(dot + 1)..].All(char.IsLetter) ? name[..dot] : name;
    }

    private async Task MakeVoiceTrackAsync()
    {
        var folder = PairedVideo?.Folder ?? (_doc?.SourcePath is { } sp ? Path.GetDirectoryName(sp) : null);
        var path = _s.Dialogs.SaveFile("Save the voice track", "WAV audio (*.wav)|*.wav", 1, folder, VoiceTrackFileName);
        if (path is null) return;
        DubbingReport = string.Empty;
        await RunDubbingWorkAsync("Making the voice track", async ct =>
        {
            var (result, took) = await BuildVoiceTrackAsync(path, 1.0, ct);
            DubbingStatus = $"Voice track saved: {Path.GetFileName(path)} ({MediaItemLength(result.Length)}), {result.Spoken} lines spoken in {FormatEta(took)}.";
            SetStatus($"Voice track saved: {Path.GetFileName(path)}.", StatusKind.Success);
        });
    }

    /// <summary>The voice track, then the voice mixed over the original sound into a copy of the video as an extra English track.</summary>
    private async Task MakeDubbedVideoAsync()
    {
        if (PairedVideo is not { } video) return;
        var ff = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        if (!ff.IsComplete)
        {
            SetStatus("ffmpeg is needed to add the voice to the video: set it in Settings > Engines & tools.", StatusKind.Error);
            return;
        }
        var suggested = DubMixer.SuggestOutput(video.FullPath);
        var ext = Path.GetExtension(suggested);
        var filter = ext == ".mp4" ? "MP4 video (*.mp4)|*.mp4|Matroska video (*.mkv)|*.mkv" : "Matroska video (*.mkv)|*.mkv|MP4 video (*.mp4)|*.mp4";
        var output = _s.Dialogs.SaveFile("Save the dubbed video", filter, 1, video.Folder, Path.GetFileName(suggested));
        if (output is null) return;
        DubbingReport = string.Empty;
        var duck = SelectedDubbingDuck.Level;
        bool makeDefault = DubbingDefaultTrack;
        await RunDubbingWorkAsync("Making the dubbed video", async ct =>
        {
            var voicePath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".voice.partial.wav");
            try
            {
                // Speaking is most of the work: 90% of the bar, the mix the rest.
                var (track, took) = await BuildVoiceTrackAsync(voicePath, 0.9, ct);
                DubbingStatus = "Mixing the voice into the video...";
                var started = DateTime.UtcNow;
                var progress = new Progress<double>(f => { if (_dubbingLive) DubbingProgress = 90 + f * 10; });
                var mixed = await DubMixer.RunAsync(ff.FfmpegPath!, ff.FfprobePath!, new DubMixRequest(video.FullPath, voicePath, output, 0, duck, makeDefault), progress, _s.Log, ct);
                var total = took + (DateTime.UtcNow - started);
                DubbingStatus = $"Dubbed video saved: {Path.GetFileName(output)}, with the English voice-over as audio track {mixed.AudioTracks}{(makeDefault ? " (the default)" : "")}. Made in {FormatEta(total)}.";
                _s.Log.Success("Dubbing", $"Dubbed video saved: {output} ({mixed.AudioTracks} audio tracks, the English voice-over {(makeDefault ? "first choice" : "last")}). The original video is unchanged.");
                SetStatus($"Dubbed video saved: {Path.GetFileName(output)}.", StatusKind.Success);
            }
            finally
            {
                try { if (File.Exists(voicePath)) File.Delete(voicePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        });
    }

    /// <summary>Speaks every line into <paramref name="path"/>, reporting into the first <paramref name="share"/> of the bar.</summary>
    private async Task<(VoiceTrackResult Result, TimeSpan Took)> BuildVoiceTrackAsync(string path, double share, CancellationToken ct)
    {
        var cues = Cues.Select(r => r.Cue.Clone()).ToList();
        var voice = SelectedDubbingVoice;
        float speed = (float)SelectedDubbingSpeed.Value;
        var options = new VoiceTrackOptions(speed, DubbingFit, PairedVideo?.Duration);
        var tts = await EnsureKokoroAsync();
        options = options with { Concurrency = tts.Concurrency };
        var started = DateTime.UtcNow;
        _s.Log.Info("Dubbing", $"Voice track: {cues.Count} lines, voice {voice.Id}, speed {speed:0.0#}, {(options.Fit ? "fitted to their time" : "not fitted")}, on {tts.DeviceLabel}, {tts.Concurrency} at a time → {Path.GetFileName(path)}.");
        bool finished = false;
        var progress = new Progress<EngineProgress>(p =>
        {
            if (!_dubbingLive || finished) return;
            DubbingProgress = p.Fraction * 100 * share;
            var elapsed = DateTime.UtcNow - started;
            var eta = p.Fraction > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) : (TimeSpan?)null;
            DubbingStatus = $"Speaking {p.Message.ToLowerInvariant()}" + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty) + ".";
        });
        var result = await VoiceTrack.BuildAsync(cues, (text, s) => tts.Speak(text, voice.Id, s), options, path, progress, ct);
        finished = true; // progress reported late must not overwrite the result
        var took = DateTime.UtcNow - started;
        DubbingReport = VoiceTrackReport(result, options.Fit);
        _s.Log.Success("Dubbing", $"Voice track made: {result.Spoken} lines spoken ({result.SpeechTime.TotalMinutes:0.0} min of speech) in {took.TotalMinutes:0.0} min"
            + (options.Fit ? $"; {result.SpedUp} said faster and {result.Stretched} also shortened to fit" : string.Empty)
            + $"; {result.Silent} without words; {result.TooLong.Count} still longer than their time"
            + (result.LongestDelay > TimeSpan.Zero ? $", the most a line was pushed back {result.LongestDelay.TotalSeconds:0.0} s." : "."));
        foreach (var l in result.TooLong.Take(50))
            _s.Log.Detail("Dubbing", $"Line {l.Number}: {l.Speech.TotalSeconds:0.0} s of speech in {l.Slot.TotalSeconds:0.0} s (over by {l.Over.TotalSeconds:0.0} s).");
        return (result, took);
    }

    private static string MediaItemLength(TimeSpan t) => Models.MediaItem.FormatDuration(t);

    public static string VoiceTrackReport(VoiceTrackResult r, bool fitted = false)
    {
        var fit = fitted && r.SpedUp + r.Stretched > 0
            ? $"{r.SpedUp + r.Stretched} line{(r.SpedUp + r.Stretched == 1 ? " was" : "s were")} said faster to fit their time{(r.Stretched > 0 ? $" ({r.Stretched} also shortened a little, pitch kept)" : "")}. "
            : string.Empty;
        if (r.TooLong.Count == 0) return fit + "Every line fits in the time before the next one.";
        var worst = r.TooLong.OrderByDescending(l => l.Over).Take(5).Select(l => $"{l.Number} (+{l.Over.TotalSeconds:0.0} s)");
        return fit + $"{r.TooLong.Count} line{(r.TooLong.Count == 1 ? " runs" : "s run")} longer than the time before the next line{(fitted ? " even so" : "")}, so the line after waits for it"
            + $" (at most {r.LongestDelay.TotalSeconds:0.0} s late). The longest: {string.Join(", ", worst)}. The Log lists them all."
            + (fitted ? " Shortening the subtitle text, or merging it with the next line, helps those." : " Turn on fitting, or choose a faster speed.");
    }

    private async Task RunDubbingWorkAsync(string what, Func<CancellationToken, Task> work)
    {
        _dubbingCts = new CancellationTokenSource();
        DubbingBusy = true;
        DubbingProgress = 0;
        DubbingStatus = what + "...";
        try
        {
            _dubbingLive = true;
            try
            {
                await work(_dubbingCts.Token);
            }
            finally
            {
                _dubbingLive = false;
            }
        }
        catch (OperationCanceledException)
        {
            DubbingStatus = "Cancelled.";
            SetStatus(what + " cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Net.Http.HttpRequestException or ArgumentException)
        {
            DubbingStatus = ex.Message;
            SetStatus($"{what} failed: {ex.Message}", StatusKind.Error);
            _s.Log.Error("Dubbing", $"{what} failed: {ex}");
        }
        finally
        {
            DubbingBusy = false;
            _dubbingCts.Dispose();
            _dubbingCts = null;
        }
    }

    private void RaiseDubbingSource()
    {
        OnPropertyChanged(nameof(DubbingSourceText));
        OnPropertyChanged(nameof(DubbingLanguageWarning));
        OnPropertyChanged(nameof(HasDubbingLanguageWarning));
        OnPropertyChanged(nameof(DubbingBlockedText));
        OnPropertyChanged(nameof(VoiceTrackFileName));
        OnPropertyChanged(nameof(DubbedVideoBlockedText));
        RelayCommand.Refresh();
    }

    private void InitDubbing()
    {
        DownloadTtsModelCommand = new AsyncRelayCommand(DownloadTtsModelAsync, () => !DubbingBusy && !HasTtsModel);
        OpenTtsModelsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_s.TtsModels.Folder);
            _s.Dialogs.OpenFolder(_s.TtsModels.Folder);
        });
        PreviewVoiceCommand = new AsyncRelayCommand(PreviewVoiceAsync, () => !DubbingBusy && HasTtsModel && PreviewText.Trim().Length > 0);
        StopPreviewCommand = new RelayCommand(() => _s.AudioPreview.Stop());
        UseSelectedLineCommand = new RelayCommand(() => TakeSelectedLine(force: true), () => SelectedCue is not null);
        MakeVoiceTrackCommand = new AsyncRelayCommand(MakeVoiceTrackAsync, () => CanMakeVoiceTrack);
        MakeDubbedVideoCommand = new AsyncRelayCommand(MakeDubbedVideoAsync, () => CanMakeDubbedVideo);
        CancelDubbingCommand = new RelayCommand(() => _dubbingCts?.Cancel(), () => DubbingBusy);
        _previewText = "Hello. This is how I sound when I read your subtitles.";

        Cues.CollectionChanged += (_, _) => RaiseDubbingSource();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasDocument) or nameof(EditorLanguage) or nameof(EditorTitle) or nameof(PairedVideo)) RaiseDubbingSource();
            if (e.PropertyName is nameof(SelectedCue)) TakeSelectedLine(force: false);
        };
    }
}
