using Phalanx.Rendering.RHI;
using Silk.NET.Windowing;

namespace Phalanx.Rendering;

/// <summary>Extra checks of the validation layer (when validation is on) and device robustness, see <see cref="RenderDevice.Validation"/>.</summary>
[Flags]
public enum RenderValidation
{
    None = 0,
    /// <summary>The layer's synchronization validation: missing barriers and cpu/gpu hazards.</summary>
    Synchronization = 1,
    /// <summary>The layer's gpu-assisted validation: out of bounds descriptor indexing and buffer reads in shaders.</summary>
    GpuAssisted = 2,
    /// <summary>robustBufferAccess on the device (out of bounds buffer reads return zero or in-bounds data), with or without validation.</summary>
    RobustBufferAccess = 4,
    /// <summary>The gpu writes begun/completed per pass marker into a host buffer, read back when the device is lost
    /// (spartan: debug.breadcrumbs). Startup only, with or without validation.</summary>
    GpuBreadcrumbs = 8,
    /// <summary>Leaves the device lost diagnostics out (VK_NV_device_diagnostic_checkpoints, VK_EXT_device_fault),
    /// which are on wherever the driver has them; spartan enables neither. A loss is then reported without the pass
    /// the gpu was in.</summary>
    NoDeviceDiagnostics = 16,
}

/// <summary>
/// The public face of the internal Vulkan RHI.
/// No Vulkan types or unsafe code here: those stay in Rendering/RHI
/// </summary>
public static class RenderDevice
{
    /// <summary>What the next device creation enables on top of plain validation (set before Renderer.Initialize).</summary>
    public static RenderValidation Validation
    {
        get => RHI_Device.ValidationFeatures;
        set => RHI_Device.ValidationFeatures = value;
    }

    /// <summary>Whether command lists write gpu timestamps (spartan: cvar_debug_gpu_timing), on by default; lists made
    /// after the change follow it.</summary>
    public static bool GpuTiming
    {
        get => RHI_Device.GpuTiming;
        set => RHI_Device.GpuTiming = value;
    }

    /// <summary>How long the last frame's start waited for the gpu to finish the frame that used its slot, in ms
    /// (frames in flight: near zero while the cpu is the bottleneck).</summary>
    public static double FrameCpuWaitMs => RHI_Device.FrameCpuWaitMs;

    /// <summary>Times the frame and its top level passes on the gpu for <see cref="TakeGpuTimings"/> (off by default;
    /// needs <see cref="GpuTiming"/>).</summary>
    public static bool GpuPassTiming
    {
        get => RHI_GpuTimings.Enabled;
        set => RHI_GpuTimings.Enabled = value;
    }

    /// <summary>Waits for every frame's gpu work at its end, so frames never overlap (off by default; a diagnostic
    /// that takes cross frame races out of a run, at the cost of the cpu/gpu overlap).</summary>
    public static bool SerializeFrames
    {
        get => RHI_Device.SerializeFrames;
        set => RHI_Device.SerializeFrames = value;
    }

    /// <summary>Frees the cached descriptor sets that went unused for a while (on by default, spartan's behaviour);
    /// off keeps them all, a diagnostic.</summary>
    public static bool DescriptorSetEviction
    {
        get => RHI_Device.DescriptorSetEviction;
        set => RHI_Device.DescriptorSetEviction = value;
    }

    /// <summary>The gpu time of the frames completed since the last call, on average, and of their top level passes
    /// (pass markers such as shadow_maps, g_buffer, light, aa_upscale) in the order they first ran; resets the sums.
    /// Empty when gpu timing is off or unsupported.</summary>
    public static (int Frames, double FrameMs, IReadOnlyList<(string Pass, double Ms)> Passes) TakeGpuTimings()
    {
        (int frames, double frame_ms, List<(string, double)> passes) = RHI_GpuTimings.TakeAverages();
        return (frames, frame_ms, passes);
    }

    /// <summary>Whether a Vulkan loader is present and reports at least one 1.3 capable device (creates a throwaway instance).</summary>
    public static bool IsVulkanAvailable() => RHI_Device.Probe(out _);

    /// <summary>Same as <see cref="IsVulkanAvailable"/>, with the reason when it is not.</summary>
    public static bool IsVulkanAvailable(out string reason) => RHI_Device.Probe(out reason);

    /// <summary>
    /// Opens the RHI on <paramref name="window"/> (as created by <c>Phalanx.Platform.Window</c>; initialized here if needed),
    /// clears the back buffer to the colour every frame and presents, until the window closes or
    /// <paramref name="maxFrames"/> frames were presented (0 = until closed). Returns the number of presented frames.
    /// <paramref name="resizeEvery"/> &gt; 0 toggles the window between its size and half of it every that many frames
    /// (resize demo: the swapchain follows the platform resize event).
    /// </summary>
    public static int RunClearLoop(IWindow window, float r, float g, float b, float a, int maxFrames = 0, bool validation = false, int resizeEvery = 0) =>
        RHI_ClearSample.RunClearLoop(window, r, g, b, a, maxFrames, validation, resizeEvery);

    /// <summary>
    /// Headless check: creates a device without a window, clears a 4x4 offscreen image through dynamic rendering,
    /// copies it back and returns the RGBA8 bytes of the first pixel.
    /// </summary>
    public static byte[] ClearOffscreenAndReadPixel(float r, float g, float b, float a, bool validation = false) =>
        RHI_ClearSample.ClearOffscreenAndReadPixel(r, g, b, a, validation, out _);

    internal static byte[] ClearOffscreenAndReadPixel(float r, float g, float b, float a, bool validation, out IReadOnlyList<string> validationMessages) =>
        RHI_ClearSample.ClearOffscreenAndReadPixel(r, g, b, a, validation, out validationMessages);

    /// <summary>
    /// Opens the RHI on <paramref name="window"/> and draws the triangle sample every frame, until the window closes or
    /// <paramref name="maxFrames"/> frames were presented (0 = until closed). Returns the number of presented frames.
    /// </summary>
    public static int RunTriangleLoop(IWindow window, int maxFrames = 0, bool validation = false) =>
        RHI_SampleHost.RunWindowed(window, () => new RHI_TriangleSample(), maxFrames, validation);

    /// <summary>Same as <see cref="RunTriangleLoop"/> with the rotating textured cube sample.</summary>
    public static int RunTexturedCubeLoop(IWindow window, int maxFrames = 0, bool validation = false) =>
        RHI_SampleHost.RunWindowed(window, () => new RHI_TexturedCubeSample(), maxFrames, validation);

    /// <summary>
    /// Headless: draws one frame of the triangle sample into a <paramref name="width"/> x <paramref name="height"/>
    /// offscreen image and returns its RGBA8 pixels (row major).
    /// </summary>
    public static byte[] RenderTriangleOffscreen(uint width = 64, uint height = 64, bool validation = false) =>
        RHI_SampleHost.RenderOffscreen(() => new RHI_TriangleSample(), width, height, 0.0f, validation, out _);

    /// <summary>Same as <see cref="RenderTriangleOffscreen"/> with the textured cube sample at <paramref name="time"/> seconds.</summary>
    public static byte[] RenderTexturedCubeOffscreen(uint width = 64, uint height = 64, float time = 0.0f, bool validation = false) =>
        RHI_SampleHost.RenderOffscreen(() => new RHI_TexturedCubeSample(), width, height, time, validation, out _);
}
