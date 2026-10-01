using System.Runtime.InteropServices;
using System.Text;

namespace SubtitleStudio.Services.Transcription;

public enum VulkanDeviceKind { Other = 0, Integrated = 1, Discrete = 2, Virtual = 3, Processor = 4 }

/// <summary>A graphics device as the Vulkan driver lists it (Index: its position in that list).</summary>
public sealed record VulkanDevice(int Index, string Name, VulkanDeviceKind Kind, uint VendorId, uint ApiVersion)
{
    public string KindText => Kind switch
    {
        VulkanDeviceKind.Discrete => "graphics card",
        VulkanDeviceKind.Integrated => "built into the processor",
        VulkanDeviceKind.Processor => "software, runs on the processor",
        VulkanDeviceKind.Virtual => "virtual",
        _ => "other",
    };

    public string Label => $"{Name} ({KindText})";

    /// <summary>Real graphics hardware: whisper.cpp's Vulkan backend only uses these.</summary>
    public bool IsGpu => Kind is VulkanDeviceKind.Discrete or VulkanDeviceKind.Integrated;

    public override string ToString() => Label;
}

public sealed record VulkanReport(IReadOnlyList<VulkanDevice> Devices, string? Problem)
{
    public IEnumerable<VulkanDevice> Gpus => Devices.Where(d => d.IsGpu);

    /// <summary>The device whisper.cpp should use by default: a graphics card first, else a built-in GPU.</summary>
    public VulkanDevice? Best => Gpus.OrderByDescending(d => d.Kind == VulkanDeviceKind.Discrete).FirstOrDefault();
}

/// <summary>
/// Lists the Vulkan devices by asking the Vulkan driver directly (vulkan-1.dll, installed by the
/// graphics driver). whisper.cpp's Vulkan backend would otherwise take every GPU, built-in ones too,
/// and use the first; on a laptop that can be the slow built-in one. Never throws.
/// </summary>
public static class VulkanDevices
{
    private const int VkSuccess = 0;
    private const int StructureTypeInstanceCreateInfo = 1;
    private const int PropertiesBufferSize = 4096; // VkPhysicalDeviceProperties is 824 bytes on x64

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateInstanceFn(IntPtr createInfo, IntPtr allocator, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumeratePhysicalDevicesFn(IntPtr instance, ref uint count, IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GetPhysicalDevicePropertiesFn(IntPtr device, IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DestroyInstanceFn(IntPtr instance, IntPtr allocator);

    public static string LibraryName => OperatingSystem.IsWindows() ? "vulkan-1.dll" : "libvulkan.so.1";

    public static VulkanReport List()
    {
        if (!NativeLibrary.TryLoad(LibraryName, out var lib))
            return new VulkanReport(Array.Empty<VulkanDevice>(), "Vulkan isn't installed (no vulkan-1.dll): the graphics driver provides it. Whisper runs on the processor.");
        IntPtr info = IntPtr.Zero, instance = IntPtr.Zero, handles = IntPtr.Zero, props = IntPtr.Zero;
        DestroyInstanceFn? destroy = null;
        try
        {
            var create = Get<CreateInstanceFn>(lib, "vkCreateInstance");
            var enumerate = Get<EnumeratePhysicalDevicesFn>(lib, "vkEnumeratePhysicalDevices");
            var properties = Get<GetPhysicalDevicePropertiesFn>(lib, "vkGetPhysicalDeviceProperties");
            destroy = Get<DestroyInstanceFn>(lib, "vkDestroyInstance");
            if (create is null || enumerate is null || properties is null || destroy is null)
                return new VulkanReport(Array.Empty<VulkanDevice>(), "The Vulkan driver is incomplete (missing functions).");

            // VkInstanceCreateInfo, all zero except sType: no layers, no extensions, no app info.
            info = Marshal.AllocHGlobal(64);
            for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(info, i, 0);
            Marshal.WriteInt32(info, 0, StructureTypeInstanceCreateInfo);
            int result = create(info, IntPtr.Zero, out instance);
            if (result != VkSuccess || instance == IntPtr.Zero)
                return new VulkanReport(Array.Empty<VulkanDevice>(), $"The Vulkan driver didn't start (error {result}). Whisper runs on the processor.");

            uint count = 0;
            result = enumerate(instance, ref count, IntPtr.Zero);
            if (result != VkSuccess || count == 0)
                return new VulkanReport(Array.Empty<VulkanDevice>(), "Vulkan found no graphics devices. Whisper runs on the processor.");
            handles = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            result = enumerate(instance, ref count, handles);
            if (result < 0)
                return new VulkanReport(Array.Empty<VulkanDevice>(), $"Vulkan couldn't list the graphics devices (error {result}).");

            var devices = new List<VulkanDevice>();
            props = Marshal.AllocHGlobal(PropertiesBufferSize);
            for (int i = 0; i < count; i++)
            {
                var device = Marshal.ReadIntPtr(handles, i * IntPtr.Size);
                properties(device, props);
                devices.Add(Parse(props, i));
            }
            return new VulkanReport(devices, null);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or SEHException or AccessViolationException)
        {
            return new VulkanReport(Array.Empty<VulkanDevice>(), "The Vulkan driver couldn't be asked about its devices: " + ex.Message);
        }
        finally
        {
            if (instance != IntPtr.Zero) destroy?.Invoke(instance, IntPtr.Zero);
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>VkPhysicalDeviceProperties: apiVersion, driverVersion, vendorID, deviceID, deviceType, deviceName[256].</summary>
    internal static VulkanDevice Parse(IntPtr props, int index)
    {
        uint api = (uint)Marshal.ReadInt32(props, 0);
        uint vendor = (uint)Marshal.ReadInt32(props, 8);
        int type = Marshal.ReadInt32(props, 16);
        var name = new byte[256];
        Marshal.Copy(props + 20, name, 0, name.Length);
        int end = Array.IndexOf(name, (byte)0);
        var text = Encoding.UTF8.GetString(name, 0, end < 0 ? name.Length : end).Trim();
        var kind = Enum.IsDefined(typeof(VulkanDeviceKind), type) ? (VulkanDeviceKind)type : VulkanDeviceKind.Other;
        return new VulkanDevice(index, text.Length == 0 ? $"Device {index}" : text, kind, vendor, api);
    }

    private static T? Get<T>(IntPtr lib, string name) where T : Delegate
        => NativeLibrary.TryGetExport(lib, name, out var fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;
}

/// <summary>
/// Sets an environment variable so native libraries loaded later see it: in the process environment
/// (for libraries with their own C runtime, which copy it when they load) and in the shared Windows C
/// runtime's copy (ucrtbase, which libraries built with the default /MD read through getenv).
/// </summary>
public static class NativeEnvironment
{
    [DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_wputenv_s")]
    private static extern int PutEnvironment(string name, string value);

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

    [DllImport("libc", EntryPoint = "unsetenv")]
    private static extern int UnsetUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    public static void Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        if (!OperatingSystem.IsWindows())
        {
            // .NET keeps its own copy of the environment on Linux/macOS; native code reads libc's.
            try
            {
                if (value is null) UnsetUnix(name);
                else SetUnix(name, value, 1);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return;
        }
        try
        {
            PutEnvironment(name, value ?? string.Empty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
