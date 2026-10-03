using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services.Batch;

namespace SubtitleStudio.ViewModels;

/// <summary>One file in the Batch queue: whether it's included, what's happening to it, and what it produced.</summary>
public sealed class QueueRowViewModel : ObservableObject
{
    private bool _include = true;
    private QueueStep _step = QueueStep.Waiting;
    private string _status = "Waiting";
    private double _progress;
    private string? _transcriptPath;
    private string? _translationPath;

    public QueueRowViewModel(MediaItem item)
    {
        Item = item;
    }

    public MediaItem Item { get; }

    public string Name => Item.Name;

    public string FullPath => Item.FullPath;

    public bool Include
    {
        get => _include;
        set
        {
            if (SetProperty(ref _include, value)) IncludeChanged?.Invoke();
        }
    }

    /// <summary>Raised when the check box changes (the tab's summary counts included files).</summary>
    public Action? IncludeChanged { get; set; }

    public QueueStep Step
    {
        get => _step;
        set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(StepText));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public string StepText => _step switch
    {
        QueueStep.Waiting => _transcriptPath is null ? "Waiting" : "Transcribed",
        QueueStep.Transcribing => "Transcribing",
        QueueStep.Translating => "Translating",
        QueueStep.Done => "Done",
        QueueStep.Skipped => "Skipped",
        QueueStep.Failed => "Failed",
        QueueStep.Cancelled => "Cancelled",
        _ => _step.ToString(),
    };

    public bool IsActive => _step is QueueStep.Transcribing or QueueStep.Translating;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Of this file's work, 0 to 100.</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string? TranscriptPath
    {
        get => _transcriptPath;
        set
        {
            if (!SetProperty(ref _transcriptPath, value)) return;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(StepText));
        }
    }

    public string? TranslationPath
    {
        get => _translationPath;
        set
        {
            if (SetProperty(ref _translationPath, value)) OnPropertyChanged(nameof(HasResult));
        }
    }

    public bool HasResult => _transcriptPath is not null || _translationPath is not null;

    /// <summary>The subtitles to open on double-click: the translation, else the transcript.</summary>
    public string? ResultPath => _translationPath ?? _transcriptPath;

    public void Reset()
    {
        Step = QueueStep.Waiting;
        Status = "Waiting";
        Progress = 0;
        TranscriptPath = null;
        TranslationPath = null;
    }
}
