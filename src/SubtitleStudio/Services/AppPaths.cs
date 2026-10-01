using System.Reflection;

namespace SubtitleStudio.Services;

/// <summary>Portable paths and version info. Everything lives next to the EXE.</summary>
public static class AppPaths
{
    public const string SettingsFileName = "subt_settings.json";
    public const string ErrorLogFileName = "subt_errors.log";

    /// <summary>
    /// Folder of the running EXE. For a single-file publish AppContext.BaseDirectory is also the EXE
    /// folder, but Environment.ProcessPath is the most direct answer.
    /// </summary>
    public static string ExeDirectory { get; } = ResolveExeDirectory();

    public static string SettingsFile => Path.Combine(ExeDirectory, SettingsFileName);

    public static string ErrorLogFile => Path.Combine(ExeDirectory, ErrorLogFileName);

    private static string ResolveExeDirectory()
    {
        var processPath = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }
}

public static class AppInfo
{
    public const string ProductName = "Subtitle Studio";

    private static readonly System.Version Parsed = Assembly.GetExecutingAssembly().GetName().Version ?? new System.Version(0, 0, 0, 0);

    /// <summary>Four-part version derived from csproj FullVersion via AssemblyVersion, e.g. "1.0.0.5".</summary>
    public static string Version { get; } = Parsed.ToString(4);

    /// <summary>Display form used in the UI, matching Video Metadata Editor: "v1.0.0 build 5".</summary>
    public static string DisplayVersion { get; } = $"v{Parsed.Major}.{Parsed.Minor}.{Parsed.Build} build {Parsed.Revision}";
}
