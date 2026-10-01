using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LLama.Native;
using SubtitleStudio.Services.Transcription;

namespace SubtitleStudio.Services.Translation;

/// <summary>
/// Loads llama.cpp (LLamaSharp's native libraries, bundled in the EXE) the way this app needs it: the
/// Vulkan build for a graphics card or the processor build otherwise, chosen by the app (LLamaSharp's own
/// choice runs the outside program "vulkaninfo" to decide; here the app's own Vulkan device list does).
/// The Vulkan build has no processor backend of its own: ggml-cpu is loaded from the processor folder first.
/// Loaded once per run of the app.
/// </summary>
public static class LlamaRuntime
{
    private static readonly object Gate = new();
    private static string? _loaded;

    /// <summary>"vulkan" or the processor build ("avx2", "avx", "noavx") that was loaded, once loaded.</summary>
    public static string? Loaded => _loaded;
    public static bool UsesGpu => _loaded == "vulkan";
    public static int? VulkanIndex { get; private set; }

    public static string? Problem { get; private set; }

    /// <summary>Messages from llama.cpp (text, isWarning).</summary>
    public static Action<string, bool>? Log { get; set; }

    /// <summary>The best processor build this processor can run.</summary>
    public static string CpuVariant()
    {
        if (!RuntimeInformation.ProcessArchitecture.Equals(Architecture.X64)) return "noavx";
        if (Avx2.IsSupported && Fma.IsSupported) return "avx2";
        return Avx.IsSupported ? "avx" : "noavx";
    }

    /// <summary>
    /// Prepares the native library for <paramref name="device"/> (before any LLamaSharp call). Returns
    /// what was loaded. Later calls with another device keep the first choice (a restart switches).
    /// </summary>
    public static string Prepare(WhisperDevice device, string? baseDirectory = null)
    {
        lock (Gate)
        {
            if (_loaded is not null)
            {
                Problem = device.UseGpu && !UsesGpu ? "The translator started on the processor in this session. Restart Subtitle Studio to use the graphics card."
                    : device.UseGpu && device.VulkanIndex != VulkanIndex ? "A different graphics device was chosen after the translator started. Restart Subtitle Studio to switch."
                    : null;
                return _loaded;
            }

            var root = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "runtimes", RuntimeId(), "native");
            string cpu = CpuVariant();
            string prefix = OperatingSystem.IsWindows() ? "" : "lib", ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            string File(string folder, string name) => Path.Combine(root, folder, prefix + name + ext);

            string? chosen = null;
            if (device.UseGpu && System.IO.File.Exists(File("vulkan", "llama")))
            {
                // Only the chosen device is visible to ggml (it would otherwise also take a GPU built into the processor).
                if (device.VulkanIndex is { } index) NativeEnvironment.Set("GGML_VK_VISIBLE_DEVICES", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (TryLoadAll(new[] { File("vulkan", "ggml-base"), File(cpu, "ggml-cpu"), File("vulkan", "ggml-vulkan"), File("vulkan", "ggml"), File("vulkan", "llama") }, out var error))
                {
                    chosen = "vulkan";
                    VulkanIndex = device.VulkanIndex;
                }
                else
                {
                    Problem = "The graphics card version of llama.cpp couldn't be loaded (" + error + "); the translator runs on the processor.";
                }
            }
            if (chosen is null)
            {
                foreach (var variant in new[] { cpu, "avx", "noavx" }.Distinct())
                {
                    if (!System.IO.File.Exists(File(variant, "llama"))) continue;
                    if (TryLoadAll(new[] { File(variant, "ggml-base"), File(variant, "ggml-cpu"), File(variant, "ggml"), File(variant, "llama") }, out var error))
                    {
                        chosen = variant;
                        break;
                    }
                    Problem ??= $"llama.cpp ({variant}) couldn't be loaded: {error}";
                }
            }
            if (chosen is null)
                throw new InvalidOperationException("The translator's llama.cpp library couldn't be loaded. " + (Problem ?? "Its files are missing from the app.")
                    + (OperatingSystem.IsWindows() ? " Installing the Microsoft Visual C++ Redistributable (x64) usually fixes it." : string.Empty));

            var llama = chosen == "vulkan" ? File("vulkan", "llama") : File(chosen, "llama");
            var mtmd = chosen == "vulkan" ? File("vulkan", "mtmd") : File(chosen, "mtmd");
            NativeLibraryConfig.All.WithLibrary(llama, mtmd).WithLogCallback((level, message) =>
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                try { Log?.Invoke(message.TrimEnd(), level is LLamaLogLevel.Warning or LLamaLogLevel.Error); }
                catch (Exception) { } // called from llama.cpp's own code: nothing may escape
            });
            _loaded = chosen;
            return chosen;
        }
    }

    private static string RuntimeId() =>
        OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64") : "linux-x64";

    private static bool TryLoadAll(IEnumerable<string> paths, out string? error)
    {
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path)) { error = Path.GetFileName(path) + " is missing"; return false; }
            if (!NativeLibrary.TryLoad(path, out _)) { error = Path.GetFileName(path) + " couldn't be loaded"; return false; }
        }
        error = null;
        return true;
    }
}
