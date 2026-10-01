using System.Collections.ObjectModel;
using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Abstractions;
using SubtitleStudio.Services.Onboarding;

namespace SubtitleStudio.ViewModels;

public sealed record SettingsCategory(string Key, string Title, string Glyph);

/// <summary>Settings tab. Every setter writes through to AppSettings and schedules an auto-save.</summary>
public sealed partial class MainViewModel
{
    private string _selectedSettingsCategory = "keys";
    private FfmpegStatus _ffmpegStatus = new(null, null, "not checked");

    public IReadOnlyList<SettingsCategory> SettingsCategories { get; } = new[]
    {
        new SettingsCategory("keys", "API keys", ""),
        new SettingsCategory("engines", "Engines & tools", ""),
        new SettingsCategory("output", "Output defaults", ""),
        new SettingsCategory("behaviour", "Behaviour", ""),
        new SettingsCategory("appearance", "Appearance", ""),
        new SettingsCategory("storage", "Storage", ""),
    };

    public string SelectedSettingsCategory
    {
        get => _selectedSettingsCategory;
        set => SetProperty(ref _selectedSettingsCategory, value ?? "keys");
    }

    public ObservableCollection<ApiKeyEntryViewModel> ApiKeyEntries { get; } = new();

    public RelayCommand BrowseFfmpegCommand { get; private set; } = null!;
    public RelayCommand DetectFfmpegCommand { get; private set; } = null!;
    public RelayCommand OpenSettingsFolderCommand { get; private set; } = null!;

    private void InitSettings()
    {
        ApiKeyEntries.Add(new ApiKeyEntryViewModel(
            ProviderIds.OpenAI,
            "OpenAI",
            "Cloud speech-to-text, translation and text-to-speech. AI Transcribe runs on this PC and needs no key; online engines that use this key arrive in later builds, and when one can be used the app asks which to use.",
            _s.ApiKeys,
            _s.Dialogs,
            SetStatus));

        BrowseFfmpegCommand = new RelayCommand(() =>
        {
            var picked = _s.Dialogs.PickFile("Locate ffmpeg.exe", "ffmpeg|ffmpeg.exe|Programs|*.exe", FfmpegPath);
            if (picked is not null) FfmpegPath = picked;
        });
        DetectFfmpegCommand = new RelayCommand(() =>
        {
            RefreshFfmpegStatus();
            SetStatus(FfmpegStatus.Summary, FfmpegStatus.IsComplete ? StatusKind.Success : StatusKind.Warning);
        });
        OpenSettingsFolderCommand = new RelayCommand(() => _s.Dialogs.RevealInExplorer(SettingsFilePath));

        HintService.Current.IsEnabled = _s.Config.ShowHints;
        RefreshFfmpegStatus();
    }

    // ---------------- Engines & tools ----------------

    public string FfmpegPath
    {
        get => _s.Config.FfmpegPath;
        set
        {
            value = (value ?? string.Empty).Trim().Trim('"');
            if (_s.Config.FfmpegPath == value) return;
            _s.Config.FfmpegPath = value;
            OnPropertyChanged();
            SaveSettings();
            RefreshFfmpegStatus();
        }
    }

    public FfmpegStatus FfmpegStatus
    {
        get => _ffmpegStatus;
        private set
        {
            if (!SetProperty(ref _ffmpegStatus, value)) return;
            OnPropertyChanged(nameof(FfmpegReady));
            OnPropertyChanged(nameof(FfmpegStatusText));
            OnPropertyChanged(nameof(FfmpegShortText));
        }
    }

    public bool FfmpegReady => FfmpegStatus.IsComplete;
    public string FfmpegStatusText => FfmpegStatus.Summary;
    public string FfmpegShortText => FfmpegStatus.ShortText;

    private void RefreshFfmpegStatus()
    {
        FfmpegStatus = _s.Ffmpeg.Resolve(_s.Config.FfmpegPath);
        OnPropertyChanged(nameof(TranscribeBlockedText));
        ProbeMissingDurations();
    }

    // ---------------- Output defaults ----------------

    public string DefaultOutputFormat
    {
        get => _s.Config.DefaultOutputFormat;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _s.Config.DefaultOutputFormat == value) return;
            _s.Config.DefaultOutputFormat = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SidecarPreview));
            SaveSettings();
        }
    }

    public string DefaultLanguage
    {
        get => _s.Config.DefaultLanguage;
        set
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || _s.Config.DefaultLanguage == value) return;
            _s.Config.DefaultLanguage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SidecarPreview));
            SaveSettings();
        }
    }

    public string SidecarPreview
    {
        get
        {
            const string video = "Film (2024)";
            var ext = DefaultOutputFormat;
            return string.Join(Environment.NewLine, new[]
            {
                SidecarDetector.BuildSidecarName(video, DefaultLanguage, false, false, false, ext),
                SidecarDetector.BuildSidecarName(video, DefaultLanguage, true, false, false, ext),
                SidecarDetector.BuildSidecarName(video, DefaultLanguage, false, true, false, ext),
            });
        }
    }

    // ---------------- Behaviour ----------------

    public bool ShowHints
    {
        get => _s.Config.ShowHints;
        set
        {
            if (_s.Config.ShowHints == value) return;
            _s.Config.ShowHints = value;
            HintService.Current.IsEnabled = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool BringToFrontOnHandoff
    {
        get => _s.Config.BringToFrontOnHandoff;
        set
        {
            if (_s.Config.BringToFrontOnHandoff == value) return;
            _s.Config.BringToFrontOnHandoff = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public bool ShowGuidedTourOnFirstRun
    {
        get => _s.Config.ShowGuidedTourOnFirstRun;
        set
        {
            if (_s.Config.ShowGuidedTourOnFirstRun == value) return;
            _s.Config.ShowGuidedTourOnFirstRun = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // ---------------- Appearance ----------------

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { ThemeService.Dark, ThemeService.Light };

    public string Theme
    {
        get => _s.Config.Theme;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _s.Config.Theme == value) return;
            _s.Config.Theme = value;
            _s.Theme.Apply(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThemeToggleText));
            SaveSettings();
        }
    }

    public string ThemeToggleText => _s.Theme.IsDark ? "Light mode" : "Dark mode";

    // ---------------- Storage ----------------

    public string SettingsFilePath => _s.Settings.FilePath;

    public string SettingsStorageText => _s.Settings.LastSaveError
        ?? "Settings save automatically a moment after each change. Delete this file to reset everything to defaults.";
}
