using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>A cue and its translation, shown while translating.</summary>
public sealed record TranslatedPair(string Source, string Translation);

/// <summary>
/// Translate tab: the subtitles open in the editor into another language, with a language model on this PC.
/// The work is in Services/Translation (llama.cpp in the app); this file holds the tab's state. The
/// translation opens in the editor as a new, unsaved subtitle with the same timings.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _translateBusy;
    private double _translateProgress;
    private string _translateStatus = "Open subtitles in the Subtitles tab, choose the language, then press Translate.";
    private CancellationTokenSource? _translateCts;
    private volatile bool _translateLive;
    private IReadOnlyList<InstalledTranslationModel> _translationModels = Array.Empty<InstalledTranslationModel>();
    private TranslationCatalogEntry _translationCatalogEntry = TranslationCatalog.Entries[0];
    private string? _lastTranslateRunText;

    public IReadOnlyList<ITranslationService> TranslationEngines => _s.TranslationEngines;

    public bool HasTranslationEngines => _s.TranslationEngines.Count > 0;

    private LocalLlmTranslator? LocalTranslator => _s.TranslationEngines.OfType<LocalLlmTranslator>().FirstOrDefault();

    // ---------------- What to translate ----------------

    /// <summary>What will be translated: the subtitles open in the editor.</summary>
    public string TranslateSourceText
    {
        get
        {
            if (_doc is null || Cues.Count == 0) return "Nothing to translate yet: open a subtitle file, or transcribe a video, and it appears here.";
            var name = _doc.SourcePath is null ? "Untitled subtitle" : Path.GetFileName(_doc.SourcePath);
            var language = EditorLanguage.Length > 0 ? WhisperLanguages.NameOf(EditorLanguage) + ", " : string.Empty;
            return $"{name}  ({language}{(Cues.Count == 1 ? "1 cue" : $"{Cues.Count:N0} cues")}){(IsDirty ? ", not saved" : string.Empty)}";
        }
    }

    public bool HasTranslateSource => _doc is not null && Cues.Count > 0;

    public IReadOnlyList<TranscribeLanguageOption> TranslateSourceLanguages { get; } =
        new[] { new TranscribeLanguageOption(null, "As the subtitle says (or let the model tell)") }
            .Concat(WhisperLanguages.All.Select(l => new TranscribeLanguageOption(l.Code, l.Name))).ToList();

    public TranscribeLanguageOption SelectedTranslateSourceLanguage
    {
        get => TranslateSourceLanguages.FirstOrDefault(l => (l.Code ?? "auto") == _s.Config.TranslateSource) ?? TranslateSourceLanguages[0];
        set
        {
            var code = value?.Code ?? "auto";
            if (_s.Config.TranslateSource == code) return;
            _s.Config.TranslateSource = code;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public IReadOnlyList<TranscribeLanguageOption> TranslateTargetLanguages { get; } =
        WhisperLanguages.All.Select(l => new TranscribeLanguageOption(l.Code, l.Name)).OrderBy(l => l.Code == "en" ? 0 : 1).ThenBy(l => l.Name).ToList();

    public TranscribeLanguageOption SelectedTranslateTargetLanguage
    {
        get => TranslateTargetLanguages.FirstOrDefault(l => l.Code == _s.Config.TranslateTarget) ?? TranslateTargetLanguages[0];
        set
        {
            var code = value?.Code ?? "en";
            if (_s.Config.TranslateTarget == code) return;
            _s.Config.TranslateTarget = code;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>The source language to tell the model: the choice, else the subtitle's language tag if it's a known one.</summary>
    private string? ResolveTranslateSource()
    {
        if (SelectedTranslateSourceLanguage.Code is { } chosen) return chosen;
        var tag = EditorLanguage.Split('-', '_')[0];
        return WhisperLanguages.All.Any(l => l.Code == tag) ? tag : null;
    }

    // ---------------- Model ----------------

    public IReadOnlyList<InstalledTranslationModel> TranslationModels
    {
        get => _translationModels;
        private set
        {
            _translationModels = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTranslationModel));
            RaiseTranslationModelState();
        }
    }

    public InstalledTranslationModel? SelectedTranslationModel
    {
        get => _translationModels.FirstOrDefault(m => string.Equals(m.FileName, _s.Config.TranslateModel, StringComparison.OrdinalIgnoreCase))
               ?? _translationModels.FirstOrDefault();
        set
        {
            if (value is null || string.Equals(_s.Config.TranslateModel, value.FileName, StringComparison.OrdinalIgnoreCase)) return;
            _s.Config.TranslateModel = value.FileName;
            OnPropertyChanged();
            RaiseTranslationModelState();
            SaveSettings();
        }
    }

    public bool HasTranslationModel => _translationModels.Count > 0;

    public bool NeedsTranslationModel => _translationModels.Count == 0;

    public IReadOnlyList<TranslationCatalogEntry> TranslationCatalogEntries => TranslationCatalog.Entries;

    public TranslationCatalogEntry SelectedTranslationCatalogEntry
    {
        get => _translationCatalogEntry;
        set
        {
            if (!SetProperty(ref _translationCatalogEntry, value ?? TranslationCatalog.Entries[0])) return;
            OnPropertyChanged(nameof(DownloadTranslationModelLabel));
        }
    }

    public string DownloadTranslationModelLabel
    {
        get
        {
            long partial = _s.TranslationModels.PartialBytes(_translationCatalogEntry);
            return partial > 0 ? $"Continue download ({_translationCatalogEntry.SizeText})" : $"Download ({_translationCatalogEntry.SizeText})";
        }
    }

    /// <summary>A note about the chosen model (one the app wasn't tested with).</summary>
    public string TranslationModelNote
    {
        get
        {
            if (SelectedTranslationModel is not { } m) return string.Empty;
            if (TranslationCatalog.Entries.Any(e => e.FileName.Equals(m.FileName, StringComparison.OrdinalIgnoreCase))) return string.Empty;
            return $"Your own model ({m.Info.Architecture}{(m.Info.SizeLabel is { } s ? ", " + s : string.Empty)}). Any instruct / chat GGUF model that llama.cpp of April 2026 can load should work; how well it translates depends on the model.";
        }
    }

    public bool HasTranslationModelNote => TranslationModelNote.Length > 0;

    public string TranslationModelsFolder => _s.TranslationModels.Folder;

    public AsyncRelayCommand DownloadTranslationModelCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportTranslationModelCommand { get; private set; } = null!;
    public RelayCommand OpenTranslationModelsFolderCommand { get; private set; } = null!;

    private void RaiseTranslationModelState()
    {
        OnPropertyChanged(nameof(HasTranslationModel));
        OnPropertyChanged(nameof(NeedsTranslationModel));
        OnPropertyChanged(nameof(TranslationModelNote));
        OnPropertyChanged(nameof(HasTranslationModelNote));
        OnPropertyChanged(nameof(DownloadTranslationModelLabel));
        OnPropertyChanged(nameof(TranslateBlockedText));
        RelayCommand.Refresh();
    }

    private void RefreshTranslationModels()
    {
        try
        {
            TranslationModels = _s.TranslationModels.List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TranslationModels = Array.Empty<InstalledTranslationModel>();
            _s.Log.Warning("Translate", "The language models folder couldn't be read: " + ex.Message);
        }
    }

    private async Task DownloadTranslationModelAsync()
    {
        var entry = _translationCatalogEntry;
        await RunTranslateWorkAsync($"Downloading {entry.Title}", async ct =>
        {
            var started = DateTime.UtcNow;
            long startBytes = _s.TranslationModels.PartialBytes(entry);
            if (startBytes > 0) _s.Log.Info("Translate", $"Continuing the download of {entry.FileName} from {startBytes / (1024.0 * 1024):0} MB.");
            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                if (!_translateLive) return;
                TranslateProgress = p.Fraction * 100;
                if (p.Phase == ModelDownloadPhase.Checking)
                {
                    TranslateStatus = $"Checking the download (SHA-256)... {p.Fraction:P0}";
                    return;
                }
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                double rate = elapsed > 1 ? (p.Done - startBytes) / elapsed : 0;
                var eta = rate > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / rate) : (TimeSpan?)null;
                TranslateStatus = $"Downloading {entry.Title}... {p.Done / (1024.0 * 1024 * 1024):0.00} of {p.Total / (1024.0 * 1024 * 1024):0.00} GB"
                    + (rate > 0 ? $", {rate / (1024 * 1024):0.0} MB/s" : string.Empty)
                    + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty);
            });
            var model = await _s.TranslationModels.DownloadAsync(entry, ModelHttp, progress, ct);
            _s.Config.TranslateModel = model.FileName;
            SaveSettings();
            RefreshTranslationModels();
            TranslateStatus = $"{model.Title} downloaded and checked. Ready to translate.";
            _s.Log.Success("Translate", $"Model downloaded and checked: {model.FileName} ({model.SizeText}).");
            SetStatus($"Language model {model.Title} is ready.", StatusKind.Success);
        });
        OnPropertyChanged(nameof(DownloadTranslationModelLabel));
    }

    private async Task ImportTranslationModelAsync()
    {
        var file = _s.Dialogs.PickFile("Choose a language model (GGUF)", "Language model (*.gguf)|*.gguf|All files|*.*");
        if (file is null) return;
        await RunTranslateWorkAsync("Adding the model", async ct =>
        {
            TranslateStatus = "Checking and copying the model into the models folder...";
            var model = await _s.TranslationModels.ImportAsync(file, ct);
            _s.Config.TranslateModel = model.FileName;
            SaveSettings();
            RefreshTranslationModels();
            TranslateStatus = $"{model.Title} added. Ready to translate.";
            _s.Log.Success("Translate", $"Model added: {model.FileName} ({model.Info.Architecture}, {model.SizeText}).");
            SetStatus($"Language model {model.Title} is ready.", StatusKind.Success);
        });
    }

    // ---------------- Device (shared with AI Transcribe) ----------------

    public string TranslateDeviceText
    {
        get
        {
            var text = "The same choice as AI Transcribe (Settings). On a 12 GB graphics card Gemma 3 12B fits completely; on the processor a whole episode takes hours.";
            return _lastTranslateRunText is null ? text : text + " " + _lastTranslateRunText;
        }
    }

    // ---------------- Engine choice ----------------

    public string RememberedTranslationEngineText
    {
        get
        {
            var id = _s.Config.TranslationEngine;
            if (string.IsNullOrEmpty(id)) return "When more than one engine can translate (this PC, and online services with a saved API key), the app asks which to use.";
            var name = _s.TranslationEngines.FirstOrDefault(e => e.Id == id)?.DisplayName ?? id;
            return $"Remembered choice: {name}. The app uses it without asking while it's available.";
        }
    }

    public bool HasRememberedTranslationEngine => !string.IsNullOrEmpty(_s.Config.TranslationEngine);

    public RelayCommand ForgetTranslationEngineChoiceCommand { get; private set; } = null!;

    private ITranslationService? ChooseTranslationEngine()
    {
        var decision = EngineChooser.Decide(_s.TranslationEngines, provider => _s.ApiKeys.GetKey(provider) is not null, _s.Config.TranslationEngine);
        if (decision.Engine is { } engine) return engine;
        if (!decision.MustAsk) return null;
        var picked = _s.Dialogs.ChooseEngine("Translate", decision.Choices.Select(e => new EngineChoice(e.Id, e.DisplayName, e.Description)).ToList(), out bool remember);
        if (picked is null) return null;
        if (remember)
        {
            _s.Config.TranslationEngine = picked.Id;
            SaveSettings();
            OnPropertyChanged(nameof(RememberedTranslationEngineText));
            OnPropertyChanged(nameof(HasRememberedTranslationEngine));
            RelayCommand.Refresh();
        }
        return decision.Choices.First(e => e.Id == picked.Id);
    }

    // ---------------- Run ----------------

    public bool TranslateBusy
    {
        get => _translateBusy;
        private set
        {
            if (!SetProperty(ref _translateBusy, value)) return;
            RelayCommand.Refresh();
        }
    }

    public double TranslateProgress
    {
        get => _translateProgress;
        private set => SetProperty(ref _translateProgress, value);
    }

    public string TranslateStatus
    {
        get => _translateStatus;
        private set => SetProperty(ref _translateStatus, value);
    }

    /// <summary>The latest lines and their translations (a live preview).</summary>
    public ObservableCollection<TranslatedPair> TranslateLivePairs { get; } = new();

    public AsyncRelayCommand TranslateCommand { get; private set; } = null!;
    public RelayCommand CancelTranslateCommand { get; private set; } = null!;

    private bool CanTranslate => HasTranslateSource && !TranslateBusy && !TranscribeBusy && !BatchBusy && !ReviewBusy
                                 && (HasTranslationModel || _s.TranslationEngines.Any(e => e.RequiresApiKey));

    public string TranslateBlockedText => !HasTranslateSource ? "Open subtitles first (Subtitles tab), or transcribe a video."
        : !HasTranslationModel ? "Add a language model first (step 2)."
        : TranscribeBusy ? "Wait for AI Transcribe to finish."
        : BatchBusy ? "Wait for the Batch queue to finish." : string.Empty;

    private async Task TranslateAsync()
    {
        if (_doc is null || Cues.Count == 0) return;
        var target = SelectedTranslateTargetLanguage.Code ?? "en";
        var source = ResolveTranslateSource();
        // A "From" language that the letters plainly contradict (Assamese chosen, Korean text): the letters win.
        var written = SubtitleTranslationPrompt.ScriptLanguage(Cues.Select(c => c.Cue.Text));
        var writtenScript = SubtitleTranslationPrompt.WrittenScript(Cues.Select(c => c.Cue.Text));
        // The letters contradict "From" when "From" isn't written in them (Japanese and Chinese share characters with each other and with Korean).
        if (source is not null && writtenScript is { } script && !Scripts.Of(source).Contains(script))
        {
            if (written is not null)
            {
                _s.Log.Warning("Translate", $"\"From\" was {WhisperLanguages.NameOf(source)}, but the subtitles are written in {WhisperLanguages.NameOf(written)}: translating from {WhisperLanguages.NameOf(written)}.");
                SetStatus($"\"From\" says {WhisperLanguages.NameOf(source)}, but the text is {WhisperLanguages.NameOf(written)}: translating from {WhisperLanguages.NameOf(written)}.", StatusKind.Warning);
                source = written;
            }
            else
            {
                // Cyrillic, Arabic or Devanagari: several languages use them, so the model is told to translate from the language the text is in.
                _s.Log.Warning("Translate", $"\"From\" was {WhisperLanguages.NameOf(source)}, but the subtitles are written in {script} letters: translating from the language they are in.");
                SetStatus($"\"From\" says {WhisperLanguages.NameOf(source)}, but the text is in {script} letters: translating from the language it is in.", StatusKind.Warning);
                source = null;
            }
        }
        else if (source is null && written is not null)
            source = written;
        if (source is not null && source == target)
        {
            SetStatus($"The subtitles are already in {WhisperLanguages.NameOf(target)}: choose another language to translate into.", StatusKind.Warning);
            return;
        }
        // The translation replaces the original in the editor: offer to save an unsaved original first.
        if (IsDirty)
        {
            if (_s.Dialogs.Confirm("Keep the original?", "The subtitles in the editor aren't saved. The translation will replace them in the editor.\n\nSave them first? (Yes: choose where to save, then translate. No: translate without saving them.)"))
            {
                SaveAs();
                if (IsDirty) return; // save cancelled or failed
            }
        }

        var engine = ChooseTranslationEngine();
        if (engine is null)
        {
            if (!_s.TranslationEngines.Any()) SetStatus("No translation engine is available in this build.", StatusKind.Error);
            return;
        }
        if (engine is LocalLlmTranslator local)
        {
            if (SelectedTranslationModel is not { } model)
            {
                SetStatus("Add a language model first: download one in step 2 of Translate.", StatusKind.Warning);
                return;
            }
            local.ModelPath = model.Path;
            local.Names = CurrentNames.Entries.Select(e => e.Name).ToList();
            local.Device = ResolveWhisperDevice();
            // One model in graphics memory at a time: Whisper's goes before the language model loads.
            LocalWhisper?.ReleaseModel();
        }

        SyncDocumentFromRows();
        var original = _doc;
        var video = PairedVideo;
        var cues = original.Cues.Select(c => c.Clone()).ToList();
        await RunTranslateWorkAsync("Translating", async ct =>
        {
            TranslateLivePairs.Clear();
            var started = DateTime.UtcNow;
            var progress = new Progress<EngineProgress>(p =>
            {
                if (!_translateLive) return;
                TranslateProgress = p.Fraction * 100;
                var elapsed = DateTime.UtcNow - started;
                var eta = p.Fraction > 0.1 && elapsed.TotalSeconds > 5
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction) : (TimeSpan?)null;
                TranslateStatus = p.Message + (eta is { } e ? $", about {FormatEta(e)} left" : string.Empty);
                if (p.LatestText is { Length: > 0 } text && text.Split("  →  ") is [var src, var tgt])
                {
                    TranslateLivePairs.Add(new TranslatedPair(src, tgt));
                    while (TranslateLivePairs.Count > 8) TranslateLivePairs.RemoveAt(0);
                }
            });

            var translated = await engine.TranslateAsync(cues, source, target, progress, ct);
            if (engine is LocalLlmTranslator { LastStats: { } st })
            {
                _lastTranslateRunText = $"Last run: {st.Device}, {st.CuesPerMinute:0} cues a minute.";
                OnPropertyChanged(nameof(TranslateDeviceText));
                if (st.DeviceProblem is { } problem) SetStatus(problem, StatusKind.Warning);
            }

            // Names: the show's list, and near-identical spellings made to match.
            var translatedCues = translated.ToList();
            ApplyNames(translatedCues);
            translated = translatedCues;

            var doc = new SubtitleDocument
            {
                Language = target,
                Format = original.Format,
                FormatHeader = original.FormatHeader,
                FormatTrailer = original.FormatTrailer,
                FrameRate = original.FrameRate,
                Forced = original.Forced,
                HearingImpaired = original.HearingImpaired,
                Sdh = original.Sdh,
            };
            doc.Cues.AddRange(translated);
            if (original.SourcePath is { } path) _standaloneBaseName = StripLanguage(Path.GetFileNameWithoutExtension(path));
            LoadDocument(doc, video, selectIndex: 0);
            IsDirty = true;
            // The original goes next to the translation for reviewing.
            SetReviewOriginal(original.Cues, original.SourcePath is { } from ? Path.GetFileName(from) : "the subtitles before translating", source);
            var name = WhisperLanguages.NameOf(target);
            var failed = (engine as LocalLlmTranslator)?.LastStats?.Failed ?? 0;
            TranslateStatus = $"Done: {translated.Count} cues in {name}. They are open in the Subtitles tab, unsaved: review and save."
                + (failed > 0 ? $" {failed} couldn't be translated and kept the original text (see the Log)." : string.Empty)
                + (NameSuggestions.Count > 0 ? $" {NameSuggestions.Count} group{(NameSuggestions.Count == 1 ? "" : "s")} of look-alike names to check below." : string.Empty);
            SetStatus($"Translated {translated.Count} cues into {name}. Review them in the Subtitles tab (the original is shown beside each line), then save."
                      + (ReviewCount > 0 ? $" {ReviewCount} line{(ReviewCount == 1 ? "" : "s")} flagged to check." : string.Empty), failed > 0 || ReviewCount > 0 ? StatusKind.Warning : StatusKind.Success);
        });
    }

    /// <summary>"Film.ko.forced" → "Film" (the translation gets its own language in the suggested name).</summary>
    private static string StripLanguage(string baseName)
    {
        var parts = baseName.Split('.').ToList();
        while (parts.Count > 1 && (parts[^1].Length is 2 or 3 && WhisperLanguages.All.Any(l => l.Code.Equals(parts[^1], StringComparison.OrdinalIgnoreCase))
                                   || parts[^1] is "forced" or "hi" or "sdh" or "cc"))
            parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }

    private async Task RunTranslateWorkAsync(string what, Func<CancellationToken, Task> work)
    {
        _translateCts = new CancellationTokenSource();
        TranslateBusy = true;
        TranslateProgress = 0;
        TranslateStatus = what + "...";
        try
        {
            _translateLive = true;
            try
            {
                await work(_translateCts.Token);
            }
            finally
            {
                _translateLive = false;
            }
        }
        catch (OperationCanceledException)
        {
            TranslateStatus = "Cancelled.";
            SetStatus(what + " cancelled.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Net.Http.HttpRequestException or LLama.Exceptions.RuntimeError)
        {
            TranslateStatus = ex.Message;
            SetStatus($"{what} failed: {ex.Message}", StatusKind.Error);
            _s.Log.Error("Translate", $"{what} failed: {ex}");
        }
        finally
        {
            TranslateBusy = false;
            _translateCts.Dispose();
            _translateCts = null;
            OnPropertyChanged(nameof(TranslateBlockedText));
        }
    }

    private void RaiseTranslateSource()
    {
        RaiseNames();
        OnPropertyChanged(nameof(TranslateSourceText));
        OnPropertyChanged(nameof(HasTranslateSource));
        OnPropertyChanged(nameof(TranslateBlockedText));
        RelayCommand.Refresh();
    }

    private void InitTranslate()
    {
        _translationCatalogEntry = TranslationCatalog.Entries.First(e => e.Recommended);
        RefreshTranslationModels();
        InitTranslateNames();

        TranslateCommand = new AsyncRelayCommand(TranslateAsync, () => CanTranslate);
        CancelTranslateCommand = new RelayCommand(() => _translateCts?.Cancel(), () => TranslateBusy);
        DownloadTranslationModelCommand = new AsyncRelayCommand(DownloadTranslationModelAsync, () => !TranslateBusy);
        ImportTranslationModelCommand = new AsyncRelayCommand(ImportTranslationModelAsync, () => !TranslateBusy);
        OpenTranslationModelsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(_s.TranslationModels.Folder);
            _s.Dialogs.OpenFolder(_s.TranslationModels.Folder);
        });
        ForgetTranslationEngineChoiceCommand = new RelayCommand(() =>
        {
            _s.Config.TranslationEngine = string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(RememberedTranslationEngineText));
            OnPropertyChanged(nameof(HasRememberedTranslationEngine));
            RelayCommand.Refresh();
        }, () => HasRememberedTranslationEngine);

        Cues.CollectionChanged += (_, _) => RaiseTranslateSource();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HasDocument) or nameof(EditorLanguage) or nameof(EditorTitle) or nameof(PairedVideo)) RaiseTranslateSource();
            else if (e.PropertyName is nameof(TranscribeBusy)) OnPropertyChanged(nameof(TranslateBlockedText));
            else if (e.PropertyName is nameof(TranslateBusy)) OnPropertyChanged(nameof(TranscribeBlockedText));
        };
    }
}
