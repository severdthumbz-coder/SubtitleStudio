using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Hardware;

/// <summary>
/// Reads the processor, memory and drives through plain Windows APIs (registry, GlobalMemoryStatusEx,
/// storage IOCTLs): no WMI, no admin rights, nothing to install.
/// </summary>
public static class HardwareDetector
{
    public static HardwareProfile Detect(GpuInfo? gpu)
    {
        string cpu = "Processor";
        try
        {
            using var key = OperatingSystem.IsWindows() ? Registry.LocalMachine?.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0") : null;
            if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
                cpu = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or NullReferenceException)
        {
        }

        ulong total = 0, available = 0;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try
        {
            if (GlobalMemoryStatusEx(ref status))
            {
                total = status.TotalPhys;
                available = status.AvailPhys;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return new HardwareProfile(cpu, Environment.ProcessorCount, total, available, gpu);
    }

    /// <summary>The drive holding <paramref name="path"/> (a file or folder), or null if it can't be read.</summary>
    public static DriveProfile? DriveOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return new DriveProfile(root, DriveKind.Network, 0, 0); // UNC share
            var info = new DriveInfo(root);
            if (!info.IsReady) return null;
            var kind = info.DriveType switch
            {
                DriveType.Network => DriveKind.Network,
                DriveType.CDRom => DriveKind.Optical,
                _ => DriveKind.Unknown,
            };
            string? model = null;
            if (kind == DriveKind.Unknown) (kind, model) = QueryStorage(root);
            return new DriveProfile(root, kind, info.AvailableFreeSpace, info.TotalSize, model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Bus type (NVMe, USB, SATA...) and seek penalty (hard drive vs SSD) of a local volume.</summary>
    private static (DriveKind Kind, string? Model) QueryStorage(string root)
    {
        var letter = root.TrimEnd('\\', ':');
        using var handle = CreateFile($@"\\.\{letter}:", 0, 3 /* read | write sharing */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (DriveKind.Unknown, null);

        int bus = -1;
        string? model = null;
        var buffer = new byte[1024];
        if (Query(handle, 0 /* StorageDeviceProperty */, buffer, out int got) && got >= 32)
        {
            bus = BitConverter.ToInt32(buffer, 28);
            int productOffset = BitConverter.ToInt32(buffer, 16);
            if (productOffset > 0 && productOffset < got)
            {
                int end = Array.IndexOf(buffer, (byte)0, productOffset);
                if (end < 0) end = got;
                model = System.Text.Encoding.ASCII.GetString(buffer, productOffset, end - productOffset).Trim();
            }
        }

        bool? seekPenalty = null;
        var penalty = new byte[16];
        if (Query(handle, 7 /* StorageDeviceSeekPenaltyProperty */, penalty, out int got2) && got2 >= 9)
            seekPenalty = penalty[8] != 0;

        return (Classify(bus, seekPenalty), string.IsNullOrWhiteSpace(model) ? null : model);
    }

    /// <summary>STORAGE_BUS_TYPE + seek penalty → kind. 7 = USB, 17 = NVMe, 12/13 = SD / MMC card.</summary>
    public static DriveKind Classify(int busType, bool? seekPenalty) => busType switch
    {
        7 or 12 or 13 => DriveKind.Usb,
        17 => DriveKind.NvmeSsd,
        _ => seekPenalty switch
        {
            false => DriveKind.Ssd,
            true => DriveKind.HardDrive,
            _ => DriveKind.Unknown,
        },
    };

    private static bool Query(SafeFileHandle handle, int propertyId, byte[] output, out int returned)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery, AdditionalParameters[1] }
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        try
        {
            return DeviceIoControl(handle, 0x002D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, query, query.Length, output, output.Length, out returned, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            returned = 0;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
