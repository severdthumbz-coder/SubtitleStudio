using SubtitleStudio.Services.Gpu;

namespace SubtitleStudio.Services.Hardware;

public enum DriveKind
{
    Unknown,
    NvmeSsd,
    Ssd,
    HardDrive,
    Usb,
    Network,
    Optical,
}

/// <summary>A drive (by its root, e.g. "D:\") as it matters for reading and writing video.</summary>
public sealed record DriveProfile(string Root, DriveKind Kind, long FreeBytes, long TotalBytes, string? Model = null)
{
    public string KindText => Kind switch
    {
        DriveKind.NvmeSsd => "NVMe SSD",
        DriveKind.Ssd => "SSD",
        DriveKind.HardDrive => "hard drive",
        DriveKind.Usb => "USB drive",
        DriveKind.Network => "network drive",
        DriveKind.Optical => "optical drive",
        _ => "drive",
    };

    /// <summary>Network, USB and optical drives can stall or crawl during a long read or write.</summary>
    public bool IsSlowOrRemote => Kind is DriveKind.Network or DriveKind.Usb or DriveKind.Optical;

    public string Summary => $"{Root.TrimEnd('\\')} {KindText}{(Model is { Length: > 0 } m ? $" ({m})" : string.Empty)}, {HardwareProfile.Bytes(FreeBytes)} free of {HardwareProfile.Bytes(TotalBytes)}";
}

/// <summary>The PC as far as the heavy work (decoding, text search, fill, AI, encoding) is concerned.</summary>
public sealed record HardwareProfile(string CpuName, int LogicalCores, ulong TotalRamBytes, ulong AvailableRamBytes, GpuInfo? Gpu)
{
    public static string Bytes(double b) => b >= 1L << 40 ? $"{b / (1L << 40):0.#} TB" : b >= 1L << 30 ? $"{b / (1L << 30):0.#} GB" : $"{b / (1L << 20):0} MB";

    public string CpuText => $"{CpuName} ({LogicalCores} threads)";
    public string RamText => $"{Bytes(TotalRamBytes)} RAM ({Bytes(AvailableRamBytes)} free)";

    /// <summary>Graphics memory of the card AI fill runs on (0 when none).</summary>
    public ulong GpuMemoryBytes => Gpu?.Primary is { IsSoftware: false } a && Gpu.Backend != AiBackend.Cpu ? a.DedicatedMemoryBytes : 0;
}

/// <summary>
/// How removal uses this PC: parallel workers, frames per batch (bounded by free memory), and AI
/// patches per model call (bounded by graphics memory). Pure, so it is testable.
/// </summary>
public sealed record PerformancePlan(int Workers, long FrameMemoryBudgetBytes, int AiPatchBatch)
{
    /// <summary>What was decided and why, for the log.</summary>
    public string Summary => $"{Workers} parallel workers, up to {HardwareProfile.Bytes(FrameMemoryBudgetBytes)} of frames in memory, AI patches per call: {AiPatchBatch}";

    public static readonly PerformancePlan Default = new(Math.Max(1, Environment.ProcessorCount), 512L << 20, 4);

    public static PerformancePlan For(HardwareProfile hw)
    {
        int workers = Math.Clamp(hw.LogicalCores, 1, 64);

        // An eighth of the free memory for frames in flight (ffmpeg, the players and the AI model need
        // the rest), between 256 MB and 2 GB. Too much would push Windows into swapping, which is slower
        // than any batch size gains.
        long budget = Math.Clamp((long)(hw.AvailableRamBytes / 8), 256L << 20, 2L << 30);

        // LaMa at 512 x 512 needs roughly 1 GB of graphics memory per patch in a batch, plus the model.
        ulong vram = hw.GpuMemoryBytes;
        int aiBatch = vram == 0 ? 1
            : vram >= 10UL << 30 ? 8
            : vram >= 6UL << 30 ? 6
            : vram >= 3UL << 30 ? 4
            : 2;
        return new PerformancePlan(workers, budget, aiBatch);
    }

    /// <summary>Batches in flight during removal: one being decoded, one searched and filled, one repainted by the AI, one encoded.</summary>
    public const int BatchesInFlight = 4;

    /// <summary>
    /// Frames per batch for a given frame size: one per worker, within the memory budget (shared by the
    /// batches in flight). For AI fill, about four model calls' worth of patches (text frames average
    /// about 2 patches), within the memory budget but not limited to one frame per worker, so calls are
    /// full and the last, padded call of a batch wastes little.
    /// </summary>
    public int FramesPerBatch(long frameBytes, bool ai)
    {
        int byMemory = (int)Math.Clamp(FrameMemoryBudgetBytes / BatchesInFlight / Math.Max(1, frameBytes), 1, 64);
        return ai
            ? Math.Clamp(Math.Min(byMemory, Math.Max(2, AiPatchBatch * 4)), 1, 32)
            : Math.Clamp(Math.Min(Workers, byMemory), 1, 32);
    }

    /// <summary>Rough size of the new video: about the original's (same quality), plus a margin.</summary>
    public static long EstimateOutputBytes(long inputBytes) => (long)(inputBytes * 1.3) + (200L << 20);
}
