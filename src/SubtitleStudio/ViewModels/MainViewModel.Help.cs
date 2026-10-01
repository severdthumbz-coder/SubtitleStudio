using SubtitleStudio.Services;

namespace SubtitleStudio.ViewModels;

/// <summary>Help tab: revision history comes straight from the embedded CHANGELOG.md.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<RevisionEntry> RevisionHistory { get; private set; } = Array.Empty<RevisionEntry>();

    public string HandoffExample => $"\"SubtitleStudio v{AppInfo.Version}.exe\" \"D:\\Movies\\Film (2024)\\Film (2024).mkv\"";

    public string SettingsFileName => AppPaths.SettingsFileName;

    private void InitHelp()
    {
        try
        {
            RevisionHistory = ChangelogService.Load();
        }
        catch (Exception)
        {
            RevisionHistory = Array.Empty<RevisionEntry>();
        }
    }
}
