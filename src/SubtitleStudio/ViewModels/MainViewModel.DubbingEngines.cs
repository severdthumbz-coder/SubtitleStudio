using System.Net.Http;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Gpu;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// The dubbing voice engine: Kokoro (built in) or Piper (downloadable voices). The picker chooses the default engine
/// for the dub; the Kokoro model and Piper voices are each loaded on first use. This partial holds the engine-picker
/// state and the Piper download/load path; the Kokoro path stays in MainViewModel.Dubbing.
/// </summary>
public sealed record DubbingEngineOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public partial class MainViewModel
{
    private PiperTts? _piper;
    private string? _piperLoadedVoiceId;

    public static readonly IReadOnlyList<DubbingEngineOption> DubbingEngines = new[]
    {
        new DubbingEngineOption("kokoro", "Kokoro (built in)"),
        new DubbingEngineOption("piper", "Piper (downloadable)"),
        new DubbingEngineOption("cloning", "Voice cloning (slow, matches the original)"),
    };

    public DubbingEngineOption SelectedDubbingEngine
    {
        get => DubbingEngines.FirstOrDefault(e => e.Id == _s.Config.DubbingEngine) ?? DubbingEngines[0];
        set
        {
            if (value is null || value.Id == _s.Config.DubbingEngine) return;
            _s.Config.DubbingEngine = value.Id;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPiperEngine));
            OnPropertyChanged(nameof(IsCloningEngine));
            OnPropertyChanged(nameof(DownloadPiperVoiceLabel));
            OnPropertyChanged(nameof(PiperVoiceReady));
            OnPropertyChanged(nameof(CloningWarningText));
            OnPropertyChanged(nameof(DownloadCloningLabel));
            OnPropertyChanged(nameof(CloningBundleAvailable));
            OnPropertyChanged(nameof(IsCloningReady));
            OnPropertyChanged(nameof(CloningEstimateText));
            OnPropertyChanged(nameof(ClonedLineCount));
            RelayCommand.Refresh();
        }
    }

    public bool IsPiperEngine => SelectedDubbingEngine.Id == "piper";

    /// <summary>
    /// Voice cloning is selected. Not yet offered in the engine picker — the picker entry lands with the hardware-aware
    /// warning dialog and the reference-clip/ensure path, so the engine is never selectable while it can't actually run.
    /// This helper and CloningTts exist so that final slice is small and the plumbing is already in place.
    /// </summary>
    public bool IsCloningEngine => SelectedDubbingEngine.Id == "cloning";

    /// <summary>Whether the cloning sidecar exe has been downloaded (the ~11.7 GB model is fetched by the exe on first run).</summary>
    public bool IsCloningReady => _s.CloningSidecar.HasExe;

    // ---------------- Piper voices ----------------

    public IReadOnlyList<PiperVoiceInfo> PiperVoices => Services.Dubbing.PiperVoices.English;

    public PiperVoiceInfo SelectedPiperVoice
    {
        get => Services.Dubbing.PiperVoices.Find(_s.Config.DubbingPiperVoice) ?? Services.Dubbing.PiperVoices.English[0];
        set
        {
            if (value is null || value.Id == _s.Config.DubbingPiperVoice) return;
            _s.Config.DubbingPiperVoice = value.Id;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadPiperVoiceLabel));
            OnPropertyChanged(nameof(PiperVoiceReady));
            RelayCommand.Refresh();
        }
    }

    /// <summary>Whether the chosen Piper voice is already downloaded.</summary>
    public bool PiperVoiceReady => _s.PiperModels.Has(SelectedPiperVoice);

    public string DownloadPiperVoiceLabel => PiperVoiceReady ? "Downloaded" : "Download this voice";

    private AsyncRelayCommand? _downloadPiperVoiceCommand;
    public AsyncRelayCommand DownloadPiperVoiceCommand => _downloadPiperVoiceCommand ??= new AsyncRelayCommand(DownloadPiperVoiceAsync, () => !PiperVoiceReady);

    private async Task DownloadPiperVoiceAsync()
    {
        var v = SelectedPiperVoice;
        await RunDubbingWorkAsync($"Downloading {v.DisplayName}", async ct =>
        {
            var started = DateTime.UtcNow;
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (!_dubbingLive) return;
                DubbingProgress = p.Fraction * 100;
                if (p.Phase == ModelDownloadPhase.Checking) { DubbingStatus = "Checking the download (SHA-256)..."; return; }
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                double rate = elapsed > 1 ? p.Done / elapsed : 0;
                DubbingStatus = $"Downloading {v.DisplayName}... {p.Done / (1024.0 * 1024):0} of {p.Total / (1024.0 * 1024):0} MB"
                    + (rate > 0 ? $", {rate / (1024 * 1024):0.0} MB/s" : string.Empty);
            });
            await _s.PiperModels.DownloadAsync(v, ModelHttp, progress, ct);
            _s.Log.Success("Dubbing", $"Piper voice downloaded and checked: {v.DisplayName} ({v.ModelFileName}).");
            DubbingStatus = $"{v.DisplayName} downloaded. Press Preview to hear it.";
            SetStatus($"Piper voice {v.DisplayName} is ready.", StatusKind.Success);
        });
        OnPropertyChanged(nameof(PiperVoiceReady));
        OnPropertyChanged(nameof(DownloadPiperVoiceLabel));
        RelayCommand.Refresh();
    }

    /// <summary>The Piper voice, loaded on first use (reloaded when the chosen voice changes).</summary>
    private async Task<IDubbingVoice> EnsurePiperAsync()
    {
        var v = SelectedPiperVoice;
        if (_piper is not null && _piperLoadedVoiceId == v.Id) return _piper;
        if (!_s.PiperModels.Has(v)) throw new InvalidOperationException($"Download the Piper voice {v.DisplayName} first.");
        DubbingStatus = $"Loading the Piper voice {v.DisplayName}...";
        var gpu = _gpu ?? await Task.Run(GpuDetector.Detect);
        var adapter = gpu?.Primary is { IsSoftware: false } a && gpu.Backend != AiBackend.Cpu ? a : null;
        var started = DateTime.UtcNow;
        _piper?.Dispose();
        _piper = await Task.Run(() =>
        {
            _ = EnglishG2P.Shared;
            var model = _s.PiperModels.Load(v, adapter, m => _s.Log.Info("Dubbing", m));
            return new PiperTts(model);
        });
        _piperLoadedVoiceId = v.Id;
        _s.Log.Info("Dubbing", $"Piper voice ready in {(DateTime.UtcNow - started).TotalSeconds:0.0} s ({_piper.DeviceLabel}).");
        OnPropertyChanged(nameof(DubbingDeviceText));
        return _piper;
    }

    /// <summary>The engine for the current dub: Piper or cloning when chosen and ready, otherwise Kokoro.</summary>
    private Task<IDubbingVoice> EnsureEngineAsync()
        => IsCloningEngine ? EnsureCloningPreviewAsync()
            : IsPiperEngine ? EnsurePiperAsync()
            : EnsureKokoroAsync();

    /// <summary>A voice id that belongs to a Piper voice (the overflow "extras"), not a Kokoro one.</summary>
    public static bool IsPiperVoiceId(string? id) => id is not null && Services.Dubbing.PiperVoices.Find(id) is not null;

    // Piper models loaded for a mixed cast (one per overflow voice used), keyed by voice id.
    private readonly Dictionary<string, PiperTts> _castPiper = new();

    /// <summary>
    /// Ensures the Piper overflow voices a cast uses are downloaded and loaded, and returns an IDubbingVoice that
    /// speaks each line with the Piper model for that line's voice id. Downloads any missing voices first (with
    /// progress), then loads each on the graphics card (or processor). Used only when the cast overflowed Kokoro.
    /// </summary>
    private async Task<IDubbingVoice> EnsurePiperForCastAsync(IReadOnlyList<string> voiceIds, CancellationToken ct)
    {
        var gpu = _gpu ?? await Task.Run(GpuDetector.Detect);
        var adapter = gpu?.Primary is { IsSoftware: false } a && gpu.Backend != AiBackend.Cpu ? a : null;
        foreach (var id in voiceIds)
        {
            if (_castPiper.ContainsKey(id)) continue;
            var info = Services.Dubbing.PiperVoices.Find(id);
            if (info is null) continue;
            if (!_s.PiperModels.Has(info))
            {
                DubbingStatus = $"Downloading the extra voice {info.DisplayName}...";
                var dl = new Progress<ModelDownloadProgress>(p => { if (_dubbingLive) DubbingStatus = $"Downloading {info.DisplayName}... {p.Done / (1024.0 * 1024):0} of {p.Total / (1024.0 * 1024):0} MB"; });
                await _s.PiperModels.DownloadAsync(info, ModelHttp, dl, ct);
                _s.Log.Success("Dubbing", $"Extra Piper voice downloaded: {info.DisplayName}.");
            }
            var piper = await Task.Run(() => { _ = EnglishG2P.Shared; return new PiperTts(_s.PiperModels.Load(info, adapter, m => _s.Log.Info("Dubbing", m))); }, ct);
            _castPiper[id] = piper;
            _s.Log.Info("Dubbing", $"Extra Piper voice ready: {info.DisplayName} ({piper.DeviceLabel}).");
        }
        return new CastPiper(_castPiper);
    }

    /// <summary>Routes each line to the Piper model for its voice id (a cast can use several Piper extras at once).</summary>
    private sealed class CastPiper(IReadOnlyDictionary<string, PiperTts> byVoice) : IDubbingVoice
    {
        // All the app's Piper voices share one sample rate (22.05 kHz); the caller resamples to the track rate.
        public int SampleRate => byVoice.Values.First().SampleRate;
        public string DeviceLabel => byVoice.Values.First().DeviceLabel;
        public int Concurrency => 1;
        public float[] Speak(string subtitleText, string voiceId, float speed = 1f)
            => byVoice.TryGetValue(voiceId, out var p) ? p.Speak(subtitleText, voiceId, speed) : Array.Empty<float>();
        public string Phonemes(string subtitleText) => byVoice.Values.First().Phonemes(subtitleText);
        public void Dispose() { } // models are owned by the view-model's _castPiper, disposed with the session
    }

    /// <summary>The voice id to speak with on the current engine (the engine's own selected voice).</summary>
    private string CurrentVoiceId => IsCloningEngine ? ClonePreviewVoiceId : IsPiperEngine ? SelectedPiperVoice.Id : SelectedDubbingVoice.Id;

    /// <summary>A display name for the current voice, for status text.</summary>
    private string CurrentVoiceName => IsCloningEngine ? "the cloned voice" : IsPiperEngine ? SelectedPiperVoice.DisplayName : SelectedDubbingVoice.Name;

    // ---------------- Voice cloning (IndexTTS sidecar) ----------------

    private const string ClonePreviewVoiceId = "clone-preview";

    /// <summary>Whether a cloning bundle is configured in this build (false until the release is wired up).</summary>
    public bool CloningBundleAvailable => CloningSidecarStore.BundleAvailable;

    public string DownloadCloningLabel => IsCloningReady ? "Downloaded" : "Download the cloning engine";

    /// <summary>
    /// What cloning will cost on this PC: cloning is CPU-bound unless there's an NVIDIA card, so on anything else it's
    /// much slower than Kokoro/Piper. The text warns and, where we can, gives a rough order-of-magnitude so the user
    /// decides with eyes open. (A precise estimate waits for the first real run, which calibrates it.)
    /// </summary>
    public string CloningWarningText
    {
        get
        {
            if (!CloningBundleAvailable)
                return "Voice cloning isn't bundled in this build yet.";
            bool cuda = _gpu?.Primary is { Vendor: GpuVendor.Nvidia } && _gpu.Backend != AiBackend.Cpu;
            if (cuda)
                return "Cloning copies each speaker's own voice from the original audio, so the dub sounds like them. "
                     + $"Your NVIDIA card ({_gpu!.Primary!.Name}) runs it on CUDA — the fastest path, but still well slower than Kokoro. "
                     + "The first run downloads the ~11.7 GB model.";
            return "Cloning copies each speaker's own voice from the original audio, so the dub sounds like them — the closest match to the original. "
                 + "On this PC it runs on the processor (no NVIDIA card), which is slow: expect it to take many times longer than Kokoro, roughly on the order of real-time or more per line. "
                 + "Good for a short clip or a single speaker; a whole episode will be a long job. The first run also downloads the ~11.7 GB model. "
                 + "Kokoro and Piper stay the quick default.";
        }
    }

    public string CloningDeviceArg => _gpu?.Primary is { Vendor: GpuVendor.Nvidia } && _gpu.Backend != AiBackend.Cpu ? "cuda" : "cpu";

    // ---------------- Cloning quality (speed knob) ----------------

    /// <summary>A cloning quality choice: a label and the IndexTTS --n-timesteps it maps to (0 = the model default).</summary>
    public sealed record CloningQualityOption(string Id, string Label, int Timesteps)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<CloningQualityOption> CloningQualities { get; } = new[]
    {
        new CloningQualityOption("fast", "Fast (roughest, quickest)", 10),
        new CloningQualityOption("balanced", "Balanced (recommended)", 0),
        new CloningQualityOption("best", "Best (slowest, cleanest)", 40),
    };

    public CloningQualityOption SelectedCloningQuality
    {
        get => CloningQualities.FirstOrDefault(q => q.Id == _s.Config.CloningQuality) ?? CloningQualities[1];
        set
        {
            if (value is null || value.Id == _s.Config.CloningQuality) return;
            _s.Config.CloningQuality = value.Id;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloningEstimateText));
        }
    }

    /// <summary>The --n-timesteps value for the chosen quality (0 = let the exe use its default).</summary>
    private int CloningTimesteps => SelectedCloningQuality.Timesteps;

    // ---------------- Cloning scope ----------------

    public sealed record CloningScopeOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<CloningScopeOption> CloningScopes { get; } = new[]
    {
        new CloningScopeOption("all", "Every line (whole dub)"),
        new CloningScopeOption("speakers", "Only the speakers I pick"),
        new CloningScopeOption("lines", "Only the selected line range"),
    };

    public CloningScopeOption SelectedCloningScope
    {
        get => CloningScopes.FirstOrDefault(s => s.Id == _s.Config.CloningScope) ?? CloningScopes[0];
        set
        {
            if (value is null || value.Id == _s.Config.CloningScope) return;
            _s.Config.CloningScope = value.Id;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCloneScopeSpeakers));
            OnPropertyChanged(nameof(CloningEstimateText));
        }
    }

    public bool IsCloneScopeSpeakers => SelectedCloningScope.Id == "speakers";

    /// <summary>
    /// The set of line indices that cloning will speak, given the current scope. "all" = every line; "speakers" = the
    /// lines of the speakers whose clone toggle is on; "lines" = the selected line range (from the Subtitles tab). Lines
    /// not in this set are spoken by Kokoro, so the slow cloning is spent only where it's wanted.
    /// </summary>
    private HashSet<int> CloneLineSet()
    {
        var set = new HashSet<int>();
        switch (SelectedCloningScope.Id)
        {
            case "speakers":
                if (UsingSpeakers)
                    foreach (var r in SpeakerRows.Where(r => r.CloneThisSpeaker))
                        foreach (var c in r.Speaker.Lines) set.Add(c.Index);
                else
                    foreach (var cue in Cues) set.Add(cue.Cue.Index); // no speakers: fall back to all
                break;
            case "lines":
                // The line selected in the Subtitles tab and a small window around it (a scene), capped so a stray
                // selection can't start a huge job. When nothing is selected, treat as the first line's neighbourhood.
                var anchorIdx = (SelectedCue?.Cue.Index) ?? (Cues.Count > 0 ? Cues[0].Cue.Index : -1);
                if (anchorIdx >= 0)
                    foreach (var cue in Cues.Where(c => Math.Abs(c.Cue.Index - anchorIdx) <= CloneLineWindow)) set.Add(cue.Cue.Index);
                break;
            default: // "all"
                foreach (var cue in Cues) set.Add(cue.Cue.Index);
                break;
        }
        return set;
    }

    /// <summary>How many lines on each side of the selected line the "selected line range" scope covers.</summary>
    private const int CloneLineWindow = 15;

    // ---------------- Time estimate ----------------

    /// <summary>
    /// Roughly how long cloning a line takes on this PC, in seconds — a measured ballpark (≈130 s/line on a CPU like
    /// J's; much faster on CUDA). Used only to warn before a long run, so order-of-magnitude is what matters.
    /// </summary>
    private double SecondsPerClonedLine => CloningDeviceArg == "cuda" ? 8 : SelectedCloningQuality.Id switch
    {
        "fast" => 70,
        "best" => 170,
        _ => 130,
    };

    /// <summary>The number of lines cloning will actually speak under the current scope (the rest go to Kokoro).</summary>
    public int ClonedLineCount => IsCloningEngine ? CloneLineSet().Count : 0;

    /// <summary>A plain-language estimate of how long the current cloning scope will take, for the UI and the confirm.</summary>
    public string CloningEstimateText
    {
        get
        {
            if (!IsCloningEngine) return string.Empty;
            int n = ClonedLineCount;
            if (n == 0) return "No lines are in the cloning scope yet.";
            var total = TimeSpan.FromSeconds(n * SecondsPerClonedLine);
            var rough = total.TotalHours >= 1 ? $"about {total.TotalHours:0.#} hour{(total.TotalHours >= 1.95 ? "s" : "")}"
                : total.TotalMinutes >= 1 ? $"about {total.TotalMinutes:0} min"
                : "under a minute";
            return $"{n:N0} line{(n == 1 ? "" : "s")} to clone — roughly {rough} on this PC ({SecondsPerClonedLine:0} s/line). The rest are spoken by Kokoro.";
        }
    }

    /// <summary>Above this, ask the user to confirm before starting (a long job they might not mean to launch).</summary>
    private static readonly TimeSpan CloningConfirmThreshold = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Confirms a long cloning run with the user (returns false to cancel). Called before the dub preview and the full
    /// dub when cloning is selected; short jobs under the threshold run without asking.
    /// </summary>
    private bool ConfirmCloningRun()
    {
        if (!IsCloningEngine) return true;
        int n = ClonedLineCount;
        if (n == 0)
        {
            SetStatus("Nothing is in the cloning scope — pick speakers or a line range, or switch scope to the whole dub.", StatusKind.Error);
            return false;
        }
        var total = TimeSpan.FromSeconds(n * SecondsPerClonedLine);
        if (total < CloningConfirmThreshold) return true;
        return _s.Dialogs.Confirm("Voice cloning is slow",
            $"Cloning {n:N0} line{(n == 1 ? "" : "s")} will take roughly {(total.TotalHours >= 1 ? $"{total.TotalHours:0.#} hours" : $"{total.TotalMinutes:0} minutes")} on this PC "
            + $"(about {SecondsPerClonedLine:0} s per line; cloning runs one line at a time on the processor). "
            + "The rest of the lines are spoken by Kokoro. Start the cloned dub?");
    }

    private AsyncRelayCommand? _downloadCloningCommand;
    public AsyncRelayCommand DownloadCloningCommand => _downloadCloningCommand ??= new AsyncRelayCommand(DownloadCloningAsync, () => CloningBundleAvailable && !IsCloningReady && !DubbingBusy);

    private async Task DownloadCloningAsync()
    {
        await RunDubbingWorkAsync("Downloading the voice-cloning engine", async ct =>
        {
            var started = DateTime.UtcNow;
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (!_dubbingLive) return;
                DubbingProgress = p.Fraction * 100;
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                double rate = elapsed > 1 ? p.Done / elapsed : 0;
                DubbingStatus = $"Downloading the cloning engine... {p.Done / (1024.0 * 1024):0} of {p.Total / (1024.0 * 1024):0} MB"
                    + (rate > 0 ? $", {rate / (1024 * 1024):0.0} MB/s" : string.Empty);
            });
            await _s.CloningSidecar.DownloadAsync(ModelHttp, progress, ct);
            _s.Log.Success("Dubbing", $"Voice-cloning engine downloaded and extracted ({CloningSidecarStore.ExeName}).");
            DubbingStatus = "The cloning engine is ready. The ~11.7 GB voice model downloads on the first line it speaks.";
            SetStatus("The voice-cloning engine is ready.", StatusKind.Success);
        });
        OnPropertyChanged(nameof(IsCloningReady));
        OnPropertyChanged(nameof(DownloadCloningLabel));
        RelayCommand.Refresh();
    }

    /// <summary>
    /// A cloning engine for previewing one line: cuts a short reference clip from the paired video (the selected
    /// line's speaker if speakers have been found, otherwise the longest subtitle line) and keys it as the preview
    /// voice. Throws a clear message when there's nothing to clone from yet (no video, or no lines).
    /// </summary>
    private async Task<IDubbingVoice> EnsureCloningPreviewAsync()
    {
        if (!IsCloningReady)
            throw new InvalidOperationException("Download the voice-cloning engine first (step 1).");
        if (PairedVideo is not { } video)
            throw new InvalidOperationException("Cloning copies a voice from the video: pair the subtitles with a video first.");
        var ff = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        if (!ff.IsComplete)
            throw new InvalidOperationException("ffmpeg is needed to take the reference clip: set it in Settings > Engines & tools.");

        // Pick a line to copy the voice from: the selected line's speaker's longest line, else the longest line overall.
        var speakerLine = SelectedLineSpeaker?.Speaker.Lines.OrderByDescending(l => (l.End - l.Start).Ticks).FirstOrDefault();
        var line = speakerLine ?? Cues.Select(r => r.Cue).OrderByDescending(l => (l.End - l.Start).Ticks).FirstOrDefault();
        if (line is null)
            throw new InvalidOperationException("There are no lines to copy a voice from.");

        DubbingStatus = "Taking a reference clip of the original voice...";
        var workDir = Path.Combine(_s.CloningSidecar.Folder, "work");
        Directory.CreateDirectory(workDir);
        var refClip = Path.Combine(workDir, "ref-preview.wav");
        await CutReferenceClipAsync(ff.FfmpegPath!, video.FullPath, line, refClip, CancellationToken.None);

        var refClips = new Dictionary<string, string> { [ClonePreviewVoiceId] = refClip };
        var gpu = _gpu ?? await Task.Run(GpuDetector.Detect);
        _gpu ??= gpu;
        return new CloningTts(_s.CloningSidecar.ExePath, refClips, CloningDeviceArg, CloningTimesteps, workDir, m => _s.Log.Info("Dubbing", m));
    }

    /// <summary>
    /// Cuts a reference clip (~10 s, 22.05 kHz mono WAV) of a line's audio from the video, for the cloning engine.
    /// A little padding is trimmed and the clip is capped so the reference is one clean stretch of the speaker's voice.
    /// </summary>
    private static async Task CutReferenceClipAsync(string ffmpeg, string videoPath, Models.SubtitleCue line, string outPath, CancellationToken ct)
    {
        var start = line.Start;
        var span = line.End - line.Start;
        if (span > TimeSpan.FromSeconds(12)) span = TimeSpan.FromSeconds(12);
        if (span < TimeSpan.FromSeconds(2)) span = TimeSpan.FromSeconds(Math.Min(6, (line.End - line.Start).TotalSeconds + 4));
        var samples = await AudioExtractor.ReadAsync(ffmpeg, videoPath, 0, start, span, span, null, ct);
        WavFile.Write(outPath, samples, AudioExtractor.SampleRate);
    }

    // ---------------- Cloning across a cast (the full cloned dub) ----------------

    /// <summary>
    /// The clip-map key for a line, when cloning: the speaker's name (unique per cast) so each line is spoken from its
    /// own speaker's reference clip. Null when speakers aren't in use — a single clip then covers every line.
    /// </summary>
    private const string CloneAllKey = "clone-all";

    /// <summary>
    /// The line → clone key map: each line's speaker name when speakers are used (and that speaker has lines), else
    /// every line keyed to one clip. Mirrors <see cref="LineVoices"/> but for cloning, where the "voice" is a clip.
    /// </summary>
    private Dictionary<int, string> LineCloneKeys()
    {
        var map = new Dictionary<int, string>();
        if (UsingSpeakers)
            foreach (var r in SpeakerRows)
                foreach (var c in r.Speaker.Lines) map[c.Index] = r.Speaker.Name;
        else
            foreach (var cue in Cues) map[cue.Cue.Index] = CloneAllKey;
        return map;
    }

    /// <summary>
    /// Prepares the cloning engine for a whole dub: cuts one reference clip per speaker (from each speaker's longest
    /// line, so the copied voice is a clean stretch) and returns a <see cref="CloningTts"/> whose clip map is keyed to
    /// match <see cref="LineCloneKeys"/>. When speakers aren't in use, one clip (the longest line overall) covers all.
    /// The ~11.7 GB model is fetched by the exe on its first line, not here. Progress is reported into the dubbing bar.
    /// </summary>
    private async Task<IDubbingVoice> EnsureCloningCastAsync(string ffmpeg, Models.MediaItem video, double from, double share, CancellationToken ct, HashSet<int>? cloneSet = null)
    {
        if (!IsCloningReady)
            throw new InvalidOperationException("Download the voice-cloning engine first (step 1).");
        var workDir = Path.Combine(_s.CloningSidecar.Folder, "work");
        Directory.CreateDirectory(workDir);
        var refClips = new Dictionary<string, string>();

        // Only cut clips for speakers that actually have a line cloning will speak (the scope's line set), so a
        // "selected speakers" or "line range" scope doesn't spend time cutting clips for speakers Kokoro will voice.
        var cloneLines = cloneSet ?? new HashSet<int>(Cues.Select(c => c.Cue.Index));
        if (UsingSpeakers)
        {
            var speakers = SpeakerRows.Select(r => r.Speaker)
                .Where(s => s.Lines.Any(l => cloneLines.Contains(l.Index)))
                .ToList();
            for (int i = 0; i < speakers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = speakers[i];
                var line = s.Lines.OrderByDescending(l => (l.End - l.Start).Ticks).First();
                var clip = Path.Combine(workDir, $"ref-{SafeName(s.Name)}-{i}.wav");
                if (_dubbingLive) { DubbingStatus = $"Taking a reference clip of {s.Name}..."; DubbingProgress = from + (i / (double)Math.Max(1, speakers.Count)) * share; }
                await CutReferenceClipAsync(ffmpeg, video.FullPath, line, clip, ct);
                refClips[s.Name] = clip;
            }
            _s.Log.Info("Dubbing", $"Cloning: {refClips.Count} reference clips cut, one per speaker in scope (from each speaker's longest line).");
        }
        else
        {
            var line = Cues.Select(r => r.Cue).Where(c => cloneLines.Contains(c.Index)).Concat(Cues.Select(r => r.Cue))
                .OrderByDescending(l => (l.End - l.Start).Ticks).FirstOrDefault()
                ?? throw new InvalidOperationException("There are no lines to copy a voice from.");
            var clip = Path.Combine(workDir, "ref-all.wav");
            if (_dubbingLive) DubbingStatus = "Taking a reference clip of the original voice...";
            await CutReferenceClipAsync(ffmpeg, video.FullPath, line, clip, ct);
            refClips[CloneAllKey] = clip;
            _s.Log.Info("Dubbing", "Cloning: one reference clip cut (no speakers in use), used for every line.");
        }

        var gpu = _gpu ?? await Task.Run(GpuDetector.Detect);
        _gpu ??= gpu;
        return new CloningTts(_s.CloningSidecar.ExePath, refClips, CloningDeviceArg, CloningTimesteps, workDir, m => _s.Log.Info("Dubbing", m));
    }

    private static string SafeName(string name)
    {
        var cleaned = new string((name ?? string.Empty).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return cleaned.Length > 0 ? cleaned : "spk";
    }
}
