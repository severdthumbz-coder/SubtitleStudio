using System.ComponentModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Subtitles;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// One row in the subtitle editor. Wraps a SubtitleCue and exposes editable text forms of its
/// times. Invalid time input is rejected (the field snaps back) and reported via <see cref="_onInvalid"/>.
/// </summary>
public sealed class CueRowViewModel : ObservableObject
{
    private readonly Action _onEdited;
    private readonly Action<string> _onInvalid;
    private CueIssue? _issue;

    public CueRowViewModel(SubtitleCue cue, Action onEdited, Action<string> onInvalid)
    {
        Cue = cue;
        _onEdited = onEdited;
        _onInvalid = onInvalid;
        cue.PropertyChanged += OnCueChanged;
    }

    public SubtitleCue Cue { get; }

    public int Number => Cue.Index;

    public string StartText
    {
        get => Timecode.Format(Cue.Start);
        set => SetTime(value, isStart: true);
    }

    public string EndText
    {
        get => Timecode.Format(Cue.End);
        set => SetTime(value, isStart: false);
    }

    public string DurationText => Timecode.FormatDuration(Cue.Duration);

    public string Text
    {
        get => Cue.Text;
        set
        {
            var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (Cue.Text == normalized) return;
            Cue.Text = normalized;
            _onEdited();
        }
    }

    public bool IsComment => Cue.Extra is not null && Cue.Extra.TryGetValue("_kind", out var k) && k == "Comment";

    public CueIssue? Issue
    {
        get => _issue;
        set
        {
            if (!SetProperty(ref _issue, value)) return;
            OnPropertyChanged(nameof(HasIssue));
            OnPropertyChanged(nameof(IssueText));
            OnPropertyChanged(nameof(IsError));
        }
    }

    public bool HasIssue => _issue is not null;
    public bool IsError => _issue?.Severity == CueIssueSeverity.Error;
    public string IssueText => _issue?.Message ?? string.Empty;

    private void SetTime(string? value, bool isStart)
    {
        if (!Timecode.TryParse(value, out var t))
        {
            _onInvalid($"'{value}' is not a valid time. Use hh:mm:ss,mmm (e.g. 00:01:02,500).");
            OnPropertyChanged(isStart ? nameof(StartText) : nameof(EndText)); // snap the field back
            return;
        }

        if (isStart ? Cue.Start == t : Cue.End == t) return;
        if (isStart) Cue.Start = t; else Cue.End = t;
        _onEdited();
    }

    private void OnCueChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SubtitleCue.Index):
                OnPropertyChanged(nameof(Number));
                break;
            case nameof(SubtitleCue.Start):
                OnPropertyChanged(nameof(StartText));
                OnPropertyChanged(nameof(DurationText));
                break;
            case nameof(SubtitleCue.End):
                OnPropertyChanged(nameof(EndText));
                OnPropertyChanged(nameof(DurationText));
                break;
            case nameof(SubtitleCue.Text):
                OnPropertyChanged(nameof(Text));
                break;
        }
    }

    /// <summary>Unhook from the model when the row is discarded.</summary>
    public void Detach() => Cue.PropertyChanged -= OnCueChanged;
}
