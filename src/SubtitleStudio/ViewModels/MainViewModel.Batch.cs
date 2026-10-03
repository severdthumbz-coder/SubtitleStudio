using System.Collections;
using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Batch;
using SubtitleStudio.Services.Subtitles;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Batch tab (season queue): many videos or audio files transcribed, and translated, one after another,
/// with the subtitles saved next to each file. The work is in Services/Batch/SeasonQueue; this file holds
/// the tab's state. It shares the language, models and device with AI Transcribe and Translate.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _batchBusy;
    private double _batchProgress;
    private string _batchStatus = "Add the episodes (files or a folder), check the options, then press Start.";
    private CancellationTokenSource? _batchCts;
    private volatile bool _batchLive;
    private IReadOnlyList<string> _batchTranslatedFiles = Array.Empty<string>();
    private string _batchNamesShow = string.Empty;
    private string _batchNameReport = string.Empty;

    public ObservableCollection<QueueRowViewModel> BatchRows { get; } = new();

    public bool HasBatchRows => BatchRows.Count > 0;

    public string BatchSummary
    {
        get
        {
            if (BatchRows.Count == 0) return "No files yet";
            int included = BatchRows.Count(r => r.Include);
            var text = BatchRows.Count == 1 ? "1 file" : $"{BatchRows.Count} files";
            if (included != BatchRows.Count) text += $", {included} included";
            var known = BatchRows.Where(r => r.Include && r.Item.Duration is not null).Sum(r => r.Item.Duration!.Value.TotalSeconds);
            if (known > 0) text += $", {MediaItem.FormatDuration(TimeSpan.FromSeconds(known))} in all";
            return text;
        }
    }

    // ---------------- Options ----------------

    /// <summary>"Don't translate", then the languages (English first).</summary>
    public IReadOnlyList<TranscribeLanguageOption> BatchTargetLanguages { get; } =
        new[] { new TranscribeLanguageOption(null, "Don't translate (transcribe only)") }
            .Concat(WhisperLanguages.All.Select(l => new TranscribeLanguageOption(l.Code, l.Name)).OrderBy(l => l.Code == "en" ? 0 : 1).ThenBy(l => l.Name)).ToList();

    public TranscribeLanguageOption SelectedBatchTarget
    {
        get => BatchTargetLanguages.FirstOrDefault(l => (l.Code ?? "none") == _s.Config.BatchTranslateTo) ?? BatchTargetLanguages[1];
        set
        {
            var code = value?.Code ?? "none";
            if (_s.Config.BatchTranslateTo == code) return;
            _s.Config.BatchTranslateTo = code;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatchPlanText));
            OnPropertyChanged(nameof(BatchBlockedText));
            SaveSettings();
            RelayCommand.Refresh();
        }
    }

    private string? BatchTarget => SelectedBatchTarget.Code;

    public bool BatchRedo
    {
        get => _s.Config.BatchRedo;
        set
        {
            if (_s.Config.BatchRedo == value) return;
            _s.Config.BatchRedo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatchPlanText));
            SaveSettings();
        }
    }

    /// <summary>What Start will do, in a sentence.</summary>
    public string BatchPlanText
    {
        get
        {
            var spoken = SelectedTranscribeLanguage.Code is { } code ? WhisperLanguages.NameOf(code) : "the language detected in each file";
            var ext = (_s.SubtitleFormats.ById(_s.Config.DefaultOutputFormat)?.Extensions.FirstOrDefault() ?? ".srt").TrimStart('.');
            var spokenTag = SelectedTranscribeLanguage.Code ?? "(language)";
            var text = $"Each file is transcribed ({spoken}) to \"Name.{spokenTag}.{ext}\"";
            if (BatchTarget is { } target) text += $", then translated into {WhisperLanguages.NameOf(target)} as \"Name.{target}.{ext}\"";
            text += ", saved next to it. All files are transcribed first and then all translated, so each model loads once.";
            text += BatchRedo
                ? " Subtitles already there are made again (and overwritten)."
                : " A file that already has them is not done again, so a stopped queue carries on where it ended.";
            return text;
        }
    }

    // ---------------- Names (the show's list, shared with Translate) ----------------

    /// <summary>The show the queued files belong to, when they all belong to one.</summary>
    public string BatchShowName
    {
        get
        {
            var shows = BatchRows.Where(r => r.Include).Select(r => NameConsistency.ShowOf(r.FullPath)).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            return shows.Count == 1 ? shows[0] : string.Empty;
        }
    }

    public bool HasBatchShow => BatchShowName.Length > 0;

    public string BatchNamesHeader => HasBatchShow ? $"Names in {BatchShowName}" : "Names";

    public string BatchNamesText
    {
        get => HasBatchShow && _s.Config.TranslateNames.TryGetValue(BatchShowName.ToLowerInvariant(), out var text) ? text : string.Empty;
        set
        {
            if (!HasBatchShow) return;
            var text = (value ?? string.Empty).Trim();
            if (text == BatchNamesText) return;
            var key = BatchShowName.ToLowerInvariant();
            if (text.Length == 0) _s.Config.TranslateNames.Remove(key);
            else _s.Config.TranslateNames[key] = text;
            SaveSettings();
            OnPropertyChanged();
            RaiseNames();
        }
    }

    public string BatchNamesNote => HasBatchShow
        ? "The same list as in the Translate tab: given to the language model for every episode, and always spelled this way."
          + (NameMatching ? " Matching (Settings) is on." : " Matching (Settings) is off.")
        : BatchRows.Count == 0 ? "Add files to see the show's names list."
        : "The files belong to more than one show: each one uses its own show's list (edit them in the Translate tab).";

    public ObservableCollection<NameSuggestionRow> BatchNameSuggestions { get; } = new();

    public bool HasBatchNameSuggestions => BatchNameSuggestions.Count > 0;

    public string BatchNameReport
    {
        get => _batchNameReport;
        private set
        {
            if (SetProperty(ref _batchNameReport, value)) OnPropertyChanged(nameof(HasBatchNameReport));
        }
    }

    public bool HasBatchNameReport => _batchNameReport.Length > 0;

    private NameList NamesOfShow(string show)
        => _s.Config.TranslateNames.TryGetValue(show.ToLowerInvariant(), out var text) ? NameList.Parse(text) : NameList.Parse(string.Empty);

    private void RaiseBatchShow()
    {
        OnPropertyChanged(nameof(BatchShowName));
        OnPropertyChanged(nameof(HasBatchShow));
        OnPropertyChanged(nameof(BatchNamesHeader));
        OnPropertyChanged(nameof(BatchNamesText));
        OnPropertyChanged(nameof(BatchNamesNote));
    }

    // ---------------- Files ----------------

    public AsyncRelayCommand BatchAddFilesCommand { get; private set; } = null!;
    public AsyncRelayCommand BatchAddFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand BatchDropCommand { get; private set; } = null!;
    public RelayCommand BatchAddListedCommand { get; private set; } = null!;
    public RelayCommand BatchRemoveCommand { get; private set; } = null!;
    public RelayCommand BatchClearCommand { get; private set; } = null!;
    public RelayCommand BatchOpenResultCommand { get; private set; } = null!;
    public RelayCommand BatchRevealCommand { get; private set; } = null!;

    private Task BatchAddFilesAsync()
    {
        var picked = _s.Dialogs.PickFiles("Add videos or audio files to the queue", FileImportService.OpenFileFilter);
        return picked.Count == 0 ? Task.CompletedTask : BatchAddAsync(picked);
    }

    private Task BatchAddFolderAsync()
    {
        var folder = _s.Dialogs.PickFolder(IncludeSubfolders ? "Add a folder to the queue (including subfolders)" : "Add a folder to the queue");
        return folder is null ? Task.CompletedTask : BatchAddAsync(new[] { folder });
    }

    /// <summary>Adds videos and audio files (subtitle files are left out), sorted by name so episodes go in order.</summary>
    private async Task BatchAddAsync(IReadOnlyList<string> paths)
    {
        if (BatchBusy) return;
        SetStatus("Scanning for videos and audio...");
        var scan = await _s.Import.ScanAsync(paths, IncludeSubfolders, null, CancellationToken.None);
        var listed = BatchRows.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<MediaItem>();
        foreach (var f in scan.Files.Where(f => f.Kind is MediaKind.Video or MediaKind.Audio)
                     .OrderBy(f => Path.GetDirectoryName(f.Path), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => Path.GetFileName(f.Path), NaturalStringComparer.Instance))
        {
            if (!listed.Add(f.Path)) continue;
            var known = Files.FirstOrDefault(m => string.Equals(m.FullPath, f.Path, StringComparison.OrdinalIgnoreCase));
            added.Add(known ?? new MediaItem(f.Path, f.Kind, f.SizeBytes, f.Sidecars));
        }
        AddBatchItems(added);
        int subtitles = scan.Files.Count(f => f.Kind == MediaKind.Subtitle);
        SetStatus(added.Count == 0
                ? "Nothing new to add: no videos or audio files found" + (subtitles > 0 ? " (subtitle files aren't queued)." : ".")
                : $"Queued {added.Count} file{(added.Count == 1 ? "" : "s")}.",
            added.Count > 0 ? StatusKind.Success : StatusKind.Warning);
        await ProbeDurationsAsync(added.Where(i => i.Duration is null).ToList(), _probeCts.Token);
        OnPropertyChanged(nameof(BatchSummary));
    }

    private void AddBatchListed()
    {
        var listed = BatchRows.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = TranscribableFiles.Where(f => listed.Add(f.FullPath)).ToList();
        AddBatchItems(added);
        SetStatus(added.Count == 0 ? "The videos and audio files in Source / Files are already queued." : $"Queued {added.Count} file{(added.Count == 1 ? "" : "s")} from Source / Files.",
            added.Count > 0 ? StatusKind.Success : StatusKind.Info);
    }

    private void AddBatchItems(IEnumerable<MediaItem> items)
    {
        foreach (var item in items)
        {
            var row = new QueueRowViewModel(item) { IncludeChanged = OnBatchIncludeChanged };
            row.Status = ExistingText(item.FullPath);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MediaItem.Duration)) OnPropertyChanged(nameof(BatchSummary));
            };
            BatchRows.Add(row);
        }
    }

    /// <summary>What a queued file already has, so it's clear what Start will skip.</summary>
    private string ExistingText(string file)
    {
        if (BatchRedo) return "Waiting";
        var spoken = SelectedTranscribeLanguage.Code;
        var target = BatchTarget;
        var transcript = SeasonQueue.FindTranscript(file, spoken, target);
        var translation = target is null ? null : SeasonQueue.FindLanguage(file, target);
        if (translation is not null) return $"Already has {WhisperLanguages.NameOf(target!)} subtitles: will be left alone";
        if (transcript is not null) return $"Has a transcript ({Path.GetFileName(transcript)}): " + (target is null ? "nothing to do" : "will only be translated");
        return "Waiting";
    }

    private void RefreshBatchExisting()
    {
        if (BatchBusy) return;
        foreach (var row in BatchRows.Where(r => r.Step == QueueStep.Waiting)) row.Status = ExistingText(row.FullPath);
    }

    private void OnBatchIncludeChanged()
    {
        OnPropertyChanged(nameof(BatchSummary));
        RaiseBatchShow();
        RelayCommand.Refresh();
    }

    private void RemoveBatchRows(IList? selection)
    {
        if (selection is null || BatchBusy) return;
        foreach (var row in selection.OfType<QueueRowViewModel>().ToList()) BatchRows.Remove(row);
    }

    private void OpenBatchResult(object? parameter)
    {
        if (parameter is not QueueRowViewModel { ResultPath: { } path } || !File.Exists(path)) return;
        OpenSubtitle(path);
    }

    // ---------------- Run ----------------

    public bool BatchBusy
    {
        get => _batchBusy;
        private set
        {
            if (!SetProperty(ref _batchBusy, value)) return;
            OnPropertyChanged(nameof(TranscribeBlockedText));
            OnPropertyChanged(nameof(TranslateBlockedText));
            OnPropertyChanged(nameof(BatchBlockedText));
            OnPropertyChanged(nameof(BatchCanEdit));
            RelayCommand.Refresh();
        }
    }

    /// <summary>The queue can be changed (not while it runs).</summary>
    public bool BatchCanEdit => !_batchBusy;

    public double BatchProgress
    {
        get => _batchProgress;
        private set => SetProperty(ref _batchProgress, value);
    }

    public string BatchStatus
    {
        get => _batchStatus;
        private set => SetProperty(ref _batchStatus, value);
    }

    public AsyncRelayCommand StartBatchCommand { get; private set; } = null!;
    public RelayCommand CancelBatchCommand { get; private set; } = null!;
    public RelayCommand BatchApplyNamesCommand { get; private set; } = null!;

    private bool CanStartBatch => BatchRows.Any(r => r.Include) && !BatchBusy && !TranscribeBusy && !TranslateBusy && FfmpegStatus.HasFfmpeg
                                  && (HasWhisperModel || _s.TranscriptionEngines.Any(e => e.RequiresApiKey))
                                  && (BatchTarget is null || HasTranslationModel || _s.TranslationEngines.Any(e => e.RequiresApiKey));

    public string BatchBlockedText => !FfmpegStatus.HasFfmpeg ? "FFmpeg is needed to read the audio: set it in Settings > Engines and tools."
        : !HasWhisperModel ? "Add a Whisper model first (AI Transcribe, step 2)."
        : BatchTarget is not null && !HasTranslationModel ? "Add a language model first (Translate, step 2), or choose \"Don't translate\"."
        : BatchRows.Count == 0 ? "Add files first."
        : !BatchRows.Any(r => r.Include) ? "Tick at least one file."
        : TranscribeBusy ? "Wait for AI Transcribe to finish."
        : TranslateBusy ? "Wait for Translate to finish." : string.Empty;

    private async Task StartBatchAsync()
    {
        var rows = BatchRows.Where(r => r.Include).ToList();
        if (rows.Count == 0) return;
        var target = BatchTarget;

        var transcriber = ChooseTranscriptionEngine();
        if (transcriber is null)
        {
            if (!_s.TranscriptionEngines.Any()) SetStatus("No transcription engine is available in this build.", StatusKind.Error);
            return;
        }
        string? whisperModel = null;
        if (transcriber is LocalWhisperEngine whisper)
        {
            if (SelectedWhisperModel is not { } model)
            {
                SetStatus("Add a Whisper model first: download one in step 2 of AI Transcribe.", StatusKind.Warning);
                return;
            }
            whisperModel = model.Path;
            whisper.Device = ResolveWhisperDevice();
        }

        ITranslationService? translator = null;
        if (target is not null)
        {
            translator = ChooseTranslationEngine();
            if (translator is null) return;
            if (translator is LocalLlmTranslator local)
            {
                if (SelectedTranslationModel is not { } model)
                {
                    SetStatus("Add a language model first: download one in step 2 of Translate.", StatusKind.Warning);
                    return;
                }
                local.ModelPath = model.Path;
                local.Device = ResolveWhisperDevice();
            }
        }
        // One model in graphics memory at a time: the language model goes while Whisper works.
        LocalTranslator?.Runner.Release();

        foreach (var row in BatchRows) row.Reset();
        BatchNameSuggestions.Clear();
        OnPropertyChanged(nameof(HasBatchNameSuggestions));
        BatchNameReport = string.Empty;
        _batchTranslatedFiles = Array.Empty<string>();

        // The names lists as they are now (the run reads them on another thread).
        var namesNow = _s.Config.TranslateNames.ToDictionary(kv => kv.Key, kv => NameList.Parse(kv.Value), StringComparer.OrdinalIgnoreCase);
        NameList NamesAtStart(string show) => namesNow.TryGetValue(show, out var list) ? list : NameList.Parse(string.Empty);
        var matching = NameMatching;

        var options = new QueueOptions(SelectedTranscribeLanguage.Code, whisperModel, target, _s.Config.DefaultOutputFormat, BatchRedo);
        var queue = new SeasonQueue(_s.SubtitleFormats, _s.Log);
        _batchCts = new CancellationTokenSource();
        BatchBusy = true;
        BatchProgress = 0;
        BatchStatus = "Starting...";
        var started = DateTime.UtcNow;
        var fractions = new double[rows.Count];
        try
        {
            _batchLive = true;
            var progress = new Progress<QueueUpdate>(u =>
            {
                if (!_batchLive || u.Index >= rows.Count) return;
                var row = rows[u.Index];
                row.Step = u.Step;
                row.Status = u.Text;
                row.Progress = u.Fraction * 100;
                if (u.TranscriptPath is not null) row.TranscriptPath = u.TranscriptPath;
                if (u.TranslationPath is not null) row.TranslationPath = u.TranslationPath;
                fractions[u.Index] = u.Step is QueueStep.Failed or QueueStep.Skipped or QueueStep.Done ? 1 : u.Fraction;
                var overall = fractions.Average();
                BatchProgress = overall * 100;
                var elapsed = DateTime.UtcNow - started;
                var eta = overall > 0.05 && elapsed.TotalSeconds > 20 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - overall) / overall) : (TimeSpan?)null;
                var phase = u.Step == QueueStep.Translating ? "Translating" : u.Step == QueueStep.Transcribing ? "Transcribing" : null;
                if (phase is not null)
                    BatchStatus = $"{phase} {u.Index + 1} of {rows.Count}: {row.Name}" + (eta is { } e ? $". About {FormatSpan(e)} left in all." : ".");
            });
            var result = await Task.Run(() => queue.RunAsync(rows.Select(r => r.FullPath).ToList(), rows.Select(r => r.Item.Duration).ToList(), options,
                transcriber, translator, NamesAtStart, matching, () => LocalWhisper?.ReleaseModel(), progress, _batchCts.Token));
            _batchLive = false;

            foreach (var row in rows.Where(r => r.Step is QueueStep.Waiting or QueueStep.Transcribing or QueueStep.Translating))
            {
                row.Step = result.Cancelled ? QueueStep.Cancelled : row.Step;
                if (result.Cancelled) row.Status = "Not done (stopped)";
            }
            _batchTranslatedFiles = result.TranslatedFiles;
            ShowBatchSuggestions(result, rows);

            var parts = new List<string>();
            if (result.Transcribed > 0) parts.Add($"{result.Transcribed} transcribed");
            if (result.Reused > 0) parts.Add($"{result.Reused} transcript{(result.Reused == 1 ? "" : "s")} already there");
            if (result.Translated > 0) parts.Add($"{result.Translated} translated");
            if (result.Skipped > 0) parts.Add($"{result.Skipped} skipped");
            if (result.Failed > 0) parts.Add($"{result.Failed} failed (see the Log)");
            var summary = parts.Count == 0 ? "nothing done" : string.Join(", ", parts);
            BatchStatus = (result.Cancelled ? "Stopped: " : "Finished: ") + summary + $" in {FormatSpan(DateTime.UtcNow - started)}."
                          + (result.NameChanges > 0 ? $" Names made consistent in {result.NameChanges} cues." : string.Empty);
            BatchProgress = result.Cancelled ? BatchProgress : 100;
            SetStatus("Batch " + BatchStatus.ToLowerInvariant(), result.Failed > 0 || result.Cancelled ? StatusKind.Warning : StatusKind.Success);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            BatchStatus = ex.Message;
            SetStatus("Batch failed: " + ex.Message, StatusKind.Error);
            _s.Log.Error("Batch", "The queue stopped: " + ex);
        }
        finally
        {
            _batchLive = false;
            BatchBusy = false;
            _batchCts.Dispose();
            _batchCts = null;
            foreach (var folder in rows.Select(r => r.Item.Folder).Distinct(StringComparer.OrdinalIgnoreCase)) RefreshSidecarsIn(folder);
            foreach (var row in rows) RefreshItemSidecars(row.Item);
        }
    }

    private static string FormatSpan(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(1, (int)t.TotalSeconds)} s";

    private static void RefreshItemSidecars(MediaItem item)
    {
        try
        {
            item.Sidecars = SidecarDetector.Detect(item.FullPath, Directory.GetFiles(item.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder went away; the list shows what it had.
        }
    }

    private void ShowBatchSuggestions(QueueResult result, IReadOnlyList<QueueRowViewModel> rows)
    {
        BatchNameSuggestions.Clear();
        _batchNamesShow = rows.Select(r => NameConsistency.ShowOf(r.FullPath)).FirstOrDefault(s => s.Length > 0) ?? string.Empty;
        foreach (var g in result.NameSuggestions)
        {
            _s.Log.Detail("Names", $"Across the queue, look-alike spellings, maybe one person: {g.Label}.");
            var entry = g.AsEntry();
            NameSuggestionRow? row = null;
            row = new NameSuggestionRow(g.Label, entry, new RelayCommand(() =>
            {
                var key = _batchNamesShow.ToLowerInvariant();
                var current = _s.Config.TranslateNames.TryGetValue(key, out var text) ? text : string.Empty;
                _s.Config.TranslateNames[key] = NameList.Add(current, entry);
                SaveSettings();
                RaiseBatchShow();
                RaiseNames();
                if (row is not null) BatchNameSuggestions.Remove(row);
                OnPropertyChanged(nameof(HasBatchNameSuggestions));
                SetStatus($"Added \"{NameList.Format(entry)}\" to the names list of {_batchNamesShow}. \"Apply names to the translated files\" uses it on them.", StatusKind.Success);
            }));
            BatchNameSuggestions.Add(row);
        }
        OnPropertyChanged(nameof(HasBatchNameSuggestions));
        if (result.NameSuggestions.Count > 0)
            BatchNameReport = $"Across all {result.Translated} translations, {result.NameSuggestions.Count} group{(result.NameSuggestions.Count == 1 ? "" : "s")} of look-alike names may be one person each: add the right ones to the list, then apply names to the translated files.";
    }

    private void ApplyBatchNames()
    {
        var files = _batchTranslatedFiles.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        try
        {
            var changed = new SeasonQueue(_s.SubtitleFormats, _s.Log).ApplyNames(files, NamesOfShow, NameMatching);
            BatchNameReport = changed == 0
                ? "Names: nothing to change in the translated files."
                : $"Names made consistent in {changed} cue{(changed == 1 ? "" : "s")} across {files.Count} translated file{(files.Count == 1 ? "" : "s")} (saved; the Log lists them).";
            SetStatus(BatchNameReport, changed > 0 ? StatusKind.Success : StatusKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SubtitleFormatException)
        {
            SetStatus("Names couldn't be applied: " + ex.Message, StatusKind.Error);
            _s.Log.Error("Names", "Applying names to the translated files failed: " + ex);
        }
    }

    private void InitBatch()
    {
        BatchAddFilesCommand = new AsyncRelayCommand(BatchAddFilesAsync, () => !BatchBusy);
        BatchAddFolderCommand = new AsyncRelayCommand(BatchAddFolderAsync, () => !BatchBusy);
        BatchDropCommand = new AsyncRelayCommand(p => p is string[] paths ? BatchAddAsync(paths) : Task.CompletedTask, _ => !BatchBusy);
        BatchAddListedCommand = new RelayCommand(AddBatchListed, () => !BatchBusy && TranscribableFiles.Count > 0);
        BatchRemoveCommand = new RelayCommand(p => RemoveBatchRows(p as IList), p => !BatchBusy && p is IList { Count: > 0 });
        BatchClearCommand = new RelayCommand(() => BatchRows.Clear(), () => !BatchBusy && BatchRows.Count > 0);
        BatchOpenResultCommand = new RelayCommand(OpenBatchResult, p => p is QueueRowViewModel { HasResult: true });
        BatchRevealCommand = new RelayCommand(p => { if (p is QueueRowViewModel r) _s.Dialogs.RevealInExplorer(r.ResultPath ?? r.FullPath); }, p => p is QueueRowViewModel);
        StartBatchCommand = new AsyncRelayCommand(StartBatchAsync, () => CanStartBatch);
        CancelBatchCommand = new RelayCommand(() => _batchCts?.Cancel(), () => BatchBusy);
        BatchApplyNamesCommand = new RelayCommand(ApplyBatchNames, () => !BatchBusy && _batchTranslatedFiles.Count > 0);

        BatchRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBatchRows));
            OnPropertyChanged(nameof(BatchSummary));
            OnPropertyChanged(nameof(BatchBlockedText));
            RaiseBatchShow();
            RelayCommand.Refresh();
        };
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SelectedTranscribeLanguage):
                    OnPropertyChanged(nameof(BatchPlanText));
                    RefreshBatchExisting();
                    break;
                case nameof(SelectedBatchTarget) or nameof(BatchRedo):
                    RefreshBatchExisting();
                    break;
                case nameof(TranscribeBusy) or nameof(TranslateBusy) or nameof(HasWhisperModel) or nameof(HasTranslationModel):
                    OnPropertyChanged(nameof(BatchBlockedText));
                    break;
                case nameof(NameMatching):
                    OnPropertyChanged(nameof(BatchNamesNote));
                    break;
            }
        };
    }
}
