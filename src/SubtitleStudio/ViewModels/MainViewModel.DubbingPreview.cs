using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Muxing;

namespace SubtitleStudio.ViewModels;

/// <summary>A preview length to choose.</summary>
public sealed record DubPreviewLength(TimeSpan Length, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Dubbing tab: hear a stretch of the dub before making the whole video. The lines in the stretch are
/// spoken with the current voice, speed, fitting and level, mixed over the original sound as the dubbed
/// video will be, and played in the tab (Services/Dubbing/DubPreview). Nothing is saved.
/// </summary>
public sealed partial class MainViewModel
{
    private ClipPlayerViewModel? _dubPlayer;
    private DubPreviewLength? _dubPreviewLength;
    private string _dubPreviewText = string.Empty;
    private bool _hasDubPreview;

    public IReadOnlyList<DubPreviewLength> DubPreviewLengths { get; } = new[]
    {
        new DubPreviewLength(TimeSpan.FromSeconds(30), "30 seconds"),
        new DubPreviewLength(TimeSpan.FromMinutes(1), "1 minute"),
        new DubPreviewLength(TimeSpan.FromMinutes(2), "2 minutes"),
    };

    public DubPreviewLength SelectedDubPreviewLength
    {
        get => _dubPreviewLength ?? DubPreviewLengths[1];
        set { if (value is not null) SetProperty(ref _dubPreviewLength, value); }
    }

    /// <summary>The player in the tab (null where no player can be made, as in tests without one).</summary>
    public ClipPlayerViewModel? DubPlayer => _dubPlayer;

    public bool HasDubPreview
    {
        get => _hasDubPreview;
        private set => SetProperty(ref _hasDubPreview, value);
    }

    /// <summary>What the clip holds: "Lines 120–134 (12:03–13:03) in Aoede's voice, original sound lower."</summary>
    public string DubPreviewText
    {
        get => _dubPreviewText;
        private set => SetProperty(ref _dubPreviewText, value);
    }

    /// <summary>Where the preview starts: the line selected in the Subtitles tab, else the first line.</summary>
    public string DubPreviewStartText
        => SelectedCue is { } c ? $"From line {c.Number} ({ClockText(c.Cue.Start)}), the line selected in the Subtitles tab."
            : Cues.Count > 0 ? $"From the first line ({ClockText(Cues[0].Cue.Start)}). Select a line in the Subtitles tab to start there." : string.Empty;

    private static string ClockText(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    public AsyncRelayCommand PreviewDubCommand { get; private set; } = null!;

    private async Task PreviewDubAsync()
    {
        if (PairedVideo is not { } video) return;
        var ff = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        if (!ff.IsComplete)
        {
            SetStatus("ffmpeg is needed to preview the dub: set it in Settings > Engines & tools.", StatusKind.Error);
            return;
        }
        var all = Cues.Select(r => r.Cue).Where(c => c.End > c.Start).OrderBy(c => c.Start).ToList();
        if (all.Count == 0) return;
        var anchor = SelectedCue?.Cue.Start ?? all[0].Start;
        var length = SelectedDubPreviewLength.Length;
        if (video.Duration is { } d && d < length) length = d;
        var start = DubPreview.StartFor(anchor, length, video.Duration);
        var lines = DubPreview.LinesIn(all, start, length);
        var voice = SelectedDubbingVoice;
        float speed = (float)SelectedDubbingSpeed.Value;
        bool fit = DubbingFit;
        var duck = SelectedDubbingDuck;

        _dubPlayer?.Unload();
        HasDubPreview = false;
        DubPreview.ClearOld();
        await RunDubbingWorkAsync("Preparing the preview", async ct =>
        {
            var folder = Directory.CreateDirectory(DubPreview.Folder).FullName;
            var stamp = DateTime.Now.ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var voicePath = Path.Combine(folder, $"dub-{stamp}.wav");
            var clip = Path.Combine(folder, $"dub-{stamp}.mkv");
            var tts = await EnsureKokoroAsync();
            bool removing = duck.Level == DuckLevel.Removed;
            VoicePlacement? placement = null;
            (float[] L, float[] R, float[] VL, float[] VR)? separated = null;
            if (removing)
            {
                var sound = await PreviewVoicesAsync(video, ff.FfmpegPath!, start, length, ct);
                separated = sound;
                placement = new VoicePlacement(DubBed.MeasureLines(sound.VoicesLeft, sound.VoicesRight, TimeSpan.Zero, lines));
            }
            double speakFrom = removing ? 30 : 0, speakShare = removing ? 45 : 70;
            DubbingStatus = $"Speaking {lines.Count} line{(lines.Count == 1 ? "" : "s")}...";
            var options = new VoiceTrackOptions(speed, fit, length, tts.Concurrency, placement is null ? null : placement.Gains, placement is null ? null : placement.Shape);
            var progress = new Progress<EngineProgress>(p => { if (_dubbingLive) DubbingProgress = speakFrom + p.Fraction * speakShare; });
            var lineVoices = LineVoices();
            var result = await VoiceTrack.BuildAsync(lines, (cue, s) => tts.Speak(cue.Text, lineVoices?.GetValueOrDefault(cue.Index) ?? voice.Id, s), options, voicePath, progress, ct);

            string? bedPath = null;
            if (separated is { } sound2)
            {
                DubbingStatus = "Taking the original voices out under the English lines...";
                bedPath = Path.Combine(folder, $"dub-{stamp}.sound.wav");
                var activity = new DubBed.VoiceActivity(DubBed.VoiceActivity.Levels(sound2.VL, sound2.VR), placement!.Usual);
                var removal = new DubBed.Removal(result.Covered ?? Array.Empty<(TimeSpan, TimeSpan)>(), TimeSpan.Zero, DubBed.SampleRate, activity);
                var (bl, br) = DubBed.Bed(sound2.L, sound2.R, sound2.VL, sound2.VR, removal);
                var path = bedPath;
                await Task.Run(() =>
                {
                    using var wav = new WavWriter(path, DubBed.SampleRate, 2);
                    wav.WriteStereo(bl, br);
                }, ct);
            }

            DubbingStatus = "Mixing the voice over the original sound...";
            var encoder = await EncoderAsync(ff.FfmpegPath!, ct);
            var args = DubPreview.RenderArgs(video.FullPath, voicePath, start, length, 0, duck.Level, DubPreview.FastVideo(encoder), clip, bedPath);
            _s.Log.Detail("Dubbing", "Preview: " + Services.ActivityLog.CommandLine(ff.FfmpegPath!, args));
            double renderFrom = speakFrom + speakShare;
            var render = new Progress<double>(f => { if (_dubbingLive) DubbingProgress = renderFrom + f * (100 - renderFrom); });
            await SubtitleMuxer.RunFfmpegAsync(ff.FfmpegPath!, args, length, render, ct);
            foreach (var f in new[] { voicePath, bedPath })
                try { if (f is not null && File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            var inStretch = all.Where(c => c.Start >= start && c.Start < start + length).ToList();
            var first = inStretch.FirstOrDefault();
            var last = inStretch.LastOrDefault();
            DubPreviewText = (first is null ? "No lines in this stretch" : first == last ? $"Line {first.Index}" : $"Lines {first.Index}–{last!.Index}")
                + $" ({ClockText(start)}–{ClockText(start + length)}), {(UsingSpeakers ? "each speaker in their own voice" : voice.Name + "'s voice")}, "
                + (removing ? "original voices removed, each line placed like the voice it replaces" : $"original sound {duck.Id switch { "little" => "a little lower", "much" => "much lower", _ => "lower" }}")
                + (fit && result.SpedUp + result.Stretched > 0 ? $"; {result.SpedUp + result.Stretched} said faster to fit" : string.Empty)
                + ". Change the voice, speed or level and preview again to compare.";
            if (_dubPlayer is { } player && player.Load(clip, start, length))
            {
                HasDubPreview = true;
                DubbingStatus = "Preview ready: press play.";
            }
            else DubbingStatus = $"Preview made, but the player isn't available here. The clip is {clip}.";
            _s.Log.Info("Dubbing", $"Preview of {ClockText(start)}–{ClockText(start + length)}: {lines.Count} lines, {(UsingSpeakers ? "each speaker in their own voice" : voice.Id)}, speed {speed:0.0#}, {(fit ? "fitted" : "not fitted")}, {duck.Id}{(placement is null ? "" : "; " + placement.Describe())}.");
        });
    }

    private void InitDubbingPreview()
    {
        if (_s.CreatePlayer is { } create)
        {
            _dubPlayer = new ClipPlayerViewModel(create(), RunOnUi);
            _dubPlayer.Failed += m => SetStatus(m, StatusKind.Error);
        }
        PreviewDubCommand = new AsyncRelayCommand(PreviewDubAsync, () => CanMakeDubbedVideo);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedCue) or nameof(HasDocument)) OnPropertyChanged(nameof(DubPreviewStartText));
        };
    }
}
