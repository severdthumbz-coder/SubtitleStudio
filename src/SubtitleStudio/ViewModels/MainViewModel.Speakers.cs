using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Dubbing;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Dubbing tab, Speakers: who says each line (voiceprints of the video's sound, grouped), male or female
/// from the pitch, a distinct matching voice for each, corrections (name, gender, voice, "same person
/// as", the selected line's speaker), and the show's speakers remembered across episodes
/// (Services/Dubbing/SpeakerFinder, SpeakerBook).
/// </summary>
public sealed partial class MainViewModel
{
    private IVoiceprintModel? _voiceprints;
    private SpeakerBook? _speakerBook;
    private string _speakersStatus = string.Empty;
    private bool _changingLineSpeaker;

    /// <summary>Loads the voiceprint model. Replaceable in tests.</summary>
    internal Func<string, IVoiceprintModel> LoadVoiceprintModel { get; set; } = path => new OnnxVoiceprintModel(path);

    public ObservableCollection<SpeakerRowViewModel> SpeakerRows { get; } = new();

    public bool HasSpeakers => SpeakerRows.Count > 0;

    public string SpeakersStatus
    {
        get => _speakersStatus;
        private set => SetProperty(ref _speakersStatus, value);
    }

    /// <summary>Each speaker in their own voice (when speakers have been found); otherwise everyone in the voice of step 2.</summary>
    public bool DubbingPerSpeaker
    {
        get => _s.Config.DubbingPerSpeaker;
        set
        {
            if (_s.Config.DubbingPerSpeaker == value) return;
            _s.Config.DubbingPerSpeaker = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    private bool UsingSpeakers => DubbingPerSpeaker && HasSpeakers;

    public AsyncRelayCommand FindSpeakersCommand { get; private set; } = null!;

    /// <summary>The selected line's speaker (the line selected in the Subtitles tab), to correct it.</summary>
    public SpeakerRowViewModel? SelectedLineSpeaker
    {
        get => SelectedCue is { } c ? SpeakerRows.FirstOrDefault(r => r.Speaker.Lines.Contains(c.Cue)) : null;
        set
        {
            if (_changingLineSpeaker || value is null || SelectedCue is not { } c) return;
            var current = SpeakerRows.FirstOrDefault(r => r.Speaker.Lines.Contains(c.Cue));
            if (current == value) return;
            current?.Speaker.Lines.Remove(c.Cue);
            value.Speaker.Lines.Add(c.Cue);
            if (current is { Speaker.Lines.Count: 0 }) SpeakerRows.Remove(current);
            RefreshSpeakerRows();
            SpeakersStatus = $"Line {c.Number} is now spoken by {value.Name}.";
        }
    }

    public string SelectedLineText => SelectedCue is { } c ? $"Line {c.Number}: {Services.Subtitles.SubtitleText.StripTags(c.Text).Replace('\n', ' ')}" : "Select a line in the Subtitles tab to change who speaks it.";

    public bool CanFindSpeakers => !DubbingBusy && HasTtsModel && PairedVideo is not null && Cues.Count > 0 && !DubbingSourceIsForeignScript;

    private async Task FindSpeakersAsync()
    {
        if (PairedVideo is not { } video) return;
        var ff = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        if (!ff.IsComplete)
        {
            SetStatus("ffmpeg is needed to listen to the video: set it in Settings > Engines & tools.", StatusKind.Error);
            return;
        }
        var cues = Cues.Select(r => r.Cue).ToList();
        await RunDubbingWorkAsync("Finding the speakers", async ct =>
        {
            if (!_s.TtsModels.HasVoiceprint)
            {
                DubbingStatus = "Downloading the voiceprint model (26 MB)...";
                var dl = new Progress<ModelDownloadProgress>(p => { if (_dubbingLive) DubbingProgress = p.Fraction * 10; });
                await _s.TtsModels.DownloadVoiceprintAsync(ModelHttp, dl, ct);
                _s.Log.Success("Dubbing", $"Voiceprint model downloaded and checked: {TtsModelStore.VoiceprintModel.FileName}.");
            }
            _voiceprints ??= await Task.Run(() => LoadVoiceprintModel(_s.TtsModels.VoiceprintPath), ct);

            DubbingStatus = $"Listening to {video.Name}...";
            var started = DateTime.UtcNow;
            var read = new Progress<double>(f => { if (_dubbingLive) DubbingProgress = 10 + f * 30; });
            var audio = await AudioExtractor.ReadAsync(ff.FfmpegPath!, video.FullPath, 0, null, null, video.Duration, read, ct);

            DubbingStatus = $"Telling the voices apart in {cues.Count} lines...";
            var book = SpeakerBook.Open(Path.Combine(AppPaths.ExeDirectory, "speakers"), NameConsistency.ShowOf(video.Name));
            var find = new Progress<double>(f => { if (_dubbingLive) DubbingProgress = 40 + f * 60; });
            var result = await Task.Run(() => SpeakerFinder.Find(audio, cues, _voiceprints, book, find, ct), ct);
            _speakerBook = book;
            ShowSpeakers(result.Speakers);

            var took = DateTime.UtcNow - started;
            int men = result.Speakers.Count(s => s.Gender == "male"), women = result.Speakers.Count(s => s.Gender == "female");
            SpeakersStatus = $"{result.Speakers.Count} speaker{(result.Speakers.Count == 1 ? "" : "s")} ({women} female, {men} male)"
                + (result.Remembered > 0 ? $", {result.Remembered} remembered from earlier episodes" : string.Empty)
                + ". Check the names and voices, and say when two are the same person.";
            DubbingStatus = $"Found {result.Speakers.Count} speakers in {FormatEta(took)}.";
            _s.Log.Success("Dubbing", $"Speakers of {video.Name}: {result.Speakers.Count} found in {took.TotalSeconds:0} s; {result.Measured} lines measured, {result.ByNeighbour} too short (given the speaker of the line next to them); {result.Remembered} remembered from {book.Path}.");
            foreach (var s in result.Speakers)
                _s.Log.Detail("Dubbing", $"{s.Name}: {s.Lines.Count} lines, {s.SpeechTime.TotalMinutes:0.0} min, pitch {(s.PitchHz is { } hz ? $"{hz:0} Hz" : "unknown")} ({s.Gender ?? "unknown"}), voice {s.Voice}{(s.Remembered ? ", remembered" : "")}.");
            DubbingPerSpeaker = true;
        });
    }

    private void ShowSpeakers(IEnumerable<Speaker> speakers)
    {
        SpeakerRows.Clear();
        foreach (var s in speakers)
            SpeakerRows.Add(new SpeakerRowViewModel(s, OnSpeakerChanged, MergeSpeakers, HearSpeakerOriginalAsync, HearSpeakerVoiceAsync));
        RefreshSpeakerRows();
    }

    private void RefreshSpeakerRows()
    {
        var rows = SpeakerRows.OrderByDescending(r => r.Speaker.Lines.Count).ToList();
        if (!rows.SequenceEqual(SpeakerRows))
        {
            SpeakerRows.Clear();
            foreach (var r in rows) SpeakerRows.Add(r);
        }
        foreach (var r in SpeakerRows)
        {
            r.SetOthers(SpeakerRows);
            r.Refresh();
        }
        OnPropertyChanged(nameof(HasSpeakers));
        RaiseSelectedLineSpeaker();
    }

    private void RaiseSelectedLineSpeaker()
    {
        _changingLineSpeaker = true;
        try
        {
            OnPropertyChanged(nameof(SelectedLineSpeaker));
            OnPropertyChanged(nameof(SelectedLineText));
        }
        finally
        {
            _changingLineSpeaker = false;
        }
    }

    private void OnSpeakerChanged(SpeakerRowViewModel row)
    {
        // A gender corrected by hand: a voice of that gender, unless one was chosen already.
        var pool = row.Speaker.Gender == "male" ? SpeakerFinder.MaleVoices : SpeakerFinder.FemaleVoices;
        if (row.Speaker.GenderSetByHand && !pool.Contains(row.Speaker.Voice))
            row.Speaker.Voice = SpeakerFinder.VoiceForGender(row.Speaker.Gender ?? "female", SpeakerRows.Select(r => r.Speaker).ToList(), row.Speaker);
        row.Refresh();
        foreach (var r in SpeakerRows) r.SetOthers(SpeakerRows);
    }

    private void MergeSpeakers(SpeakerRowViewModel from, SpeakerRowViewModel into)
    {
        into.Speaker.Lines.AddRange(from.Speaker.Lines);
        if (from.Speaker.Voiceprint is { } a && into.Speaker.Voiceprint is { } b)
            into.Speaker.Voiceprint = Voiceprints.Mean(new[] { a, b });
        SpeakerRows.Remove(from);
        RefreshSpeakerRows();
        SpeakersStatus = $"{from.Name} and {into.Name} are now one speaker, {into.Name}.";
        _s.Log.Info("Dubbing", $"Speakers joined: {from.Name} into {into.Name} ({into.Speaker.Lines.Count} lines).");
    }

    /// <summary>The voice for each line (by its index) with a voice per speaker; null: everyone in one voice.</summary>
    private Dictionary<int, string>? LineVoices()
    {
        if (!UsingSpeakers) return null;
        var map = new Dictionary<int, string>();
        foreach (var r in SpeakerRows)
            foreach (var c in r.Speaker.Lines) map[c.Index] = r.Speaker.Voice;
        return map;
    }

    /// <summary>The speakers kept for the show's next episodes (after a voice track or dub made with them).</summary>
    private void RememberSpeakers()
    {
        if (!UsingSpeakers || _speakerBook is null) return;
        _speakerBook.Remember(SpeakerRows.Select(r => r.Speaker).ToList());
        _s.Log.Info("Dubbing", $"Speakers remembered for the show ({_speakerBook.People.Count} in {Path.GetFileName(_speakerBook.Path)}).");
    }

    /// <summary>Plays the speaker's longest line from the video's own sound.</summary>
    private async Task HearSpeakerOriginalAsync(SpeakerRowViewModel row)
    {
        if (PairedVideo is not { } video || row.Speaker.Lines.Count == 0) return;
        var ff = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        if (!ff.IsComplete) return;
        var line = row.Speaker.Lines.OrderByDescending(l => (l.End - l.Start).Ticks).First();
        try
        {
            var samples = await AudioExtractor.ReadAsync(ff.FfmpegPath!, video.FullPath, 0, line.Start, line.End - line.Start, line.End - line.Start, null, CancellationToken.None);
            var path = Path.Combine(Path.GetTempPath(), "SubtitleStudio-speaker.wav");
            _s.AudioPreview.Stop();
            WavFile.Write(path, samples, AudioExtractor.SampleRate);
            _s.AudioPreview.Play(path);
            SpeakersStatus = $"{row.Name}, line {line.Index}: \"{Services.Subtitles.SubtitleText.StripTags(line.Text).Replace('\n', ' ')}\"";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SpeakersStatus = "Couldn't play it: " + ex.Message;
        }
    }

    /// <summary>The speaker's first longer line, in their dub voice.</summary>
    private async Task HearSpeakerVoiceAsync(SpeakerRowViewModel row)
    {
        var line = row.Speaker.Lines.OrderBy(l => l.Start).FirstOrDefault(l => (l.End - l.Start).TotalSeconds >= 1.5) ?? row.Speaker.Lines.FirstOrDefault();
        if (line is null) return;
        try
        {
            var tts = await EnsureKokoroAsync();
            var samples = await Task.Run(() => tts.Speak(line.Text, row.Speaker.Voice, (float)SelectedDubbingSpeed.Value));
            if (samples.Length == 0) return;
            var path = Path.Combine(Path.GetTempPath(), "SubtitleStudio-voice-preview.wav");
            _s.AudioPreview.Stop();
            WavFile.Write(path, samples, KokoroTts.SampleRate);
            _s.AudioPreview.Play(path);
            SpeakersStatus = $"{row.Name} in {row.Voice.Name}'s voice, line {line.Index}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            SpeakersStatus = "Couldn't play it: " + ex.Message;
        }
    }

    private void InitSpeakers()
    {
        FindSpeakersCommand = new AsyncRelayCommand(FindSpeakersAsync, () => CanFindSpeakers);
        SpeakerRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSpeakers));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectedCue)) RaiseSelectedLineSpeaker();
            // Other subtitles opened: the speakers belonged to the old ones.
            if (e.PropertyName is nameof(HasDocument) or nameof(EditorTitle) && SpeakerRows.Count > 0)
            {
                var open = new HashSet<SubtitleCue>(Cues.Select(c => c.Cue), ReferenceEqualityComparer.Instance);
                if (!SpeakerRows.Any(r => r.Speaker.Lines.Any(open.Contains)))
                {
                    SpeakerRows.Clear();
                    SpeakersStatus = string.Empty;
                }
            }
        };
    }
}
