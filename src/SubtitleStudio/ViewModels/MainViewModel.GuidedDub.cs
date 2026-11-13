using System.Collections.Generic;
using System.Linq;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Batch;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Guided dubbing of a batch: instead of dubbing headlessly, each queued episode is loaded into the Dubbing tab one at
/// a time so the user keeps full control (speakers, voices, options) and quality. A banner on the Dubbing tab shows
/// which episode is up and what's next, with Next / Skip / Stop. The user can choose, on the Batch tab, whether Next
/// just loads the episode and waits, or loads and starts the dub straight away. After the last episode the app returns
/// to the Batch tab with a summary. No change to how dubbing itself works — this only drives the existing tab.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>One episode in a guided-dubbing run: the video and the subtitle to dub (its translation, else transcript).</summary>
    private sealed record GuidedDubItem(MediaItem Video, string SubtitlePath);

    private List<GuidedDubItem> _guidedDub = new();
    private int _guidedDubIndex = -1;
    private int _guidedDubDone;

    /// <summary>True while a guided-dubbing run is in progress (the Dubbing-tab banner shows only then).</summary>
    public bool GuidedDubActive => _guidedDubIndex >= 0 && _guidedDubIndex < _guidedDub.Count;

    /// <summary>Load the next episode and start the dub at once; otherwise load it and wait for the user.</summary>
    public bool GuidedDubAutoStart
    {
        get => _s.Config.BatchGuidedDubAutoStart;
        set
        {
            if (_s.Config.BatchGuidedDubAutoStart == value) return;
            _s.Config.BatchGuidedDubAutoStart = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string GuidedDubBannerText => GuidedDubActive
        ? $"Guided dubbing — episode {_guidedDubIndex + 1} of {_guidedDub.Count}: {_guidedDub[_guidedDubIndex].Video.Name}"
        : string.Empty;

    public string GuidedDubNextText
    {
        get
        {
            if (!GuidedDubActive) return string.Empty;
            int next = _guidedDubIndex + 1;
            return next < _guidedDub.Count ? $"Next: {_guidedDub[next].Video.Name}" : "This is the last episode.";
        }
    }

    public bool GuidedDubHasNext => GuidedDubActive && _guidedDubIndex + 1 < _guidedDub.Count;

    /// <summary>Can a guided-dubbing run be started: the queue has episodes with a subtitle to dub, and nothing is busy.</summary>
    public bool CanStartGuidedDub => !GuidedDubActive && !BatchBusy && !DubbingBusy && BuildGuidedDubItems().Count > 0;

    public RelayCommand StartGuidedDubCommand { get; private set; } = null!;
    public AsyncRelayCommand GuidedDubNextCommand { get; private set; } = null!;
    public RelayCommand GuidedDubSkipCommand { get; private set; } = null!;
    public RelayCommand GuidedDubStopCommand { get; private set; } = null!;

    private void InitGuidedDub()
    {
        StartGuidedDubCommand = new RelayCommand(StartGuidedDub, () => CanStartGuidedDub);
        GuidedDubNextCommand = new AsyncRelayCommand(GuidedDubNextAsync, () => GuidedDubActive && !DubbingBusy);
        GuidedDubSkipCommand = new RelayCommand(GuidedDubSkip, () => GuidedDubActive && !DubbingBusy);
        GuidedDubStopCommand = new RelayCommand(GuidedDubStop, () => GuidedDubActive);
    }

    /// <summary>The included queued episodes that have a subtitle to dub (translation first, else transcript).</summary>
    private List<GuidedDubItem> BuildGuidedDubItems()
    {
        var items = new List<GuidedDubItem>();
        var target = BatchTarget;
        foreach (var row in BatchRows.Where(r => r.Include && r.Item.IsVideo))
        {
            var sub = (target is not null ? SeasonQueue.FindLanguage(row.FullPath, target) : null)
                      ?? SeasonQueue.FindTranscript(row.FullPath, SelectedTranscribeLanguage.Code, target);
            if (sub is not null) items.Add(new GuidedDubItem(row.Item, sub));
        }
        return items;
    }

    private void StartGuidedDub()
    {
        _guidedDub = BuildGuidedDubItems();
        if (_guidedDub.Count == 0)
        {
            SetStatus("No queued episode has a subtitle to dub yet. Transcribe/translate them first.", StatusKind.Warning);
            return;
        }
        _guidedDubDone = 0;
        _guidedDubIndex = -1;
        _s.Log.Info("Dubbing", $"▶ Guided dubbing started: {_guidedDub.Count} episode(s), {(GuidedDubAutoStart ? "load and start each" : "load and wait at each")}.");
        LoadGuidedDubEpisode(0);
    }

    private void LoadGuidedDubEpisode(int index)
    {
        if (index < 0 || index >= _guidedDub.Count) { FinishGuidedDub(); return; }
        _guidedDubIndex = index;
        var item = _guidedDub[index];
        try
        {
            var doc = _s.SubtitleFormats.Load(item.SubtitlePath);
            var video = Files.FirstOrDefault(f => string.Equals(f.FullPath, item.Video.FullPath, System.StringComparison.OrdinalIgnoreCase)) ?? item.Video;
            if (!Files.Contains(video)) Files.Add(video);
            LoadDocument(doc, video, selectIndex: 0);
            SelectedTabIndex = Tabs.Dubbing;
            RaiseGuidedDub();
            _s.Log.Info("Dubbing", $"Guided dubbing {index + 1}/{_guidedDub.Count}: loaded {item.Video.Name} with {System.IO.Path.GetFileName(item.SubtitlePath)}.");
            SetStatus($"Guided dubbing {index + 1} of {_guidedDub.Count}: {item.Video.Name} is loaded." + (GuidedDubAutoStart ? " Starting the dub..." : " Dub it, then press Next."), StatusKind.Info);
            if (GuidedDubAutoStart && CanMakeDubbedVideo) _ = AutoStartDubAsync();
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.InvalidOperationException)
        {
            _s.Log.Error("Dubbing", $"Guided dubbing: couldn't load {item.Video.Name}: {ex.Message}");
            SetStatus($"Couldn't load {item.Video.Name}: {ex.Message}. Skipping.", StatusKind.Warning);
            LoadGuidedDubEpisode(index + 1);
        }
    }

    /// <summary>
    /// Auto-start mode: dub the loaded episode to a default output name (no save prompt), then advance when it finishes.
    /// </summary>
    private async System.Threading.Tasks.Task AutoStartDubAsync()
    {
        if (PairedVideo is not { } video || !CanMakeDubbedVideo) return;
        var output = Services.Dubbing.DubMixer.SuggestOutput(video.FullPath, small: false);
        await MakeDubbedVideoAsync(output).ConfigureAwait(true);
        if (GuidedDubActive) { _guidedDubDone++; LoadGuidedDubEpisode(_guidedDubIndex + 1); }
    }

    private async System.Threading.Tasks.Task GuidedDubNextAsync()
    {
        if (!GuidedDubActive) return;
        _guidedDubDone++;
        await System.Threading.Tasks.Task.CompletedTask;
        LoadGuidedDubEpisode(_guidedDubIndex + 1);
    }

    private void GuidedDubSkip()
    {
        if (!GuidedDubActive) return;
        _s.Log.Info("Dubbing", $"Guided dubbing: skipped {_guidedDub[_guidedDubIndex].Video.Name}.");
        LoadGuidedDubEpisode(_guidedDubIndex + 1);
    }

    private void GuidedDubStop()
    {
        if (!GuidedDubActive) return;
        _s.Log.Info("Dubbing", $"Guided dubbing stopped at episode {_guidedDubIndex + 1} of {_guidedDub.Count} ({_guidedDubDone} done).");
        EndGuidedDub($"Guided dubbing stopped: {_guidedDubDone} of {_guidedDub.Count} done.");
    }

    private void FinishGuidedDub()
    {
        _s.Log.Success("Dubbing", $"Guided dubbing finished: {_guidedDubDone} of {_guidedDub.Count} episode(s) done.");
        EndGuidedDub($"Guided dubbing finished: {_guidedDubDone} of {_guidedDub.Count} episode(s) dubbed.");
    }

    private void EndGuidedDub(string message)
    {
        _guidedDubIndex = -1;
        RaiseGuidedDub();
        SelectedTabIndex = Tabs.Batch;
        SetStatus(message, StatusKind.Success);
    }

    private void RaiseGuidedDub()
    {
        OnPropertyChanged(nameof(GuidedDubActive));
        OnPropertyChanged(nameof(GuidedDubBannerText));
        OnPropertyChanged(nameof(GuidedDubNextText));
        OnPropertyChanged(nameof(GuidedDubHasNext));
        OnPropertyChanged(nameof(CanStartGuidedDub));
        RelayCommand.Refresh();
    }
}
