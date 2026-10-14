using Phalanx.Rendering.RHI;
using Silk.NET.Windowing;

namespace Phalanx.Rendering;

/// <summary>
/// The public face of the internal Vulkan RHI.
/// No Vulkan types or unsafe code here: those stay in Rendering/RHI
/// </summary>
public static class RenderDevice
{
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
