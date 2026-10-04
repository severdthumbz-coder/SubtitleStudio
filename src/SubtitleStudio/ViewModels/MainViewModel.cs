using System.Windows;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;

namespace SubtitleStudio.ViewModels;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Tab order in MainWindow. Keep in sync with the TabItems.</summary>
public static class Tabs
{
    public const int Files = 0;
    public const int Subtitles = 1;
    public const int BurnedIn = 2;
    public const int Transcribe = 3;
    public const int Translate = 4;
    public const int Batch = 5;
    public const int Dubbing = 6;
    public const int Settings = 7;
    public const int Log = 8;
    public const int Help = 9;
}

/// <summary>
/// Shell view model. This file holds ONLY shared state (status, version, tab selection, theme toggle).
/// Each area lives in its own partial: MainViewModel.Files.cs, .Subtitles.cs, .Transcribe.cs,
/// .Translate.cs, .Dubbing.cs, .Settings.cs, .Log.cs, .Help.cs. Real work is delegated to Services/.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private string _statusText = "Ready";
    private StatusKind _statusKind = StatusKind.Info;
    private int _selectedTabIndex;

    public MainViewModel(AppServices services)
    {
        _s = services;

        ShowSettingsCommand = new RelayCommand(() => SelectedTabIndex = Tabs.Settings);
        ShowHelpCommand = new RelayCommand(() => SelectedTabIndex = Tabs.Help);
        ToggleThemeCommand = new RelayCommand(() => Theme = _s.Theme.IsDark ? ThemeService.Light : ThemeService.Dark);

        _s.Settings.SaveFailed += message => RunOnUi(() => SetStatus(message, StatusKind.Error));

        InitLog();
        InitFiles();
        InitSubtitles();
        InitPreview();
        InitSync();
        InitBurnedIn();
        InitTranscribe();
        InitTranslate();
        InitBatch();
        InitMux();
        InitEmbedded();
        InitReview();
        InitDubbing();
        InitSettings();
        InitHelp();
    }

    public string Version => AppInfo.Version;

    /// <summary>"v1.0.0 build 5": shown under the app name, in the title bar and the status bar.</summary>
    public string DisplayVersion => AppInfo.DisplayVersion;

    public string WindowTitle => $"{AppInfo.ProductName}  {DisplayVersion}";

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value)) OnTabSelected(value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    public void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        StatusText = text;
        StatusKind = kind;
        _s.Log.Write(LevelOf(kind), "Status", text);
    }

    /// <summary>Called by the window on close so the next launch reopens at the same size.</summary>
    public void RememberWindow(double width, double height, bool maximized)
    {
        _s.Config.Window = new Models.WindowPlacement { Width = width, Height = height, Maximized = maximized };
        SaveSettings();
    }

    private void SaveSettings() => _s.Settings.ScheduleSave(_s.Config);

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
