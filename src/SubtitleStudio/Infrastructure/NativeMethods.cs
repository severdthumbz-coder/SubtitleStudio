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

    // ---------------- Recycle Bin ----------------

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>
    /// Sends a file to the Recycle Bin. On a drive without one (a network share), Windows asks before
    /// deleting it for good, and "No" leaves it. True only when the file is gone.
    /// </summary>
    internal static bool MoveToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0", // the list ends with two nulls; marshalling adds one
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
        };
        int result = SHFileOperation(ref operation);
        return result == 0 && !operation.fAnyOperationsAborted && !File.Exists(path);
    }
}
