using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Hardware;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// Log tab: everything the app did (status messages, each detection / extraction / removal with its
/// settings, speed and timing, encoder and AI device choices, ffmpeg command lines and errors), plus the
/// hardware profile. Kept in Services/ActivityLog and subt_activity.log next to the EXE.
/// Also holds the hardware profile and the performance plan used by removal.
/// </summary>
public sealed partial class MainViewModel
{
    public const int LogViewLimit = 3000;

    private bool _showLogDetails;
    private int _logWarnings, _logErrors;
    private HardwareProfile? _hardware;
    private PerformancePlan _plan = PerformancePlan.Default;

    /// <summary>Entries shown in the Log tab (details only when <see cref="ShowLogDetails"/>), oldest first.</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public bool ShowLogDetails
    {
        get => _showLogDetails;
        set
        {
            if (!SetProperty(ref _showLogDetails, value)) return;
            RebuildLogView();
        }
    }

    public string LogSummary => $"{_s.Log.Snapshot().Count} entries · {_logWarnings} warnings · {_logErrors} errors"
        + (_s.Log.FilePath is { } f ? $" · also saved to {Path.GetFileName(f)} next to the app" : string.Empty);

    public string? LogFilePath => _s.Log.FilePath;

    public RelayCommand CopyLogCommand { get; private set; } = null!;
    public RelayCommand OpenLogFileCommand { get; private set; } = null!;
    public RelayCommand ClearLogViewCommand { get; private set; } = null!;

    // ---------------- hardware ----------------

    public HardwareProfile? Hardware => _hardware;
    public PerformancePlan Plan => _plan;

    public string HardwareCpuText => _hardware?.CpuText ?? "Detecting...";
    public string HardwareRamText => _hardware?.RamText ?? "Detecting...";
    public string HardwarePlanText => _hardware is null ? string.Empty : $"Removal uses {_plan.Summary}.";
    public string HardwareAppDriveText => HardwareDetector.DriveOf(AppPaths.ExeDirectory) is { } d ? $"App folder: {d.Summary}" : string.Empty;

    private void InitLog()
    {
        CopyLogCommand = new RelayCommand(() =>
        {
            _s.Dialogs.CopyText(_s.Log.ToText(_showLogDetails));
            SetStatus(_showLogDetails ? "Copied the log (with details)." : "Copied the log.", StatusKind.Success);
        });
        OpenLogFileCommand = new RelayCommand(() => { if (_s.Log.FilePath is { } f) _s.Dialogs.RevealInExplorer(f); },
            () => _s.Log.FilePath is { } f && File.Exists(f));
        ClearLogViewCommand = new RelayCommand(() => LogEntries.Clear());

        foreach (var e in _s.Log.Snapshot()) Count(e);
        RebuildLogView();
        _s.Log.Added += e => RunOnUi(() =>
        {
            Count(e);
            if (_showLogDetails || e.Level != LogLevel.Detail)
            {
                LogEntries.Add(e);
                if (LogEntries.Count > LogViewLimit) LogEntries.RemoveAt(0);
            }
            OnPropertyChanged(nameof(LogSummary));
        });
    }

    private void Count(LogEntry e)
    {
        if (e.Level == LogLevel.Warning) _logWarnings++;
        if (e.Level == LogLevel.Error) _logErrors++;
    }

    private void RebuildLogView()
    {
        LogEntries.Clear();
        foreach (var e in _s.Log.Snapshot().Where(e => _showLogDetails || e.Level != LogLevel.Detail).TakeLast(LogViewLimit))
            LogEntries.Add(e);
        OnPropertyChanged(nameof(LogSummary));
    }

    /// <summary>After the graphics card is known: read the rest of the PC and decide how to use it.</summary>
    private void DetectHardware(Services.Gpu.GpuInfo? gpu)
    {
        _ = Task.Run(() => HardwareDetector.Detect(gpu)).ContinueWith(t => RunOnUi(() =>
        {
            if (!t.IsCompletedSuccessfully) return;
            _hardware = t.Result;
            _plan = _hardware.TotalRamBytes > 0 ? PerformancePlan.For(_hardware) : PerformancePlan.Default;
            _s.Log.Info("Hardware", $"{_hardware.CpuText}; {_hardware.RamText}; graphics: {gpu?.Summary ?? "none"}.");
            if (HardwareDetector.DriveOf(AppPaths.ExeDirectory) is { } d) _s.Log.Info("Hardware", "App folder on " + d.Summary + ".");
            _s.Log.Info("Hardware", $"Plan: {_plan.Summary}.");
            OnPropertyChanged(nameof(HardwareCpuText));
            OnPropertyChanged(nameof(HardwareRamText));
            OnPropertyChanged(nameof(HardwarePlanText));
            OnPropertyChanged(nameof(HardwareAppDriveText));
        }), TaskScheduler.Default);
    }

    private static LogLevel LevelOf(StatusKind kind) => kind switch
    {
        StatusKind.Success => LogLevel.Success,
        StatusKind.Warning => LogLevel.Warning,
        StatusKind.Error => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
