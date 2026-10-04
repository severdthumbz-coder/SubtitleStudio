using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>A file that could be the original of the open translation.</summary>
public sealed record ReviewCandidate(string Path, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Another way to translate the selected line, with a button that uses it.</summary>
public sealed record SuggestionRow(string Text, RelayCommand UseCommand);

/// <summary>
/// Subtitles tab, review: a translation next to its original. The original goes in its own column, the
/// lines worth checking are flagged, and for the selected line the language model suggests other
/// translations (with an optional note). The checks are in Services/Translation/TranslationReview; the
/// suggestions in LocalLlmTranslator.SuggestAsync.
/// </summary>
public sealed partial class MainViewModel
{
    private List<SubtitleCue>? _reviewOriginal;
    private string? _reviewOriginalName;
    private string? _reviewOriginalLanguage;
    private ReviewCandidate? _selectedReviewCandidate;
    private IReadOnlyList<ReviewCandidate> _reviewCandidates = Array.Empty<ReviewCandidate>();
    private int _reviewCount;
    private string _reviewHint = string.Empty;
    private bool _reviewBusy;
    private string _suggestStatus = string.Empty;
    private CancellationTokenSource? _suggestCts;

    public bool HasReviewOriginal => _reviewOriginal is not null;

    /// <summary>"Original: Episode 1.ko.srt (Korean), 516 lines."</summary>
    public string ReviewOriginalText => _reviewOriginal is null ? string.Empty
        : $"Original: {_reviewOriginalName}" + (_reviewOriginalLanguage is { } l ? $" ({WhisperLanguages.NameOf(l)})" : string.Empty) + $", {_reviewOriginal.Count} lines.";

    public int ReviewCount
    {
        get => _reviewCount;
        private set
        {
            if (SetProperty(ref _reviewCount, value)) OnPropertyChanged(nameof(ReviewSummary));
        }
    }

    public string ReviewSummary => _reviewOriginal is null ? string.Empty
        : _reviewCount == 0 ? "Nothing flagged: no leftover original script, untranslated, repeated or empty lines."
        : $"{_reviewCount} line{(_reviewCount == 1 ? "" : "s")} to check (leftover original script, untranslated, empty, repeated, much longer than the original, or a question that lost its question mark).";

    /// <summary>Other-language subtitle files of the same video (or name) that could be the original.</summary>
    public IReadOnlyList<ReviewCandidate> ReviewCandidates
    {
        get => _reviewCandidates;
        private set
        {
            _reviewCandidates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReviewCandidates));
            SelectedReviewCandidate = value.FirstOrDefault();
        }
    }

    public bool HasReviewCandidates => _reviewCandidates.Count > 0;

    public ReviewCandidate? SelectedReviewCandidate
    {
        get => _selectedReviewCandidate;
        set
        {
            if (SetProperty(ref _selectedReviewCandidate, value)) RelayCommand.Refresh();
        }
    }

    // ---------------- The selected line ----------------

    public string? SelectedOriginal => SelectedCue?.SourceText;

    public bool HasSelectedOriginal => HasReviewOriginal && SelectedCue?.SourceText is not null;

    /// <summary>A note for the model ("Han is a surname; 선생님 is Doctor here"), kept while moving between lines.</summary>
    public string ReviewHint
    {
        get => _reviewHint;
        set => SetProperty(ref _reviewHint, value ?? string.Empty);
    }

    public ObservableCollection<SuggestionRow> Suggestions { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool ReviewBusy
    {
        get => _reviewBusy;
        private set
        {
            if (!SetProperty(ref _reviewBusy, value)) return;
            OnPropertyChanged(nameof(TranslateBlockedText));
            RelayCommand.Refresh();
        }
    }

    public string SuggestStatus
    {
        get => _suggestStatus;
        private set => SetProperty(ref _suggestStatus, value);
    }

    public RelayCommand CompareWithCandidateCommand { get; private set; } = null!;
    public RelayCommand CompareWithFileCommand { get; private set; } = null!;
    public RelayCommand HideOriginalCommand { get; private set; } = null!;
    public RelayCommand NextToCheckCommand { get; private set; } = null!;
    public AsyncRelayCommand SuggestCommand { get; private set; } = null!;
    public RelayCommand CancelSuggestCommand { get; private set; } = null!;

    private bool CanSuggest => HasSelectedOriginal && !ReviewBusy && !TranslateBusy && !TranscribeBusy && !BatchBusy
                               && LocalTranslator is not null && HasTranslationModel;

    // ---------------- Attaching the original ----------------

    /// <summary>Shows <paramref name="original"/> next to the open translation (called after translating, or when chosen).</summary>
    private void SetReviewOriginal(IEnumerable<SubtitleCue> original, string name, string? language)
    {
        _reviewOriginal = original.Select(c => c.Clone()).ToList();
        _reviewOriginalName = name;
        _reviewOriginalLanguage = language;
        RefreshReview();
        RaiseReview();
    }

    private void ClearReviewOriginal()
    {
        if (_reviewOriginal is null) return;
        _reviewOriginal = null;
        _reviewOriginalName = null;
        _reviewOriginalLanguage = null;
        foreach (var row in Cues)
        {
            row.SourceText = null;
            row.ReviewFlag = null;
        }
        ReviewCount = 0;
        Suggestions.Clear();
        SuggestStatus = string.Empty;
        RaiseReview();
    }

    private void CompareWith(string path)
    {
        try
        {
            var doc = _s.SubtitleFormats.Load(path);
            var language = doc.Language ?? SidecarDetector.ParseStandalone(path).Language ?? SubtitleTranslationPrompt.ScriptLanguage(doc.Cues.Select(c => c.Text));
            if (IsTranslationOfOpen(language))
            {
                OpenTranslationBesideOpen(path, doc, language);
                return;
            }
            SetReviewOriginal(doc.Cues, Path.GetFileName(path), language);
            SetStatus($"Showing {Path.GetFileName(path)} next to the translation: {ReviewSummary}", ReviewCount > 0 ? StatusKind.Warning : StatusKind.Success);
        }
        catch (Exception ex) when (ex is Services.Subtitles.SubtitleFormatException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't open {Path.GetFileName(path)}: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>
    /// The other file is the translation, the open one its original: the other file is in the language
    /// translations go into (Translate's "Into") and the open one isn't.
    /// </summary>
    private bool IsTranslationOfOpen(string? otherLanguage)
    {
        var target = _s.Config.TranslateTarget;
        var open = EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0]
            : SubtitleTranslationPrompt.ScriptLanguage(Cues.Select(r => r.Text));
        return otherLanguage is { } other && other.Split('-', '_')[0].Equals(target, StringComparison.OrdinalIgnoreCase)
               && open is not null && !open.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The open subtitles are the original: the translation opens in the editor with them beside it.</summary>
    private void OpenTranslationBesideOpen(string path, SubtitleDocument translation, string? translationLanguage)
    {
        if (_doc is null) return;
        SyncDocumentFromRows();
        var originalCues = Cues.Select(r => r.Cue.Clone()).ToList();
        var originalName = _doc.SourcePath is { } p ? Path.GetFileName(p) : "the open subtitles (not saved)";
        var originalLanguage = EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0] : SubtitleTranslationPrompt.ScriptLanguage(originalCues.Select(c => c.Text));
        var video = PairedVideo;
        if (IsDirty)
        {
            // The original stays beside the translation, but only in memory: offer to save it first.
            if (_s.Dialogs.Confirm("Save the original first?",
                    $"{Path.GetFileName(path)} is the translation, so it opens in the editor, with the open subtitles beside it as the original.\n\nThe open subtitles aren't saved. Save them first? (Yes: save. No: keep them only beside the translation, until you close it.)"))
            {
                Save();
                if (IsDirty) return; // save cancelled or failed
                originalName = _doc.SourcePath is { } saved ? Path.GetFileName(saved) : originalName;
            }
            else if (_doc.SourcePath is not null)
                originalName += " (with unsaved changes)";
            IsDirty = false;
        }
        translation.SourcePath = path;
        translation.Language ??= translationLanguage;
        LoadDocument(translation, video, selectIndex: 0);
        SetReviewOriginal(originalCues, originalName, originalLanguage);
        SetStatus($"{Path.GetFileName(path)} is the translation: it's open in the editor, with {originalName} beside it as the original. {ReviewSummary}",
            ReviewCount > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    private void CompareWithFile()
    {
        var start = _doc?.SourcePath ?? PairedVideo?.FullPath;
        var file = _s.Dialogs.PickFile("Choose the original subtitles (the language it was translated from)",
            "Subtitles|*.srt;*.vtt;*.ass;*.ssa;*.sub|All files|*.*", start);
        if (file is not null) CompareWith(file);
    }

    /// <summary>Subtitle files of the same video, in another language than the open one.</summary>
    private void RefreshReviewCandidates()
    {
        if (_doc is null)
        {
            ReviewCandidates = Array.Empty<ReviewCandidate>();
            return;
        }
        var list = new List<ReviewCandidate>();
        try
        {
            var mine = _doc.SourcePath is { } p ? Path.GetFullPath(p) : null;
            var language = EditorLanguage.Split('-', '_')[0];
            IEnumerable<SidecarInfo> found = Array.Empty<SidecarInfo>();
            if (PairedVideo is { } video)
                found = SidecarDetector.Detect(video.FullPath, Directory.GetFiles(video.Folder));
            else if (mine is not null)
            {
                var stem = Path.Combine(Path.GetDirectoryName(mine)!, StripLanguage(Path.GetFileNameWithoutExtension(mine)) + ".mkv");
                found = SidecarDetector.Detect(stem, Directory.GetFiles(Path.GetDirectoryName(mine)!));
            }
            foreach (var s in found)
            {
                if (s.Format is "idx" or "sup" || string.Equals(Path.GetFullPath(s.FilePath), mine, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Language is { } l && language.Length > 0 && l.Split('-', '_')[0].Equals(language, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ReviewCandidate(s.FilePath, Path.GetFileName(s.FilePath) + (s.Language is { } lang ? $" ({WhisperLanguages.NameOf(lang.Split('-')[0])})" : string.Empty)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The folder can't be read: no suggestions.
        }
        ReviewCandidates = list;
    }

    // ---------------- Checks ----------------

    /// <summary>Pairs every row with its original and flags the lines to check (after any change).</summary>
    private void RefreshReview()
    {
        if (_reviewOriginal is null) return;
        var rows = Cues.ToList();
        var paired = TranslationReview.Pair(rows.Select(r => r.Cue).ToList(), _reviewOriginal);
        var target = EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0] : _s.Config.TranslateTarget;
        var flags = TranslationReview.CheckAll(paired, rows.Select(r => r.Text).ToList(), target);
        int count = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].SourceText = paired[i];
            rows[i].ReviewFlag = flags[i];
            if (flags[i] is not null) count++;
        }
        ReviewCount = count;
        OnPropertyChanged(nameof(SelectedOriginal));
        OnPropertyChanged(nameof(HasSelectedOriginal));
    }

    private void NextToCheck()
    {
        if (Cues.Count == 0) return;
        int start = SelectedCue is null ? -1 : Cues.IndexOf(SelectedCue);
        for (int k = 1; k <= Cues.Count; k++)
        {
            var row = Cues[(start + k) % Cues.Count];
            if (!row.HasReviewFlag) continue;
            SelectedCue = row;
            SetStatus($"Cue #{row.Number}: {row.ReviewText}", StatusKind.Warning);
            return;
        }
        SetStatus("No lines flagged to check.");
    }

    private void RaiseReview()
    {
        OnPropertyChanged(nameof(HasReviewOriginal));
        OnPropertyChanged(nameof(ReviewOriginalText));
        OnPropertyChanged(nameof(ReviewSummary));
        OnPropertyChanged(nameof(SelectedOriginal));
        OnPropertyChanged(nameof(HasSelectedOriginal));
        OnPropertyChanged(nameof(HasSuggestions));
        RelayCommand.Refresh();
    }

    // ---------------- Suggestions ----------------

    private async Task SuggestAsync()
    {
        var row = SelectedCue;
        if (row?.SourceText is not { } original || LocalTranslator is not { } local || SelectedTranslationModel is not { } model) return;
        int index = Cues.IndexOf(row);
        var before = Cues.Take(index).Where(r => r.SourceText is not null).TakeLast(8)
            .Select(r => (SubtitleTranslationPrompt.Prepare(r.SourceText!).Text, SubtitleTranslationPrompt.Prepare(r.Text).Text)).ToList();
        var after = Cues.Skip(index + 1).Where(r => r.SourceText is not null).Take(3).Select(r => r.SourceText!).ToList();
        var target = EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0] : _s.Config.TranslateTarget;

        local.ModelPath = model.Path;
        local.Device = ResolveWhisperDevice();
        local.Names = CurrentNames.Entries.Select(e => e.Name).ToList();
        LocalWhisper?.ReleaseModel();

        _suggestCts = new CancellationTokenSource();
        ReviewBusy = true;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        SuggestStatus = local.Runner.Architecture is null ? "Loading the language model, then suggesting..." : "Suggesting...";
        try
        {
            var options = await Task.Run(() => local.SuggestAsync(before, original, row.Text, after, _reviewOriginalLanguage, target,
                string.IsNullOrWhiteSpace(ReviewHint) ? null : ReviewHint, 3, _suggestCts.Token));
            if (!ReferenceEquals(row, SelectedCue))
            {
                SuggestStatus = string.Empty;
                return;
            }
            foreach (var text in options)
            {
                var chosen = text;
                Suggestions.Add(new SuggestionRow(chosen, new RelayCommand(() => UseSuggestion(row, chosen))));
            }
            SuggestStatus = options.Count == 0 ? "No suggestions came back; try again with a note." : "Choose one (Undo puts the line back), or edit the text above.";
        }
        catch (OperationCanceledException)
        {
            SuggestStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or LLama.Exceptions.RuntimeError)
        {
            SuggestStatus = "Suggestions failed: " + ex.Message;
            _s.Log.Error("Translate", "Suggestions failed: " + ex);
        }
        finally
        {
            ReviewBusy = false;
            _suggestCts.Dispose();
            _suggestCts = null;
            OnPropertyChanged(nameof(HasSuggestions));
        }
    }

    private void UseSuggestion(CueRowViewModel row, string text)
    {
        if (!Cues.Contains(row)) return;
        PushUndo();
        row.Text = text;
        SetStatus($"Cue #{row.Number} changed (Undo puts it back).", StatusKind.Success);
    }

    private void OnReviewSelectionChanged()
    {
        Suggestions.Clear();
        SuggestStatus = string.Empty;
        OnPropertyChanged(nameof(SelectedOriginal));
        OnPropertyChanged(nameof(HasSelectedOriginal));
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void InitReview()
    {
        CompareWithCandidateCommand = new RelayCommand(() => { if (SelectedReviewCandidate is { } c) CompareWith(c.Path); }, () => _doc is not null && SelectedReviewCandidate is not null);
        CompareWithFileCommand = new RelayCommand(CompareWithFile, () => _doc is not null);
        HideOriginalCommand = new RelayCommand(ClearReviewOriginal, () => HasReviewOriginal);
        NextToCheckCommand = new RelayCommand(NextToCheck, () => ReviewCount > 0);
        SuggestCommand = new AsyncRelayCommand(SuggestAsync, () => CanSuggest);
        CancelSuggestCommand = new RelayCommand(() => _suggestCts?.Cancel(), () => ReviewBusy);
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SelectedCue):
                    OnReviewSelectionChanged();
                    break;
                case nameof(EditorLanguage) or nameof(PairedVideo):
                    RefreshReviewCandidates();
                    RefreshReview();
                    break;
            }
        };
    }
}
