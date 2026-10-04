using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.ViewModels;

public sealed record OutputFormatOption(string Id, string Display);

/// <summary>
/// Subtitles tab: open / new / edit / validate / save. File formats, validation and cue edits live in
/// Services/Subtitles (SubtitleFormatRegistry, SubtitleValidator, CueOperations); this partial only
/// holds editor state and orchestrates.
/// </summary>
public sealed partial class MainViewModel
{
    private SubtitleDocument? _doc;
    private CueRowViewModel? _selectedCue;
    private bool _isDirty;
    private string _editorFormat = "srt";
    private string _editorLanguage = string.Empty;
    private bool _editorForced, _editorHearingImpaired, _editorSdh;
    private string _editorFrameRateText = MicroDvdFormat.DefaultFrameRate.ToString(CultureInfo.InvariantCulture);
    private MediaItem? _pairedVideo;
    private string _standaloneBaseName = "Untitled";
    private int _issueCount, _errorCount;

    public IReadOnlyList<OutputFormatOption> OutputFormats { get; } = new[]
    {
        new OutputFormatOption("srt", "SubRip (.srt)"),
        new OutputFormatOption("vtt", "WebVTT (.vtt)"),
        new OutputFormatOption("ass", "Advanced SubStation Alpha (.ass)"),
        new OutputFormatOption("ssa", "SubStation Alpha (.ssa)"),
        new OutputFormatOption("sub", "MicroDVD (.sub)"),
    };

    public ObservableCollection<CueRowViewModel> Cues { get; } = new();

    public bool HasDocument => _doc is not null;

    public CueRowViewModel? SelectedCue
    {
        get => _selectedCue;
        set
        {
            if (SetProperty(ref _selectedCue, value)) OnSelectedCueChangedForPreview();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            OnPropertyChanged(nameof(EditorTitle));
        }
    }

    public string EditorTitle
    {
        get
        {
            if (_doc is null) return "Subtitle editor";
            var name = _doc.SourcePath is null ? "Untitled subtitle" : Path.GetFileName(_doc.SourcePath);
            return IsDirty ? name + "  •" : name;
        }
    }

    public string DocumentInfo
    {
        get
        {
            if (_doc is null) return string.Empty;
            var parts = new List<string> { Cues.Count == 1 ? "1 cue" : $"{Cues.Count:N0} cues", _doc.Format.ToUpperInvariant() };
            if (_doc.SourceEncoding is not null) parts.Add(_doc.SourceEncoding);
            else parts.Add("not saved yet");
            return string.Join("  ·  ", parts);
        }
    }

    // ---------------- Save-as settings ----------------

    public string EditorFormat
    {
        get => _editorFormat;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SetProperty(ref _editorFormat, value)) return;
            OnPropertyChanged(nameof(EditorUsesFrames));
            OnPropertyChanged(nameof(SuggestedFileName));
        }
    }

    public bool EditorUsesFrames => EditorFormat == "sub";

    public string EditorLanguage
    {
        get => _editorLanguage;
        set
        {
            if (SetProperty(ref _editorLanguage, (value ?? string.Empty).Trim().ToLowerInvariant()))
                OnPropertyChanged(nameof(SuggestedFileName));
        }
    }

    public bool EditorForced
    {
        get => _editorForced;
        set { if (SetProperty(ref _editorForced, value)) OnPropertyChanged(nameof(SuggestedFileName)); }
    }

    /// <summary>HI and SDH are alternatives for the same flag slot; setting one clears the other.</summary>
    public bool EditorHearingImpaired
    {
        get => _editorHearingImpaired;
        set
        {
            if (!SetProperty(ref _editorHearingImpaired, value)) return;
            if (value) EditorSdh = false;
            OnPropertyChanged(nameof(SuggestedFileName));
        }
    }

    public bool EditorSdh
    {
        get => _editorSdh;
        set
        {
            if (!SetProperty(ref _editorSdh, value)) return;
            if (value) EditorHearingImpaired = false;
            OnPropertyChanged(nameof(SuggestedFileName));
        }
    }

    public string EditorFrameRateText
    {
        get => _editorFrameRateText;
        set
        {
            var text = (value ?? string.Empty).Trim().Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps is <= 1 or >= 200)
            {
                SetStatus($"'{value}' is not a valid frame rate (e.g. 23.976, 25, 29.97).", StatusKind.Warning);
                OnPropertyChanged();
                return;
            }
            SetProperty(ref _editorFrameRateText, fps.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    public IReadOnlyList<MediaItem> VideoFiles => Files.Where(f => f.Kind == MediaKind.Video).ToList();

    public MediaItem? PairedVideo
    {
        get => _pairedVideo;
        set
        {
            if (!SetProperty(ref _pairedVideo, value)) return;
            OnPropertyChanged(nameof(SuggestedFileName));
            OnPropertyChanged(nameof(IsPaired));
            RefreshPreview();
        }
    }

    public bool IsPaired => _pairedVideo is not null;

    public string SuggestedFileName
    {
        get
        {
            var baseName = PairedVideo is not null ? Path.GetFileNameWithoutExtension(PairedVideo.Name) : _standaloneBaseName;
            var ext = _s.SubtitleFormats.ById(EditorFormat)?.Extensions[0] ?? ".srt";
            return SidecarDetector.BuildSidecarName(baseName, EditorLanguage, EditorForced, EditorHearingImpaired, EditorSdh, ext);
        }
    }

    // ---------------- Checks ----------------

    public int IssueCount
    {
        get => _issueCount;
        private set { if (SetProperty(ref _issueCount, value)) OnPropertyChanged(nameof(IssueSummary)); }
    }

    public int ErrorCount
    {
        get => _errorCount;
        private set { if (SetProperty(ref _errorCount, value)) OnPropertyChanged(nameof(IssueSummary)); }
    }

    public string IssueSummary => IssueCount == 0
        ? "No problems found."
        : $"{IssueCount} cue{(IssueCount == 1 ? "" : "s")} need attention ({ErrorCount} error{(ErrorCount == 1 ? "" : "s")}, {IssueCount - ErrorCount} warning{(IssueCount - ErrorCount == 1 ? "" : "s")}).";

    // ---------------- Commands ----------------

    public RelayCommand NewSubtitleCommand { get; private set; } = null!;
    public RelayCommand NewForVideoCommand { get; private set; } = null!;
    public RelayCommand OpenSubtitleCommand { get; private set; } = null!;
    public RelayCommand OpenInEditorCommand { get; private set; } = null!;
    public RelayCommand SaveSubtitleCommand { get; private set; } = null!;
    public RelayCommand SaveSubtitleAsCommand { get; private set; } = null!;
    public RelayCommand AddCueCommand { get; private set; } = null!;
    public RelayCommand DeleteCuesCommand { get; private set; } = null!;
    public RelayCommand SplitCueCommand { get; private set; } = null!;
    public RelayCommand MergeCueCommand { get; private set; } = null!;
    public RelayCommand SortCuesCommand { get; private set; } = null!;
    public RelayCommand NextIssueCommand { get; private set; } = null!;
    public RelayCommand UnpairVideoCommand { get; private set; } = null!;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand RedoCommand { get; private set; } = null!;

    private void InitSubtitles()
    {
        NewSubtitleCommand = new RelayCommand(() => NewDocument(null));
        NewForVideoCommand = new RelayCommand(p => NewDocument(p as MediaItem), p => p is MediaItem { Kind: MediaKind.Video });
        OpenSubtitleCommand = new RelayCommand(() =>
        {
            var path = _s.Dialogs.PickFile("Open subtitle file", _s.SubtitleFormats.OpenFilter, _doc?.SourcePath);
            if (path is not null) OpenSubtitle(path);
        });
        OpenInEditorCommand = new RelayCommand(p =>
        {
            var path = p switch
            {
                MediaItem { Kind: MediaKind.Subtitle } m => m.FullPath,
                SidecarInfo s => s.FilePath,
                string s => s,
                _ => null,
            };
            if (path is not null) OpenSubtitle(path);
        }, p => p is MediaItem { Kind: MediaKind.Subtitle } or SidecarInfo or string);

        SaveSubtitleCommand = new RelayCommand(Save, () => HasDocument);
        SaveSubtitleAsCommand = new RelayCommand(SaveAs, () => HasDocument);
        AddCueCommand = new RelayCommand(AddCue, () => HasDocument);
        DeleteCuesCommand = new RelayCommand(p => DeleteCues(p as IList), _ => HasDocument && Cues.Count > 0);
        SplitCueCommand = new RelayCommand(SplitCue, () => SelectedCue is not null);
        MergeCueCommand = new RelayCommand(MergeCue, () => SelectedCue is not null && Cues.IndexOf(SelectedCue) < Cues.Count - 1);
        SortCuesCommand = new RelayCommand(SortCues, () => HasDocument && Cues.Count > 1);
        NextIssueCommand = new RelayCommand(NextIssue, () => IssueCount > 0);
        UnpairVideoCommand = new RelayCommand(() => PairedVideo = null, () => IsPaired);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redo.Count > 0);

        Files.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VideoFiles));
    }

    /// <summary>Called when the window closes. False = the user wants to keep editing.</summary>
    public bool ConfirmCloseEditor()
    {
        if (BatchBusy)
        {
            if (!_s.Dialogs.Confirm("Stop the Batch queue?", "The Batch queue is still running. Close anyway?\n\nSubtitles already saved are kept, and pressing Start next time carries on with the files that aren't done."))
                return false;
            _batchCts?.Cancel();
        }
        return ConfirmDiscardChanges();
    }

    // ---------------- Open / new ----------------

    public void OpenSubtitle(string path)
    {
        if (!ConfirmDiscardChanges()) return;

        SubtitleDocument doc;
        try
        {
            doc = _s.SubtitleFormats.Load(path);
        }
        catch (Exception ex) when (ex is SubtitleFormatException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't open {Path.GetFileName(path)}: {ex.Message}", StatusKind.Error);
            return;
        }

        // Pair with a listed video if the name matches, so "Save" keeps the Plex/Jellyfin naming.
        var video = VideoFiles
            .Where(v => SidecarDetector.TryParse(v.FullPath, path) is not null)
            .OrderByDescending(v => Path.GetFileNameWithoutExtension(v.Name).Length)
            .FirstOrDefault();

        if (video is not null && SidecarDetector.TryParse(video.FullPath, path) is { } info)
        {
            doc.Language = info.Language;
            doc.Forced = info.Forced;
            doc.HearingImpaired = info.HearingImpaired;
            doc.Sdh = info.Sdh;
            _standaloneBaseName = Path.GetFileNameWithoutExtension(video.Name);
        }
        else
        {
            var standalone = SidecarDetector.ParseStandalone(path);
            doc.Language = standalone.Language;
            doc.Forced = standalone.Forced;
            doc.HearingImpaired = standalone.HearingImpaired;
            doc.Sdh = standalone.Sdh;
            _standaloneBaseName = standalone.BaseName.Length > 0 ? standalone.BaseName : "Untitled";
        }

        LoadDocument(doc, video, selectIndex: 0);

        var message = $"Opened {Path.GetFileName(path)}: {Cues.Count:N0} cues ({doc.SourceEncoding}).";
        if (doc.FrameRateAssumed)
            message += $" No frame-rate line in the file; assumed {doc.FrameRate:0.###} fps. Check timings and set the rate before saving.";
        SetStatus(message, doc.FrameRateAssumed || IssueCount > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    private void NewDocument(MediaItem? video)
    {
        if (!ConfirmDiscardChanges()) return;

        var doc = new SubtitleDocument
        {
            Format = _s.Config.DefaultOutputFormat,
            Language = _s.Config.DefaultLanguage,
            FrameRate = _s.Config.DefaultOutputFormat == "sub" ? MicroDvdFormat.DefaultFrameRate : null,
        };
        doc.Cues.Add(CueOperations.CreateAfter(null, null));
        _standaloneBaseName = video is not null ? Path.GetFileNameWithoutExtension(video.Name) : "Untitled";

        LoadDocument(doc, video, selectIndex: 0);
        SetStatus(video is null ? "New subtitle started." : $"New subtitle for {video.Name}.");
    }

    /// <summary>Opens cues produced elsewhere (e.g. OCR of burned-in subtitles) as a new, unsaved document.</summary>
    private void OpenExtractedSubtitle(List<SubtitleCue> cues, MediaItem video, string? language)
    {
        var doc = new SubtitleDocument
        {
            Format = _s.Config.DefaultOutputFormat,
            Language = string.IsNullOrWhiteSpace(language) ? _s.Config.DefaultLanguage : language,
            FrameRate = _s.Config.DefaultOutputFormat == "sub" ? MicroDvdFormat.DefaultFrameRate : null,
        };
        doc.Cues.AddRange(cues);
        _standaloneBaseName = Path.GetFileNameWithoutExtension(video.Name);
        LoadDocument(doc, video, selectIndex: 0);
        IsDirty = true;
    }

    private void LoadDocument(SubtitleDocument doc, MediaItem? video, int selectIndex)
    {
        ClearReviewOriginal();
        foreach (var row in Cues) row.Detach();
        Cues.Clear();

        _doc = doc;
        CueOperations.Renumber(doc.Cues);
        foreach (var cue in doc.Cues) Cues.Add(NewRow(cue));

        EditorFormat = doc.Format;
        EditorLanguage = doc.Language ?? string.Empty;
        EditorForced = doc.Forced;
        EditorHearingImpaired = doc.HearingImpaired;
        EditorSdh = doc.Sdh;
        _editorFrameRateText = (doc.FrameRate ?? MicroDvdFormat.DefaultFrameRate).ToString("0.###", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(EditorFrameRateText));

        IsDirty = false;
        _undo.Clear();
        _redo.Clear();
        Revalidate();
        SelectedCue = Cues.Count == 0 ? null : Cues[Math.Clamp(selectIndex, 0, Cues.Count - 1)];
        PairedVideo = video;   // after the selection, so the preview opens at the selected cue
        RefreshPreview();
        UpdateOverlay();

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(DocumentInfo));
        OnPropertyChanged(nameof(SuggestedFileName));
        RefreshReviewCandidates();
        SelectedTabIndex = Tabs.Subtitles;
    }

    private bool ConfirmDiscardChanges()
    {
        if (_doc is null || !IsDirty) return true;
        var name = _doc.SourcePath is null ? "the untitled subtitle" : Path.GetFileName(_doc.SourcePath);
        return _s.Dialogs.Confirm("Unsaved changes", $"Discard the unsaved changes to {name}?");
    }

    // ---------------- Save ----------------

    private void Save()
    {
        if (_doc is null) return;
        var path = _doc.SourcePath;
        var sourceFormat = path is null ? null : _s.SubtitleFormats.ForExtension(Path.GetExtension(path))?.Id;
        if (path is not null && sourceFormat == EditorFormat)
            SaveTo(path, EditorFormat);
        else
            SaveAs(); // untitled, or the format was changed: never silently overwrite with another format
    }

    private void SaveAs()
    {
        if (_doc is null) return;

        var folder = PairedVideo?.Folder
                     ?? (_doc.SourcePath is not null ? Path.GetDirectoryName(_doc.SourcePath) : null);
        var (filter, index) = _s.SubtitleFormats.SaveFilter(EditorFormat);
        var path = _s.Dialogs.SaveFile("Save subtitle as", filter, index, folder, SuggestedFileName);
        if (path is null) return;

        var format = _s.SubtitleFormats.ForExtension(Path.GetExtension(path))?.Id ?? EditorFormat;
        SaveTo(path, format);
    }

    private void SaveTo(string path, string formatId)
    {
        if (_doc is null) return;

        Revalidate();
        if (ErrorCount > 0 && !_s.Dialogs.Confirm("Problems found",
                $"{ErrorCount} cue{(ErrorCount == 1 ? " has" : "s have")} errors (for example a negative or zero duration) that players may reject.\n\nSave anyway?"))
        {
            NextIssue();
            return;
        }

        SyncDocumentFromRows();
        _doc.Language = EditorLanguage.Length == 0 ? null : EditorLanguage;
        _doc.Forced = EditorForced;
        _doc.HearingImpaired = EditorHearingImpaired;
        _doc.Sdh = EditorSdh;
        if (formatId == "sub")
            _doc.FrameRate = double.Parse(EditorFrameRateText, CultureInfo.InvariantCulture);

        try
        {
            _s.SubtitleFormats.Save(_doc, path, formatId);
        }
        catch (Exception ex) when (ex is SubtitleFormatException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save {Path.GetFileName(path)}: {ex.Message}", StatusKind.Error);
            return;
        }

        var selected = SelectedCue is null ? 0 : Cues.IndexOf(SelectedCue);
        if (formatId != _doc.Format)
        {
            // Converted to another format: reload what was written so the editor shows exactly
            // the text and header that are now on disk.
            try
            {
                var reloaded = _s.SubtitleFormats.Load(path);
                reloaded.Language = _doc.Language;
                reloaded.Forced = _doc.Forced;
                reloaded.HearingImpaired = _doc.HearingImpaired;
                reloaded.Sdh = _doc.Sdh;
                LoadDocument(reloaded, PairedVideo, selected);
            }
            catch (Exception ex) when (ex is SubtitleFormatException or IOException or UnauthorizedAccessException)
            {
                SetStatus($"Saved, but could not reload {Path.GetFileName(path)}: {ex.Message}", StatusKind.Warning);
                return;
            }
        }
        else
        {
            _doc.SourcePath = path;
            _doc.SourceEncoding = formatId == "vtt" ? "UTF-8" : "UTF-8 (BOM)";
            IsDirty = false;
            OnPropertyChanged(nameof(EditorTitle));
            OnPropertyChanged(nameof(DocumentInfo));
        }

        RefreshSidecarsIn(Path.GetDirectoryName(path));
        var warn = IssueCount - ErrorCount;
        SetStatus($"Saved {Path.GetFileName(path)} ({Cues.Count:N0} cues).{(warn > 0 ? $" {warn} warning{(warn == 1 ? "" : "s")} remain." : string.Empty)}",
            warn > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    // ---------------- Cue edits ----------------

    private void AddCue()
    {
        if (_doc is null) return;
        PushUndo();
        int index = SelectedCue is null ? Cues.Count - 1 : Cues.IndexOf(SelectedCue);
        var previous = index >= 0 ? Cues[index].Cue : null;
        var next = index + 1 < Cues.Count ? Cues[index + 1].Cue : null;

        var row = NewRow(CueOperations.CreateAfter(previous, next));
        Cues.Insert(index + 1, row);
        AfterStructureChange(row);
    }

    private void DeleteCues(IList? selection)
    {
        var rows = selection?.OfType<CueRowViewModel>().ToList() ?? new List<CueRowViewModel>();
        if (rows.Count == 0 && SelectedCue is not null) rows.Add(SelectedCue);
        if (rows.Count == 0) return;

        PushUndo();
        int firstIndex = rows.Min(r => Cues.IndexOf(r));
        foreach (var row in rows)
        {
            row.Detach();
            Cues.Remove(row);
        }
        AfterStructureChange(Cues.Count == 0 ? null : Cues[Math.Min(firstIndex, Cues.Count - 1)]);
        SetStatus(rows.Count == 1 ? "Cue deleted." : $"{rows.Count} cues deleted.");
    }

    private void SplitCue()
    {
        if (SelectedCue is null) return;
        PushUndo();
        int index = Cues.IndexOf(SelectedCue);
        var (first, second) = CueOperations.Split(SelectedCue.Cue);

        SelectedCue.Detach();
        Cues.RemoveAt(index);
        Cues.Insert(index, NewRow(first));
        var secondRow = NewRow(second);
        Cues.Insert(index + 1, secondRow);
        AfterStructureChange(secondRow);
    }

    private void MergeCue()
    {
        if (SelectedCue is null) return;
        PushUndo();
        int index = Cues.IndexOf(SelectedCue);
        if (index < 0 || index + 1 >= Cues.Count) return;

        var merged = NewRow(CueOperations.Merge(Cues[index].Cue, Cues[index + 1].Cue));
        Cues[index + 1].Detach();
        Cues[index].Detach();
        Cues.RemoveAt(index + 1);
        Cues.RemoveAt(index);
        Cues.Insert(index, merged);
        AfterStructureChange(merged);
    }

    private void SortCues()
    {
        PushUndo();
        var selected = SelectedCue;
        var order = CueOperations.SortedByTime(Cues.Select(r => r.Cue));
        var rows = order.Select(c => Cues.First(r => ReferenceEquals(r.Cue, c))).ToList();
        Cues.Clear();
        foreach (var row in rows) Cues.Add(row);
        AfterStructureChange(selected);
        SetStatus("Cues sorted by start time.");
    }

    private void NextIssue()
    {
        if (Cues.Count == 0) return;
        int start = SelectedCue is null ? -1 : Cues.IndexOf(SelectedCue);
        for (int step = 1; step <= Cues.Count; step++)
        {
            var row = Cues[(start + step) % Cues.Count];
            if (row.HasIssue)
            {
                SelectedCue = row;
                SetStatus($"Cue #{row.Number}: {row.IssueText}", row.IsError ? StatusKind.Error : StatusKind.Warning);
                return;
            }
        }
    }

    // ---------------- Helpers ----------------

    private CueRowViewModel NewRow(SubtitleCue cue)
        => new(cue, OnCueEdited, message => SetStatus(message, StatusKind.Warning));

    private void OnCueEdited()
    {
        IsDirty = true;
        Revalidate();
        UpdateOverlay();
    }

    private void AfterStructureChange(CueRowViewModel? select)
    {
        SyncDocumentFromRows();
        SelectedCue = select;
        IsDirty = true;
        Revalidate();
        UpdateOverlay();
        OnPropertyChanged(nameof(DocumentInfo));
    }

    private void SyncDocumentFromRows()
    {
        if (_doc is null) return;
        _doc.Cues.Clear();
        _doc.Cues.AddRange(Cues.Select(r => r.Cue));
        CueOperations.Renumber(_doc.Cues);
    }

    // ---------------- Undo / redo ----------------
    // Snapshots of the whole cue list, taken before structural and sync operations. Typing in a
    // text or time field is covered by the field's own Ctrl+Z, not by these snapshots.

    private const int UndoLimit = 50;
    private readonly List<List<SubtitleCue>> _undo = new();
    private readonly List<List<SubtitleCue>> _redo = new();

    private List<SubtitleCue> SnapshotCues() => Cues.Select(r => r.Cue.Clone()).ToList();

    private void PushUndo(List<SubtitleCue>? snapshot = null)
    {
        if (_doc is null) return;
        _undo.Add(snapshot ?? SnapshotCues());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Undo() => SwapSnapshot(_undo, _redo, "Undone.");

    private void Redo() => SwapSnapshot(_redo, _undo, "Redone.");

    private void SwapSnapshot(List<List<SubtitleCue>> from, List<List<SubtitleCue>> to, string message)
    {
        if (_doc is null || from.Count == 0) return;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(SnapshotCues());

        int selected = SelectedCue is null ? 0 : Math.Max(0, Cues.IndexOf(SelectedCue));
        foreach (var row in Cues) row.Detach();
        Cues.Clear();
        foreach (var cue in snapshot) Cues.Add(NewRow(cue));
        AfterStructureChange(Cues.Count == 0 ? null : Cues[Math.Min(selected, Cues.Count - 1)]);
        SetStatus(message);
    }

    private void Revalidate()
    {
        if (_doc is null)
        {
            IssueCount = ErrorCount = 0;
            return;
        }

        var checkOverlaps = SubtitleText.DialectOf(_doc.Format) != TextDialect.Ass;
        var issues = SubtitleValidator.Validate(Cues.Select(r => r.Cue).ToList(), checkOverlaps);
        int errors = 0;
        foreach (var row in Cues)
        {
            row.Issue = issues.TryGetValue(row.Cue, out var issue) ? issue : null;
            if (row.IsError) errors++;
        }
        IssueCount = issues.Count;
        ErrorCount = errors;
        RefreshReview();
    }
}
