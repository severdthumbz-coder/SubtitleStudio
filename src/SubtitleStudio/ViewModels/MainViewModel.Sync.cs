using System.Collections;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Audio;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.ViewModels;

public sealed record SnapWindowOption(int Milliseconds, string Label);

/// <summary>
/// Sync tools in the Subtitles tab: shift, frame-rate stretch, two-point sync, and the audio-based
/// tools (detect offset, snap to speech). Maths is in SyncOperations; audio decoding in
/// AudioEnvelopeService. Every change is undoable.
/// </summary>
public sealed partial class MainViewModel
{
    private string _shiftText = "+0.500";
    private bool _syncSelectedOnly;
    private FrameRatePreset _frameRatePreset = SyncOperations.FrameRatePresets[0];
    private (SubtitleCue Cue, TimeSpan Source, TimeSpan Target)? _anchorA, _anchorB;
    private SnapWindowOption _snapWindow = null!;
    private bool _audioBusy;
    private double _audioProgress;
    private CancellationTokenSource? _audioCts;
    private (string Path, SpeechActivity Activity)? _speechCache;

    public IReadOnlyList<FrameRatePreset> FrameRatePresets => SyncOperations.FrameRatePresets;

    public IReadOnlyList<SnapWindowOption> SnapWindows { get; } = new[]
    {
        new SnapWindowOption(250, "within 0.25 s"),
        new SnapWindowOption(400, "within 0.4 s"),
        new SnapWindowOption(700, "within 0.7 s"),
        new SnapWindowOption(1000, "within 1 s"),
    };

    /// <summary>Signed offset: "+1.5", "-0.250", "-00:00:02,000".</summary>
    public string ShiftText
    {
        get => _shiftText;
        set => SetProperty(ref _shiftText, value ?? string.Empty);
    }

    /// <summary>When on, shift / stretch / snap only touch the cues selected in the list.</summary>
    public bool SyncSelectedOnly
    {
        get => _syncSelectedOnly;
        set => SetProperty(ref _syncSelectedOnly, value);
    }

    public FrameRatePreset SelectedFrameRatePreset
    {
        get => _frameRatePreset;
        set => SetProperty(ref _frameRatePreset, value ?? SyncOperations.FrameRatePresets[0]);
    }

    public SnapWindowOption SelectedSnapWindow
    {
        get => _snapWindow;
        set => SetProperty(ref _snapWindow, value ?? SnapWindows[1]);
    }

    public string AnchorAText => DescribeAnchor(_anchorA, "Anchor 1: select a cue near the start, play to where it should begin, then Set.");
    public string AnchorBText => DescribeAnchor(_anchorB, "Anchor 2: same for a cue near the end.");

    public bool AudioBusy
    {
        get => _audioBusy;
        private set => SetProperty(ref _audioBusy, value);
    }

    public double AudioProgress
    {
        get => _audioProgress;
        private set => SetProperty(ref _audioProgress, value);
    }

    public RelayCommand ApplyShiftCommand { get; private set; } = null!;
    public RelayCommand ApplyStretchCommand { get; private set; } = null!;
    public RelayCommand SetAnchorACommand { get; private set; } = null!;
    public RelayCommand SetAnchorBCommand { get; private set; } = null!;
    public RelayCommand ApplyAnchorsCommand { get; private set; } = null!;
    public RelayCommand ClearAnchorsCommand { get; private set; } = null!;
    public AsyncRelayCommand DetectOffsetCommand { get; private set; } = null!;
    public AsyncRelayCommand SnapToAudioCommand { get; private set; } = null!;
    public RelayCommand CancelAudioCommand { get; private set; } = null!;

    private void InitSync()
    {
        _snapWindow = SnapWindows[1];

        ApplyShiftCommand = new RelayCommand(p => ApplyShift(p as IList), _ => HasDocument && !AudioBusy);
        ApplyStretchCommand = new RelayCommand(p => ApplyStretch(p as IList), _ => HasDocument && !AudioBusy);
        SetAnchorACommand = new RelayCommand(() => SetAnchor(first: true), () => IsPreviewLoaded && SelectedCue is not null);
        SetAnchorBCommand = new RelayCommand(() => SetAnchor(first: false), () => IsPreviewLoaded && SelectedCue is not null);
        ApplyAnchorsCommand = new RelayCommand(ApplyAnchors, () => _anchorA is not null && _anchorB is not null && !AudioBusy);
        ClearAnchorsCommand = new RelayCommand(ClearAnchors, () => _anchorA is not null || _anchorB is not null);
        DetectOffsetCommand = new AsyncRelayCommand(DetectOffsetAsync, () => CanUseAudio);
        SnapToAudioCommand = new AsyncRelayCommand(p => SnapToAudioAsync(p as IList), _ => CanUseAudio);
        CancelAudioCommand = new RelayCommand(() => _audioCts?.Cancel(), () => AudioBusy);
    }

    private bool CanUseAudio => HasDocument && PairedVideo is not null && FfmpegStatus.HasFfmpeg && !AudioBusy;

    /// <summary>Cues the sync tools act on: the grid selection when "selected only" is on, else all.</summary>
    private List<SubtitleCue> SyncTargets(IList? selection)
    {
        if (SyncSelectedOnly)
        {
            var picked = selection?.OfType<CueRowViewModel>().Select(r => r.Cue).ToList() ?? new List<SubtitleCue>();
            if (picked.Count == 0 && SelectedCue is not null) picked.Add(SelectedCue.Cue);
            return picked;
        }
        return Cues.Select(r => r.Cue).ToList();
    }

    private void ApplyShift(IList? selection)
    {
        var text = ShiftText.Trim();
        bool negative = text.StartsWith('-');
        if (!Timecode.TryParse(text.TrimStart('+', '-'), out var magnitude) || magnitude == TimeSpan.Zero)
        {
            SetStatus($"'{ShiftText}' is not a usable offset. Examples: +1.5, -0.250, -00:00:02,000.", StatusKind.Warning);
            return;
        }

        var targets = SyncTargets(selection);
        if (targets.Count == 0) return;
        var offset = negative ? magnitude.Negate() : magnitude;

        PushUndo();
        SyncOperations.Shift(targets, offset);
        AfterTimingChange();
        SetStatus($"Shifted {Describe(targets.Count)} {(negative ? "earlier" : "later")} by {Timecode.FormatDuration(magnitude)}.", StatusKind.Success);
    }

    private void ApplyStretch(IList? selection)
    {
        var targets = SyncTargets(selection);
        if (targets.Count == 0) return;

        PushUndo();
        SyncOperations.Stretch(targets, SelectedFrameRatePreset.Factor);
        AfterTimingChange();
        SetStatus($"Re-timed {Describe(targets.Count)} for {SelectedFrameRatePreset.Label} (x{SelectedFrameRatePreset.Factor:0.#####}).", StatusKind.Success);
    }

    private void SetAnchor(bool first)
    {
        if (SelectedCue is null) return;
        var anchor = (SelectedCue.Cue, SelectedCue.Cue.Start, _position);
        if (first) _anchorA = anchor; else _anchorB = anchor;
        OnPropertyChanged(first ? nameof(AnchorAText) : nameof(AnchorBText));
        RelayCommand.Refresh();
    }

    private void ApplyAnchors()
    {
        if (_anchorA is not { } a || _anchorB is not { } b) return;
        var snapshot = SnapshotCues();
        try
        {
            // Anchors hold the cue times as they were when set; later edits to those cues don't matter.
            var (scale, offset) = SyncOperations.TwoPointSync(Cues.Select(r => r.Cue).ToList(), a.Source, a.Target, b.Source, b.Target);
            PushUndo(snapshot);
            AfterTimingChange();
            ClearAnchors();
            SetStatus($"Two-point sync applied: speed x{scale:0.#####}, offset {Timecode.FormatDuration(offset)}.", StatusKind.Success);
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message, StatusKind.Warning);
        }
    }

    private void ClearAnchors()
    {
        _anchorA = _anchorB = null;
        OnPropertyChanged(nameof(AnchorAText));
        OnPropertyChanged(nameof(AnchorBText));
    }

    private async Task DetectOffsetAsync()
    {
        var activity = await GetSpeechActivityAsync();
        if (activity is null) return;

        var estimate = SyncOperations.DetectOffset(Cues.Where(r => !r.IsComment).Select(r => r.Cue).ToList(), activity, TimeSpan.FromSeconds(20));
        if (estimate.Confidence < 0.15 || estimate.Offset == TimeSpan.Zero)
        {
            SetStatus(estimate.Offset == TimeSpan.Zero && estimate.Confidence >= 0.15
                ? "The subtitle already lines up with the speech (no offset found)."
                : "No clear offset found. The audio may be mostly music, or the subtitle drifts (try the frame-rate or two-point tools).",
                StatusKind.Warning);
            return;
        }

        var sign = estimate.Offset > TimeSpan.Zero ? "later" : "earlier";
        var amount = Timecode.FormatDuration(estimate.Offset.Duration());
        ShiftText = (estimate.Offset > TimeSpan.Zero ? "+" : "-") + estimate.Offset.Duration().TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        if (_s.Dialogs.Confirm("Detected offset",
                $"The subtitle seems to need moving {amount} {sign} (confidence {estimate.Confidence:P0}).\n\nShift all cues now? (You can undo.)"))
        {
            PushUndo();
            SyncOperations.Shift(Cues.Select(r => r.Cue), estimate.Offset);
            AfterTimingChange();
            SetStatus($"Shifted all cues {amount} {sign} to match the speech.", StatusKind.Success);
        }
        else
        {
            SetStatus($"Detected offset: {amount} {sign}. It's in the Shift box if you want to apply it later.");
        }
    }

    private async Task SnapToAudioAsync(IList? selection)
    {
        var activity = await GetSpeechActivityAsync();
        if (activity is null) return;

        var targets = SyncTargets(selection).Where(c => c.Extra is null || !c.Extra.TryGetValue("_kind", out var k) || k != "Comment").ToList();
        if (targets.Count == 0) return;

        PushUndo();
        var ordered = targets.OrderBy(c => c.Start).ToList();
        var result = SyncOperations.SnapToSpeech(ordered, activity, TimeSpan.FromMilliseconds(SelectedSnapWindow.Milliseconds));
        AfterTimingChange();
        SetStatus($"Snapped {result.StartsSnapped} starts and {result.EndsSnapped} ends to speech; {result.Unchanged} cues had no speech edge nearby.",
            result.StartsSnapped + result.EndsSnapped > 0 ? StatusKind.Success : StatusKind.Warning);
    }

    /// <summary>Decodes the paired video's audio once per video and caches the speech mask.</summary>
    private async Task<SpeechActivity?> GetSpeechActivityAsync()
    {
        var video = PairedVideo;
        var ffmpeg = FfmpegStatus.FfmpegPath;
        if (video is null || ffmpeg is null) return null;

        if (_speechCache is { } cached && string.Equals(cached.Path, video.FullPath, StringComparison.OrdinalIgnoreCase))
            return cached.Activity;

        _audioCts = new CancellationTokenSource();
        AudioBusy = true;
        AudioProgress = 0;
        RelayCommand.Refresh();
        var progress = new Progress<double>(f =>
        {
            AudioProgress = f * 100;
            SetStatus($"Analysing the audio of {video.Name}... {f:P0}");
        });

        try
        {
            var envelope = await _s.Audio.ExtractAsync(ffmpeg, video.FullPath, video.Duration, progress, _audioCts.Token);
            var activity = await Task.Run(() => SpeechActivity.FromEnvelope(envelope));
            _speechCache = (video.FullPath, activity);
            return activity;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Audio analysis cancelled.");
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            SetStatus($"Could not analyse the audio: {ex.Message}", StatusKind.Error);
            return null;
        }
        finally
        {
            AudioBusy = false;
            _audioCts.Dispose();
            _audioCts = null;
            RelayCommand.Refresh();
        }
    }

    private void AfterTimingChange()
    {
        SyncDocumentFromRows();
        IsDirty = true;
        Revalidate();
        UpdateOverlay();
    }

    private string Describe(int count) => count == Cues.Count ? $"all {count} cues" : count == 1 ? "1 cue" : $"{count} cues";

    private static string DescribeAnchor((SubtitleCue Cue, TimeSpan Source, TimeSpan Target)? anchor, string empty)
        => anchor is { } a ? $"#{a.Cue.Index}: {Timecode.Format(a.Source)} → {Timecode.Format(a.Target)}" : empty;
}
