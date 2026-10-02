using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>Similar spellings found after translating, with a button that adds them to the names list.</summary>
public sealed record NameSuggestionRow(string Label, NameEntry Entry, RelayCommand AddCommand);

/// <summary>
/// Translate tab, names: a names list per show (picked from the file name) and the matching of
/// near-identical spellings after translating. The work is in Services/Translation/NameConsistency.
/// </summary>
public sealed partial class MainViewModel
{
    private string _nameReportText = string.Empty;

    /// <summary>The show the open subtitles belong to ("Hyper Knife"), from the video's or the file's name.</summary>
    public string TranslateShowName
    {
        get
        {
            var name = NameConsistency.ShowOf(PairedVideo?.Name ?? _doc?.SourcePath);
            return name.Length > 0 ? name : NameConsistency.ShowOf(_standaloneBaseName);
        }
    }

    private string ShowKey => TranslateShowName.ToLowerInvariant();

    public string NamesHeader => TranslateShowName.Length > 0 ? $"Names in {TranslateShowName}" : "Names";

    /// <summary>The show's names list as typed (one name per line, "Name = other spelling, other spelling").</summary>
    public string TranslateNamesText
    {
        get => _s.Config.TranslateNames.TryGetValue(ShowKey, out var text) ? text : string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == TranslateNamesText) return;
            if (text.Length == 0) _s.Config.TranslateNames.Remove(ShowKey);
            else _s.Config.TranslateNames[ShowKey] = text;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NamesSummary));
            RelayCommand.Refresh();
        }
    }

    private NameList CurrentNames => NameList.Parse(TranslateNamesText);

    /// <summary>Make near-identical spellings match after translating (Settings; on by default).</summary>
    public bool NameMatching
    {
        get => _s.Config.TranslateNameMatching;
        set
        {
            if (_s.Config.TranslateNameMatching == value) return;
            _s.Config.TranslateNameMatching = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NamesSummary));
        }
    }

    public string NamesSummary
    {
        get
        {
            var list = CurrentNames;
            var listText = list.IsEmpty ? "No names listed yet." : $"{list.Entries.Count} name{(list.Entries.Count == 1 ? "" : "s")} listed: always spelled this way, and given to the language model.";
            var matching = NameMatching
                ? " Matching is on: after translating, spellings a letter apart become the usual one, and look-alikes are suggested below."
                : " Matching is off (Settings): only the list is used.";
            return listText + matching;
        }
    }

    public ObservableCollection<NameSuggestionRow> NameSuggestions { get; } = new();

    public bool HasNameSuggestions => NameSuggestions.Count > 0;

    /// <summary>What the last name pass changed.</summary>
    public string NameReportText
    {
        get => _nameReportText;
        private set
        {
            if (SetProperty(ref _nameReportText, value)) OnPropertyChanged(nameof(HasNameReport));
        }
    }

    public bool HasNameReport => _nameReportText.Length > 0;

    public RelayCommand ApplyNamesCommand { get; private set; } = null!;

    /// <summary>Applies the list (and matching) to cues; logs and shows what changed; refreshes the suggestions.</summary>
    private NameReport ApplyNames(IList<SubtitleCue> cues)
    {
        var report = NameConsistency.Apply(cues, CurrentNames, NameMatching);
        foreach (var c in report.Changes)
            _s.Log.Info("Names", $"\"{c.From}\" → \"{c.To}\" in {c.Cues} cue{(c.Cues == 1 ? "" : "s")}" + (c.FromList ? " (names list)" : " (near-identical spelling)") + ".");
        foreach (var g in report.Suggestions)
            _s.Log.Detail("Names", $"Look-alike spellings, maybe one person: {g.Label}.");

        NameSuggestions.Clear();
        foreach (var g in report.Suggestions)
        {
            var entry = g.AsEntry();
            NameSuggestionRow? row = null;
            row = new NameSuggestionRow(g.Label, entry, new RelayCommand(() =>
            {
                TranslateNamesText = NameList.Add(TranslateNamesText, entry);
                if (row is not null) NameSuggestions.Remove(row);
                OnPropertyChanged(nameof(HasNameSuggestions));
                SetStatus($"Added \"{NameList.Format(entry)}\" to the names list of {TranslateShowName}. Apply names to use it on the open subtitles.", StatusKind.Success);
            }));
            NameSuggestions.Add(row);
        }
        OnPropertyChanged(nameof(HasNameSuggestions));

        NameReportText = report.Changes.Count == 0
            ? (report.Suggestions.Count > 0 ? "No spellings changed. Look-alikes to check are listed below." : "Names: nothing to change.")
            : $"Names made consistent in {report.CuesChanged} cue{(report.CuesChanged == 1 ? "" : "s")}: "
              + string.Join("; ", report.Changes.Take(6).Select(c => $"{c.From} → {c.To}")) + (report.Changes.Count > 6 ? $"; and {report.Changes.Count - 6} more (see the Log)" : string.Empty) + ".";
        return report;
    }

    /// <summary>The names list and matching on the subtitles open in the editor (one undo step).</summary>
    private void ApplyNamesToEditor()
    {
        if (_doc is null || Cues.Count == 0) return;
        var before = SnapshotCues();
        var cues = SnapshotCues();
        var report = ApplyNames(cues);
        if (report.CuesChanged == 0)
        {
            SetStatus(report.Suggestions.Count > 0 ? "No names changed; look-alikes are listed in the Translate tab." : "No names to change.");
            return;
        }
        PushUndo(before);
        int selected = SelectedCue is null ? 0 : Math.Max(0, Cues.IndexOf(SelectedCue));
        foreach (var row in Cues) row.Detach();
        Cues.Clear();
        foreach (var cue in cues) Cues.Add(NewRow(cue));
        AfterStructureChange(Cues.Count == 0 ? null : Cues[Math.Min(selected, Cues.Count - 1)]);
        SetStatus($"Names made consistent in {report.CuesChanged} cue{(report.CuesChanged == 1 ? "" : "s")} (Undo puts them back).", StatusKind.Success);
    }

    private void RaiseNames()
    {
        OnPropertyChanged(nameof(TranslateShowName));
        OnPropertyChanged(nameof(NamesHeader));
        OnPropertyChanged(nameof(TranslateNamesText));
        OnPropertyChanged(nameof(NamesSummary));
    }

    private void InitTranslateNames()
    {
        ApplyNamesCommand = new RelayCommand(ApplyNamesToEditor, () => _doc is not null && Cues.Count > 0 && !TranslateBusy);
    }
}
