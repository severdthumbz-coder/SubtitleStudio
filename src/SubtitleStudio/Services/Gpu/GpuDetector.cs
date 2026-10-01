using System.Runtime.InteropServices;

namespace SubtitleStudio.Services.Gpu;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Microsoft,
    Other,
}

/// <summary>How AI video work (inpainting) will run on this PC, best first.</summary>
public enum AiBackend
{
    /// <summary>No usable GPU: runs on the processor (slow).</summary>
    Cpu,

    /// <summary>DirectML (Direct3D 12): works on AMD, Intel and NVIDIA GPUs on Windows 10/11.</summary>
    DirectML,

    /// <summary>NVIDIA CUDA. Not used: it needs the CUDA toolkit installed, and DirectML runs well on NVIDIA too.</summary>
    Cuda,
}

/// <param name="AdapterIndex">DXGI enumeration index (what DirectML calls the device id); -1 when unknown.</param>
public sealed record GpuAdapter(string Name, GpuVendor Vendor, uint VendorId, uint DeviceId, ulong DedicatedMemoryBytes, bool IsSoftware, int AdapterIndex = -1)
{
    public string MemoryText => DedicatedMemoryBytes >= 1UL << 30
        ? $"{DedicatedMemoryBytes / (double)(1UL << 30):0.#} GB"
        : $"{DedicatedMemoryBytes / (1024 * 1024)} MB";
}

public sealed record GpuInfo(IReadOnlyList<GpuAdapter> Adapters, GpuAdapter? Primary, AiBackend Backend, string? Error)
{
    public string Summary => Primary is null
        ? Error is null ? "No graphics card found; AI work will run on the processor." : $"Graphics card not detected ({Error}); AI work will run on the processor."
        : $"{Primary.Name} ({Primary.MemoryText}) · {BackendText}";

    public string BackendText => Backend switch
    {
        AiBackend.Cuda => "AI acceleration: CUDA (DirectML also available)",
        AiBackend.DirectML => "AI acceleration: DirectML",
        _ => "AI on the processor (slow)",
    };
}

/// <summary>
/// Lists graphics adapters through DXGI (the same enumeration Direct3D uses), picks the best
/// hardware adapter (most dedicated memory, discrete over integrated) and the AI backend for it.
/// Needs no drivers or SDKs beyond Windows itself.
/// </summary>
public static class GpuDetector
{
    public static GpuVendor VendorFromId(uint vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 or 0x8087 => GpuVendor.Intel,
        0x1414 => GpuVendor.Microsoft, // Basic Render Driver / WARP
        0 => GpuVendor.Unknown,
        _ => GpuVendor.Other,
    };

    /// <summary>DirectML runs on any Direct3D 12 GPU; CUDA is offered on NVIDIA with enough memory.</summary>
    public static AiBackend ChooseBackend(GpuAdapter? adapter)
    {
        if (adapter is null || adapter.IsSoftware) return AiBackend.Cpu;
        // DirectML on every vendor: it ships inside the app. CUDA would need NVIDIA's toolkit installed.
        return adapter.Vendor is GpuVendor.Amd or GpuVendor.Intel or GpuVendor.Nvidia or GpuVendor.Other ? AiBackend.DirectML : AiBackend.Cpu;
    }

    public static GpuAdapter? ChoosePrimary(IEnumerable<GpuAdapter> adapters)
        => adapters
            .Where(a => !a.IsSoftware && a.Vendor != GpuVendor.Microsoft)
            .OrderByDescending(a => a.DedicatedMemoryBytes)
            .FirstOrDefault();

    public static GpuInfo Detect()
    {
        try
        {
            var adapters = EnumerateDxgi();
            var primary = ChoosePrimary(adapters);
            return new GpuInfo(adapters, primary, ChooseBackend(primary), null);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException or MarshalDirectiveException)
        {
            return new GpuInfo(Array.Empty<GpuAdapter>(), null, AiBackend.Cpu, ex.Message);
        }
    }

    // ------------------------------------------------------------------ DXGI interop

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    private static List<GpuAdapter> EnumerateDxgi()
    {
        var list = new List<GpuAdapter>();
        var iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factoryObj));
        var factory = (IDXGIFactory1)factoryObj;
        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var desc));
                    list.Add(new GpuAdapter(
                        desc.Description.Trim(),
                        VendorFromId(desc.VendorId),
                        desc.VendorId,
                        desc.DeviceId,
                        (ulong)desc.DedicatedVideoMemory,
                        (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0,
                        (int)i));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return list;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // COM interface inheritance isn't mapped by .NET interop: every base method is redeclared in
    // vtable order (IDXGIObject -> IDXGIFactory -> IDXGIFactory1). Unused ones are placeholders.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    // IDXGIObject -> IDXGIAdapter -> IDXGIAdapter1
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
