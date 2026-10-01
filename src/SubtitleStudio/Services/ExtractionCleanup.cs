using System.Diagnostics;

namespace SubtitleStudio.Services;

/// <summary>
/// The single-file EXE unpacks its libraries (VLC, ONNX Runtime, ...) into %TEMP%\.net\SubtitleStudio\&lt;id&gt;,
/// a new folder for every build, and .NET never removes the old ones (hundreds of MB each). At start-up
/// the folders left by other builds are deleted, in the background. Only when this copy runs from such a
/// folder, only sibling folders holding Subtitle Studio's own files, and not while another copy of the
/// app is running (an older build still open may not have loaded all its files yet).
/// </summary>
public static class ExtractionCleanup
{
    /// <summary>Files that mark a folder as one of ours.</summary>
    private static readonly string[] Markers = { "SubtitleStudio.dll", "libvlc.dll", "onnxruntime.dll" };

    public sealed record Result(int Deleted, long FreedBytes, int Skipped);

    /// <summary>Deletes the other builds' folders next to <paramref name="currentFolder"/>; null when not applicable.</summary>
    public static Result? Run(string currentFolder, Func<bool>? otherCopyRunning = null)
    {
        var current = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder)));
        var app = current.Parent;
        if (app?.Parent is not { Name: ".net" }) return null; // not unpacked by the single-file host
        if ((otherCopyRunning ?? OtherCopyRunning)()) return null;

        int deleted = 0, skipped = 0;
        long freed = 0;
        foreach (var dir in app.EnumerateDirectories())
        {
            if (string.Equals(dir.FullName, current.FullName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Markers.Any(m => File.Exists(Path.Combine(dir.FullName, m)))) continue;
            long size = 0;
            try
            {
                size = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                dir.Delete(recursive: true);
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++; // in use or protected: tried again next start
            }
        }
        return new Result(deleted, freed, skipped);
    }

    private static bool OtherCopyRunning()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id != self && p.ProcessName.StartsWith("SubtitleStudio", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return false;
    }
}
