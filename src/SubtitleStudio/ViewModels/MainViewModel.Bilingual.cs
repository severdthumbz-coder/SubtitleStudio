using SubtitleStudio.Infrastructure;
using SubtitleStudio.Models;
using SubtitleStudio.Services;
using SubtitleStudio.Services.Subtitles;
using SubtitleStudio.Services.Transcription;
using SubtitleStudio.Services.Translation;

namespace SubtitleStudio.ViewModels;

/// <summary>A reading-speed limit to choose in Settings (0: by language).</summary>
public sealed record ReadingLimitOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A bilingual layout to choose, with when to use it.</summary>
public sealed record BilingualLayoutOption(BilingualLayout Layout, string Id, string Label, string Description)
{
    public override string ToString() => Label;
}

/// <summary>
/// Subtitles tab: reading speed (lines too fast to read, and lengthening them into the free time around
/// them) and saving a translation with its original in one file (Services/Subtitles/ReadingSpeed and
/// BilingualSubtitles).
/// </summary>
public sealed partial class MainViewModel
{
    private int _tooFastCount;
    private RelayCommand? _fixReadingSpeedCommand;
    private RelayCommand? _saveBilingualCommand;

    // ---------------- Reading speed ----------------

    /// <summary>The language the open subtitles are read in: their language tag, else what the letters say.</summary>
    private string? ReadingLanguage => EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0]
        : SubtitleTranslationPrompt.ScriptLanguage(Cues.Select(r => r.Text));

    /// <summary>Characters a second: the one chosen in Settings, else the usual limit for the language.</summary>
    private double ReadingLimit => _s.Config.ReadingSpeedLimit > 0 ? _s.Config.ReadingSpeedLimit : ReadingSpeed.LimitFor(ReadingLanguage);

    public int TooFastCount
    {
        get => _tooFastCount;
        private set
        {
            if (SetProperty(ref _tooFastCount, value))
            {
                OnPropertyChanged(nameof(IssueSummary));
                OnPropertyChanged(nameof(HasTooFast));
                RelayCommand.Refresh();
            }
        }
    }

    public bool HasTooFast => _tooFastCount > 0;

    /// <summary>"Reading speed: up to 12 characters a second (Korean)."</summary>
    public string ReadingSpeedText
    {
        get
        {
            if (!_s.Config.CheckReadingSpeed) return "Reading speed isn't checked (turn it on in Settings).";
            var language = ReadingLanguage;
            var why = _s.Config.ReadingSpeedLimit > 0 ? "set in Settings"
                : language is null ? "the usual limit when the language isn't known" : WhisperLanguages.NameOf(language);
            return $"Reading speed: up to {ReadingLimit:0.#} characters a second ({why}).";
        }
    }

    public RelayCommand FixReadingSpeedCommand => _fixReadingSpeedCommand ??= new RelayCommand(FixReadingSpeed, () => HasTooFast);

    private void FixReadingSpeed()
    {
        if (_doc is null || TooFastCount == 0) return;
        PushUndo();
        var ordered = CueOperations.SortedByTime(Cues.Select(r => r.Cue));
        var result = ReadingSpeed.Fix(ordered, ReadingLanguage, ReadingLimit);
        AfterTimingChange();
        var left = result.Improved + result.NoRoom;
        SetStatus($"Lengthened {result.Fixed + result.Improved} line{(result.Fixed + result.Improved == 1 ? "" : "s")} into the free time around {(result.Fixed + result.Improved == 1 ? "it" : "them")}; {result.Fixed} now read in time."
                  + (left > 0 ? $" {left} still too fast with no more room: shorten the text or merge with a neighbour (Next problem finds them)." : string.Empty),
            left > 0 ? StatusKind.Warning : StatusKind.Success);
    }

    public IReadOnlyList<ReadingLimitOption> ReadingLimitOptions { get; } = new[]
    {
        new ReadingLimitOption(0, "By language (English 20, Korean 12, Chinese 9, Japanese 4, others 17)"),
        new ReadingLimitOption(12, "12 characters a second (slow)"),
        new ReadingLimitOption(15, "15 characters a second"),
        new ReadingLimitOption(17, "17 characters a second"),
        new ReadingLimitOption(20, "20 characters a second"),
        new ReadingLimitOption(25, "25 characters a second (fast)"),
    };

    public ReadingLimitOption SelectedReadingLimit
    {
        get => ReadingLimitOptions.FirstOrDefault(o => Math.Abs(o.Value - _s.Config.ReadingSpeedLimit) < 0.01) ?? ReadingLimitOptions[0];
        set
        {
            var v = value?.Value ?? 0;
            if (Math.Abs(_s.Config.ReadingSpeedLimit - v) < 0.01) return;
            _s.Config.ReadingSpeedLimit = v;
            OnPropertyChanged();
            SaveSettings();
            AfterReadingSettingChange();
        }
    }

    public bool CheckReadingSpeed
    {
        get => _s.Config.CheckReadingSpeed;
        set
        {
            if (_s.Config.CheckReadingSpeed == value) return;
            _s.Config.CheckReadingSpeed = value;
            OnPropertyChanged();
            SaveSettings();
            AfterReadingSettingChange();
        }
    }

    private void AfterReadingSettingChange()
    {
        Revalidate();
        OnPropertyChanged(nameof(ReadingSpeedText));
    }

    // ---------------- Bilingual ----------------

    public IReadOnlyList<BilingualLayoutOption> BilingualLayouts { get; } = new[]
    {
        new BilingualLayoutOption(BilingualLayout.Stacked, "stacked", "Original above the translation (SRT)",
            "Both lines in one subtitle at the bottom, the original first in a softer colour. Plays everywhere: VLC, Plex, Jellyfin, TVs. "
            + "Best for most videos; players that ignore colours show both in white."),
        new BilingualLayoutOption(BilingualLayout.TopAndBottom, "topbottom", "Original at the top of the screen, translation at the bottom (ASS)",
            "The original sits at the top, smaller and in a softer colour; the translation stays at the bottom. Keeps long lines readable and the middle of the picture clear. "
            + "Needs the ASS format: VLC, mpv, MPC-HC and Jellyfin show it as intended; some TVs and Plex apps convert or ignore the positions."),
    };

    public BilingualLayoutOption SelectedBilingualLayout
    {
        get => BilingualLayouts.FirstOrDefault(o => o.Id == _s.Config.BilingualLayout) ?? BilingualLayouts[0];
        set
        {
            var id = value?.Id ?? "stacked";
            if (_s.Config.BilingualLayout == id) return;
            _s.Config.BilingualLayout = id;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public RelayCommand SaveBilingualCommand => _saveBilingualCommand ??= new RelayCommand(SaveBilingual, () => HasReviewOriginal && Cues.Count > 0);

    /// <summary>The bilingual document for the open translation and its original (null without an original).</summary>
    internal SubtitleDocument? BuildBilingual()
    {
        if (_doc is null || !HasReviewOriginal) return null;
        RefreshReview();
        var rows = Cues.ToList();
        var language = EditorLanguage.Length > 0 ? EditorLanguage : null;
        return BilingualSubtitles.Build(rows.Select(r => r.Cue).ToList(), EditorFormat, rows.Select(r => r.SourceText).ToList(), SelectedBilingualLayout.Layout, language);
    }

    /// <summary>"Episode 3.en.ko-en.srt": the video's name, the translation's language, then both.</summary>
    internal string BilingualFileName(BilingualLayout layout)
    {
        // "Episode 3" + ".en" (the translation's language first, so players list it under that language) + ".ko-en".
        var baseName = StripLanguage(Path.GetFileNameWithoutExtension(SuggestedFileName));
        var target = EditorLanguage.Length > 0 ? EditorLanguage.Split('-', '_')[0] : null;
        var name = baseName + (target is null ? string.Empty : "." + target)
                   + (_reviewOriginalLanguage is { } o && target is not null ? $".{o.Split('-', '_')[0]}-{target}" : ".bilingual");
        return name + "." + BilingualSubtitles.FormatOf(layout);
    }

    private void SaveBilingual()
    {
        var layout = SelectedBilingualLayout.Layout;
        var doc = BuildBilingual();
        if (doc is null) return;
        var format = BilingualSubtitles.FormatOf(layout);
        var folder = PairedVideo?.Folder ?? (_doc?.SourcePath is { } sp ? Path.GetDirectoryName(sp) : null);
        var (filter, index) = _s.SubtitleFormats.SaveFilter(format);
        var path = _s.Dialogs.SaveFile("Save with both languages", filter, index, folder, BilingualFileName(layout));
        if (path is null) return;
        try
        {
            _s.SubtitleFormats.Save(doc, path, format);
            _s.Log.Success("Subtitles", $"Saved {Path.GetFileName(path)}: the translation with its original ({SelectedBilingualLayout.Label}), {Cues.Count} lines.");
            SetStatus($"Saved {Path.GetFileName(path)} with both languages. The open subtitles are unchanged.", StatusKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SubtitleFormatException)
        {
            SetStatus($"Couldn't save {Path.GetFileName(path)}: {ex.Message}", StatusKind.Error);
        }
    }
}
