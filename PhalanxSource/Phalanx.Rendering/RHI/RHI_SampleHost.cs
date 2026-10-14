// Runs the RHI samples (RHI_TriangleSample, RHI_TexturedCubeSample) in a window or offscreen, kept inside RHI because
// it touches Vulkan types (architecture rule 10). RenderDevice forwards here. Not in spartan, whose renderer owns the
// frame loop; the frame itself follows Renderer::Tick (BeginFrame, the passes, EndFrame with present).
using Silk.NET.Windowing;

namespace Phalanx.Rendering.RHI;

internal interface IRHI_Sample : IDisposable
{
    // record the sample into color (or the swapchain) and depth, time is in seconds since the first frame
    void Render(RHI_CommandList cmd, RHI_SwapChain? swapchain, RHI_Texture? color, RHI_Texture depth, float time);
}

internal static class RHI_SampleHost
{
    public const RHI_Format FormatDepth = RHI_Format.D32_Float;

    // spartan's renderer samplers (Renderer::CreateSamplers, Renderer_Resources.cpp:407-460), in Renderer_Sampler order:
    // [0] is the comparison sampler (set 6), [1..8] the regular ones (set 7)
    public static RHI_Sampler[] CreateRendererSamplers()
    {
        var samplers = new RHI_Sampler[RHI_Definitions.rhi_max_sampler_count];
        samplers[0] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Greater, 0.0f, true, 0.0f, "compare_depth"); // reverse-z
        samplers[1] = new RHI_Sampler(RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "point_clamp_edge");
        samplers[2] = new RHI_Sampler(RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "point_clamp_border");
        samplers[3] = new RHI_Sampler(RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Wrap,        RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "point_wrap");
        samplers[4] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "bilinear_clamp_edge");
        samplers[5] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "bilinear_clamp_border");
        samplers[6] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Wrap,        RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "bilinear_wrap");
        samplers[7] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never, 0.0f, false, 0.0f, "trilinear_clamp");
        samplers[8] = new RHI_Sampler(RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Sampler_Address_Mode.Wrap,        RHI_Comparison_Function.Never, 16.0f, false, 0.0f, "anisotropic_wrap");
        return samplers;
    }

    private static RHI_Texture CreateDepth(uint width, uint height) =>
        new(RHI_Texture_Type.Type2D, width, height, 1, 1, FormatDepth, RHI_Texture_Flags.RHI_Texture_Rtv, "sample_depth");

    /// <summary>
    /// Opens the RHI on <paramref name="window"/>, renders the sample every frame and presents, until the window closes or
    /// <paramref name="max_frames"/> frames were presented (0 = until closed). Returns the number of presented frames.
    /// </summary>
    public static int RunWindowed(IWindow window, Func<IRHI_Sample> create, int max_frames, bool validation)
    {
        if (!window.IsInitialized)
            window.Initialize();

        RHI_Device.Initialize(RHI_ClearSample.GetWindowExtensions(window), validation);
        int presented = 0, frames = 0;
        try
        {
            RHI_SwapChain swapchain = RHI_Device.CreateSwapChain(window, RHI_Present_Mode.Fifo, hdr: false, "swapchain");
            using IRHI_Sample sample = create();
            RHI_Texture? depth = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!window.IsClosing && (max_frames == 0 || presented < max_frames))
                {
                    frames++;
                    window.DoEvents();
                    if (window.IsClosing)
                        break;

                    if (!RHI_Device.AcquireSwapChainImage())
                    {
                        if (max_frames > 0 && frames > max_frames * 4) break; // minimized for too long in a bounded run
                        continue;
                    }

                    // the depth buffer follows the swapchain size
                    if (depth == null || depth.GetWidth() != swapchain.Width || depth.GetHeight() != swapchain.Height)
                    {
                        depth?.Dispose();
                        depth = CreateDepth(swapchain.Width, swapchain.Height);
                    }

                    RHI_CommandList cmd = RHI_Device.BeginFrame();
                    sample.Render(cmd, swapchain, null, depth, (float)clock.Elapsed.TotalSeconds);
                    RHI_Device.EndFrame(); // prepare_for_present + submit + present
                    presented++;
                }
                RHI_Device.QueueWaitAll();
            }
            finally
            {
                depth?.Dispose();
            }
        }
        finally
        {
            RHI_Device.Destroy();
        }
        return presented;
    }

    /// <summary>
    /// Headless: creates a device without a window, renders one frame of the sample into a
    /// <paramref name="width"/> x <paramref name="height"/> RGBA8 target and returns its pixels (row major, 4 bytes each).
    /// </summary>
    public static byte[] RenderOffscreen(Func<IRHI_Sample> create, uint width, uint height, float time, bool validation, out IReadOnlyList<string> validation_messages)
    {
        RHI_Device.Initialize(Array.Empty<string>(), validation);
        try
        {
            return RenderOffscreenOnDevice(create, width, height, time);
        }
        finally
        {
            validation_messages = RHI_Device.ValidationMessages;
            RHI_Device.Destroy();
        }
    }

    // same, on an already initialized device (the tests share one device per test)
    public static byte[] RenderOffscreenOnDevice(Func<IRHI_Sample> create, uint width, uint height, float time)
    {
        using IRHI_Sample sample = create();
        using var color = new RHI_Texture(RHI_Texture_Type.Type2D, width, height, 1, 1, RHI_Format.R8G8B8A8_Unorm,
                                          RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "sample_color");
        using RHI_Texture depth = CreateDepth(width, height);

        RHI_CommandList cmd = RHI_Device.BeginFrame();
        sample.Render(cmd, null, color, depth, time);
        RHI_Device.EndFrame().Wait();
        byte[] pixels = RHI_Device.ReadbackTexture(color, 0, 0);
        RHI_Device.QueueWaitAll();
        return pixels;
    }
}
