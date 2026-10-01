using System.Windows;
using System.Windows.Interop;
using SubtitleStudio.Infrastructure;

namespace SubtitleStudio.Services;

/// <summary>
/// Swaps the design-token dictionary (Themes/Tokens.Dark.xaml / Tokens.Light.xaml) at runtime.
/// All control styles reference tokens via DynamicResource, so they re-colour live.
/// </summary>
public sealed class ThemeService
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public string Current { get; private set; } = Dark;

    public bool IsDark => Current == Dark;

    public void Apply(string? theme)
    {
        var name = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SubtitleStudio;component/Themes/Tokens.{name}.xaml", UriKind.Absolute),
        };

        var existing = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Themes/Tokens.", StringComparison.OrdinalIgnoreCase) == true);

        if (existing is null)
            dictionaries.Insert(0, replacement);
        else
            dictionaries[dictionaries.IndexOf(existing)] = replacement;

        Current = name;

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
    }

    /// <summary>
    /// Dark/light native title bar via DWM. Needs a window handle, so call from SourceInitialized.
    /// Silently does nothing on Windows builds that do not support the attribute.
    /// </summary>
    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int useDark = IsDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
    }
}
