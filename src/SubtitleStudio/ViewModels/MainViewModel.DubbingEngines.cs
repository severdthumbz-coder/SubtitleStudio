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
            OnPropertyChanged(nameof(DownloadPiperVoiceLabel));
            OnPropertyChanged(nameof(PiperVoiceReady));
            RelayCommand.Refresh();
        }
    }

    public bool IsPiperEngine => SelectedDubbingEngine.Id == "piper";

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

    /// <summary>The engine for the current dub: Piper when chosen and a voice is ready, otherwise Kokoro.</summary>
    private Task<IDubbingVoice> EnsureEngineAsync() => IsPiperEngine ? EnsurePiperAsync() : EnsureKokoroAsync();

    /// <summary>The voice id to speak with on the current engine (the engine's own selected voice).</summary>
    private string CurrentVoiceId => IsPiperEngine ? SelectedPiperVoice.Id : SelectedDubbingVoice.Id;

    /// <summary>A display name for the current voice, for status text.</summary>
    private string CurrentVoiceName => IsPiperEngine ? SelectedPiperVoice.DisplayName : SelectedDubbingVoice.Name;
}
