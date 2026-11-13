using System.Collections.Generic;
using System.Linq;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Guided burned-in removal of a batch: like guided dubbing, each queued episode is loaded into the Burned-in tab one at
/// a time, so the user keeps full control and — importantly — can see the detected subtitle area and a 10-second preview
/// before anything is painted over the picture. A banner on the Burned-in tab shows which episode is up and what's next,
/// with Next / Skip / Stop. On the Batch tab the user chooses whether Next just loads-and-detects and waits (the default,
/// safest) or also starts the removal at once. After the last episode the app returns to the Batch tab with a summary.
/// No change to how removal itself works — this only drives the existing tab.
///
/// Combined mode: when the user picks "removal + dubbing in one walk-through", each episode is first cleaned here and the
/// guided-dubbing run is started straight after, so you pass through the season once instead of twice.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>One episode in a guided burned-in run: the video whose picture to clean.</summary>
    private sealed record GuidedBurnItem(MediaItem Video);

    private List<GuidedBurnItem> _guidedBurn = new();
    private int _guidedBurnIndex = -1;
    private int _guidedBurnDone;

    /// <summary>True while a guided burned-in run is in progress (the Burned-in-tab banner shows only then).</summary>
    public bool GuidedBurnActive => _guidedBurnIndex >= 0 && _guidedBurnIndex < _guidedBurn.Count;

    /// <summary>After Detect, start the removal at once; otherwise load, detect and wait for the user to review and preview.</summary>
    public bool GuidedBurnAutoStart
    {
        get => _s.Config.BatchGuidedBurnAutoStart;
        set
        {
            if (_s.Config.BatchGuidedBurnAutoStart == value) return;
            _s.Config.BatchGuidedBurnAutoStart = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Walk through removal and dubbing together per episode (true), or run burned-in as its own session (false).</summary>
    public bool GuidedBurnCombined
    {
        get => _s.Config.BatchGuidedCombined;
        set
        {
            if (_s.Config.BatchGuidedCombined == value) return;
            _s.Config.BatchGuidedCombined = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GuidedBurnSeparate));
            OnPropertyChanged(nameof(StartGuidedBurnText));
            SaveSettings();
        }
    }

    /// <summary>The inverse of <see cref="GuidedBurnCombined"/>, for the "its own pass" radio button.</summary>
    public bool GuidedBurnSeparate
    {
        get => !GuidedBurnCombined;
        set { if (value) GuidedBurnCombined = false; }
    }

    public string StartGuidedBurnText => GuidedBurnCombined
        ? "Start guided removal + dubbing"
        : "Start guided burned-in removal";

    public string GuidedBurnBannerText => GuidedBurnActive
        ? $"Guided removal — episode {_guidedBurnIndex + 1} of {_guidedBurn.Count}: {_guidedBurn[_guidedBurnIndex].Video.Name}"
          + (GuidedBurnCombined ? " (dubbing follows)" : string.Empty)
        : string.Empty;

    public string GuidedBurnNextText
    {
        get
        {
            if (!GuidedBurnActive) return string.Empty;
            int next = _guidedBurnIndex + 1;
            return next < _guidedBurn.Count ? $"Next: {_guidedBurn[next].Video.Name}" : "This is the last episode.";
        }
    }

    public bool GuidedBurnHasNext => GuidedBurnActive && _guidedBurnIndex + 1 < _guidedBurn.Count;

    /// <summary>Can a guided removal run start: the queue has videos, OCR and FFmpeg are ready, and nothing is busy.</summary>
    public bool CanStartGuidedBurn => !GuidedBurnActive && !GuidedDubActive && !BatchBusy && !BurnedInBusy
                                      && OcrAvailable && FfmpegStatus.IsComplete && BuildGuidedBurnItems().Count > 0;

    public RelayCommand StartGuidedBurnCommand { get; private set; } = null!;
    public AsyncRelayCommand GuidedBurnNextCommand { get; private set; } = null!;
    public RelayCommand GuidedBurnSkipCommand { get; private set; } = null!;
    public RelayCommand GuidedBurnStopCommand { get; private set; } = null!;

    private void InitGuidedBurn()
    {
        StartGuidedBurnCommand = new RelayCommand(StartGuidedBurn, () => CanStartGuidedBurn);
        GuidedBurnNextCommand = new AsyncRelayCommand(GuidedBurnNextAsync, () => GuidedBurnActive && !BurnedInBusy);
        GuidedBurnSkipCommand = new RelayCommand(GuidedBurnSkip, () => GuidedBurnActive && !BurnedInBusy);
        GuidedBurnStopCommand = new RelayCommand(GuidedBurnStop, () => GuidedBurnActive);
    }

    /// <summary>The included queued video episodes (audio-only files have no picture to clean).</summary>
    private List<GuidedBurnItem> BuildGuidedBurnItems()
        => BatchRows.Where(r => r.Include && r.Item.IsVideo).Select(r => new GuidedBurnItem(r.Item)).ToList();

    private void StartGuidedBurn()
    {
        _guidedBurn = BuildGuidedBurnItems();
        if (_guidedBurn.Count == 0)
        {
            SetStatus("No queued video to clean. Add some video episodes to the batch first.", StatusKind.Warning);
            return;
        }
        _guidedBurnDone = 0;
        _guidedBurnIndex = -1;
        _s.Log.Info("Remove", $"▶ Guided burned-in removal started: {_guidedBurn.Count} episode(s), "
            + $"{(GuidedBurnAutoStart ? "detect and remove each" : "detect and wait at each")}"
            + (GuidedBurnCombined ? ", then guided dubbing." : "."));
        LoadGuidedBurnEpisode(0);
    }

    private void LoadGuidedBurnEpisode(int index)
    {
        if (index < 0 || index >= _guidedBurn.Count) { FinishGuidedBurn(); return; }
        _guidedBurnIndex = index;
        var item = _guidedBurn[index];
        var video = Files.FirstOrDefault(f => string.Equals(f.FullPath, item.Video.FullPath, System.StringComparison.OrdinalIgnoreCase)) ?? item.Video;
        if (!Files.Contains(video)) Files.Add(video);
        BurnedInVideo = video;
        SelectedTabIndex = Tabs.BurnedIn;
        RaiseGuidedBurn();
        _s.Log.Info("Remove", $"Guided removal {index + 1}/{_guidedBurn.Count}: loaded {item.Video.Name}.");
        SetStatus($"Guided removal {index + 1} of {_guidedBurn.Count}: {item.Video.Name} is loaded."
            + (GuidedBurnAutoStart ? " Detecting, then removing..." : " Press Detect, review the area, preview, then Next."), StatusKind.Info);
        if (GuidedBurnAutoStart) _ = AutoRemoveAsync();
    }

    /// <summary>
    /// Auto mode: detect the burned-in area, then remove it to a default output name (no save prompt), then advance.
    /// Removal only runs when detection actually found subtitles, so a subtitle-free episode is passed over untouched.
    /// </summary>
    private async System.Threading.Tasks.Task AutoRemoveAsync()
    {
        if (!GuidedBurnActive) return;
        await DetectBurnedInAsync().ConfigureAwait(true);
        if (!GuidedBurnActive) return; // stopped while detecting

        if (_detection is { Result.Likely: true })
        {
            // GuidedBurnActive is true, so CreateCleanVideoAsync saves next to the original with the auto name, no prompt.
            await CreateCleanVideoAsync(null).ConfigureAwait(true);
            _guidedBurnDone++;
        }
        else
        {
            _s.Log.Info("Remove", $"Guided removal: {BurnedInVideo?.Name}: no burned-in subtitles detected, left as is.");
            SetStatus($"{BurnedInVideo?.Name}: no burned-in subtitles found — nothing to remove.", StatusKind.Info);
        }
        if (GuidedBurnActive) AdvanceGuidedBurn(_guidedBurnIndex + 1);
    }

    private async System.Threading.Tasks.Task GuidedBurnNextAsync()
    {
        if (!GuidedBurnActive) return;
        _guidedBurnDone++;
        await System.Threading.Tasks.Task.CompletedTask;
        AdvanceGuidedBurn(_guidedBurnIndex + 1);
    }

    private void GuidedBurnSkip()
    {
        if (!GuidedBurnActive) return;
        _s.Log.Info("Remove", $"Guided removal: skipped {_guidedBurn[_guidedBurnIndex].Video.Name}.");
        AdvanceGuidedBurn(_guidedBurnIndex + 1);
    }

    private void GuidedBurnStop()
    {
        if (!GuidedBurnActive) return;
        _s.Log.Info("Remove", $"Guided removal stopped at episode {_guidedBurnIndex + 1} of {_guidedBurn.Count} ({_guidedBurnDone} done).");
        EndGuidedBurn($"Guided removal stopped: {_guidedBurnDone} of {_guidedBurn.Count} done.", handOff: false);
    }

    private void FinishGuidedBurn()
    {
        _s.Log.Success("Remove", $"Guided removal finished: {_guidedBurnDone} of {_guidedBurn.Count} episode(s) cleaned.");
        EndGuidedBurn($"Guided removal finished: {_guidedBurnDone} of {_guidedBurn.Count} episode(s) cleaned.", handOff: GuidedBurnCombined);
    }

    /// <summary>Move to the next episode, or finish when past the end.</summary>
    private void AdvanceGuidedBurn(int index) => LoadGuidedBurnEpisode(index);

    /// <summary>
    /// End the run. In combined mode a finished (not stopped) sweep then starts guided dubbing on the same queue, so the
    /// user walks the season once. Otherwise it returns to the Batch tab.
    /// </summary>
    private void EndGuidedBurn(string message, bool handOff)
    {
        _guidedBurnIndex = -1;
        RaiseGuidedBurn();
        if (handOff && CanStartGuidedDub)
        {
            SetStatus(message + " Now starting guided dubbing.", StatusKind.Success);
            StartGuidedDub();
            return;
        }
        SelectedTabIndex = Tabs.Batch;
        SetStatus(message + (handOff ? " (Nothing to dub — no subtitles are ready yet.)" : string.Empty), StatusKind.Success);
    }

    private void RaiseGuidedBurn()
    {
        OnPropertyChanged(nameof(GuidedBurnActive));
        OnPropertyChanged(nameof(GuidedBurnBannerText));
        OnPropertyChanged(nameof(GuidedBurnNextText));
        OnPropertyChanged(nameof(GuidedBurnHasNext));
        OnPropertyChanged(nameof(CanStartGuidedBurn));
        RelayCommand.Refresh();
    }
}
