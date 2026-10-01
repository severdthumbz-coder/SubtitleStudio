using System.Runtime.InteropServices;

namespace SubtitleStudio.Infrastructure;

internal static class NativeMethods
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ / Windows 11).</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Pre-20H1 builds of Windows 10 used attribute 19 for the same thing.</summary>
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    internal const int ASFW_ANY = -1;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int processId);
}
