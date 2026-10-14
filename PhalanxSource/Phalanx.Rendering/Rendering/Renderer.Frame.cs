// Port of the frame loop of SpartanEngine source/rendering/Renderer.cpp @ 4c07baa: Initialize (:577-678),
// Shutdown (:680-724), Tick (:726-1060), TickAdvanceFrameConstantBufferRing (:1112), UpdateFrameConstantBuffer
// (:2281) with UpdateFrameCb_ScalarFields (:1916) and SetStandardResources (:5202), plus the resolution setters.
// Deviations:
// - Initialize takes the window (spartan reads its static Window) or, headless, an output size: then there is no
//   swapchain, the render resolution equals the output and the frame stays in frame_output for readback.
// - The swapchain image is acquired once per frame before BeginFrame. Spartan acquires again before the back buffer
//   blit (:984); the second acquire is a no-op there, Phalanx's acquire would take a second image.
// - The rhi has no pipeline-bound callback (spartan: SetPipelineBoundCallback -> SetStandardResources, :584), so the
//   passes call SetStandardResources right after SetPipelineState. Default push constants are a PassBufferData the
//   passes fill and push themselves (spartan: SetDefaultPushConstants).
// - The frame constant buffer gets the camera and projection history (:1789), the projection jitter (:1817, the
//   taau halton path; XeSS/DLSS and xr are not ported) and the view projection and camera fields (:1885) besides
//   the scalars. Cluster lighting, feature bits, stereo, radial blur hubs, the emissive pool and the orthographic
//   projection come with the passes that read them. Without a camera the matrices stay identity.
// - The halton phase is a field reset by Initialize (spartan: a function-local static that never resets).
// - The output resolution follows the swapchain when the window is resized (spartan: OnResize from the window event).
// - Frames in flight are throttled by RHI_Device.BeginFrame (FramesInFlight timeline values), which is what
//   RotateFrameBuffers' slot wait does in spartan; the per-slot gpu-driven buffers it rotates don't exist yet.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Rendering.RHI;
using Silk.NET.Windowing;
using static Phalanx.Rendering.Renderer_Definitions;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using RenderView   = Phalanx.World.RenderView;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    private static bool s_initialized;
    private static ulong s_frame_num;
    private static uint s_frame_cb_ring_slot;
    private static uint s_resolution_render_width, s_resolution_render_height;
    private static uint s_resolution_output_width, s_resolution_output_height;
    private static FrameBufferData s_cb_frame;
    private static uint s_temporal_frame;
    private static readonly System.Diagnostics.Stopwatch s_clock = new();
    private static double s_time_last;
    private static uint s_jitter_phase_index;
    private static Vector2 s_jitter_offset;

    /// <summary>spartan's r.antialiasing_upsampling (default taau, ConsoleVariables.cpp:189); only taau jitters so far.</summary>
    public static Renderer_AntiAliasing_Upsampling AntiAliasingUpsampling { get; set; } = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;

    /// <summary>Whether <see cref="Initialize(IWindow, bool)"/> (or the headless overload) ran and <see cref="Shutdown"/> did not.</summary>
    public static bool IsInitialized => s_initialized;

    /// <summary>Frames rendered since <see cref="Initialize(IWindow, bool)"/>.</summary>
    public static ulong GetFrameNumber() => s_frame_num;

    public static (uint width, uint height) GetResolutionRender() => (s_resolution_render_width, s_resolution_render_height);
    public static (uint width, uint height) GetResolutionOutput() => (s_resolution_output_width, s_resolution_output_height);

    /// <summary>Gamma written to the frame constant buffer (spartan's r.gamma, default the display gamma 2.2).</summary>
    public static float Gamma { get; set; } = 2.2f;

    /// <summary>
    /// Creates the device on <paramref name="window"/> (initialized here if needed), its swapchain and the renderer's
    /// resources. The output resolution is the window's, the render resolution 1920x1080 like spartan.
    /// Spartan does not derive the render resolution from the window or display: Renderer::Initialize sets the output
    /// to Window::GetWidthInPixels/GetHeightInPixels and the render to a fixed 1920x1080 (Renderer.cpp:604-607 @ 4c07baa,
    /// :674-677 upstream 67a6e8b, "lower than output so taau works well"); only a saved settings file
    /// (Settings.cpp:384-408) or the editor's render options change it. With a window smaller than 1080p (or not 16:9)
    /// the render target is larger than (or a different aspect from) the output and TAAU downsamples; that is spartan's
    /// behaviour and is kept.
    /// </summary>
    public static void Initialize(IWindow window, bool validation = false)
    {
        if (!window.IsInitialized)
            window.Initialize();
        InitializeCore(RHI_ClearSample.GetWindowExtensions(window), validation,
                       (uint)System.Math.Max(window.FramebufferSize.X, 0), (uint)System.Math.Max(window.FramebufferSize.Y, 0),
                       renderer_resolution_render_width, renderer_resolution_render_height, window);
    }

    /// <summary>Headless: creates the device without a window and renders <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static void Initialize(uint width, uint height, bool validation = false) =>
        InitializeCore(Array.Empty<string>(), validation, width, height, width, height, null);

    private static void InitializeCore(IReadOnlyList<string> extensions, bool validation, uint output_width, uint output_height,
                                       uint render_width, uint render_height, IWindow? window)
    {
        if (s_initialized)
            throw new InvalidOperationException("The renderer is already initialized");

        RHI_Device.Initialize(extensions, validation);
        try
        {
            SetResolutionOutputInternal(output_width, output_height, recreate: false);
            SetResolutionRenderInternal(render_width, render_height, recreate: false);
            SetViewport(output_width, output_height);

            if (window != null)
                RHI_Device.CreateSwapChain(window, RHI_Present_Mode.Fifo, hdr: false, "renderer");

            // spartan creates these on the thread pool while the first frames show the loading screen, the slice is
            // small enough to build inline
            CreateStandardTextures();
            CreateShaders();

            CreateBuffers();
            CreateStandardMeshes();
            CreateStandardMaterials();
            CreateRenderTargets(true, true);
            CreateSamplers();
            UpdateBindlessResources();
        }
        catch
        {
            DestroyResources();
            RHI_Device.Destroy();
            throw;
        }

        s_frame_num          = 0;
        s_frame_cb_ring_slot = 0;
        s_temporal_frame     = 0;
        s_cb_frame           = default;
        s_cb_frame.view = s_cb_frame.projection = s_cb_frame.view_projection = s_cb_frame.view_projection_unjittered = Matrix4x4.Identity;
        s_jitter_phase_index = 0;
        s_jitter_offset      = Vector2.Zero;
        s_clock.Restart();
        s_time_last   = 0.0;
        s_initialized = true;
        Log.LogInfo($"output {output_width}x{output_height}, render {render_width}x{render_height}{(window == null ? ", headless" : "")}");
    }

    public static void Shutdown()
    {
        if (!s_initialized)
            return;
        s_initialized = false;
        RHI_Device.QueueWaitAll();
        DestroyResources();
        GeometryBuffer.Shutdown();
        ResetSceneGpuState();
        RHI_Device.DestroySwapChain();
        RHI_Device.Destroy();
    }

    /// <summary>
    /// Renders one frame: waits for the frame slot, updates the frame constant buffer, runs the passes and, with a
    /// swapchain, blits frame_output to the back buffer and presents. Returns false when nothing rendered (minimized
    /// window, render resolution below 64 pixels).
    /// </summary>
    public static bool Tick()
    {
        if (!s_initialized)
            throw new InvalidOperationException("The renderer is not initialized");

        RHI_SwapChain? swapchain = RHI_Device.GetSwapChain();
        bool resolution_valid = s_resolution_render_width >= renderer_min_render_dimension && s_resolution_render_height >= renderer_min_render_dimension;
        bool can_render = resolution_valid && (swapchain == null || RHI_Device.AcquireSwapChainImage());
        if (!can_render)
            return false;

        // the output follows the swapchain
        if (swapchain != null && (swapchain.GetWidth() != s_resolution_output_width || swapchain.GetHeight() != s_resolution_output_height))
        {
            SetResolutionOutputInternal(swapchain.GetWidth(), swapchain.GetHeight(), recreate: true);
            SetViewport(swapchain.GetWidth(), swapchain.GetHeight());
        }

        RHI_CommandList cmd = RHI_Device.BeginFrame();
        if (s_bindless_samplers_dirty)
            UpdateBindlessResources();

        TickAdvanceFrameConstantBufferRing();
        TickUploadBindlessDependencies(cmd);
        UpdateFrameConstantBuffer(cmd);
        ProduceFrame(cmd);

        if (swapchain is { IsImageAcquired: true })
            RHI_Device.BlitToBackBuffer(cmd, GetRenderTargetTexture(RenderTarget.frame_output)!);
        RHI_Device.EndFrame(); // with an acquired image: prepare for present, submit, present

        s_frame_num++;
        return true;
    }

    public static void SetResolutionRender(uint width, uint height) => SetResolutionRenderInternal(width, height, recreate: s_initialized);
    public static void SetResolutionOutput(uint width, uint height) => SetResolutionOutputInternal(width, height, recreate: s_initialized);

    private static void SetResolutionRenderInternal(uint width, uint height, bool recreate)
    {
        if (width == s_resolution_render_width && height == s_resolution_render_height)
            return;
        s_resolution_render_width  = width;
        s_resolution_render_height = height;
        if (recreate)
        {
            CreateRenderTargets(true, false);
            CreateSamplers(); // the anisotropic mip bias follows the render/output ratio
        }
    }

    private static void SetResolutionOutputInternal(uint width, uint height, bool recreate)
    {
        if (width == s_resolution_output_width && height == s_resolution_output_height)
            return;
        s_resolution_output_width  = width;
        s_resolution_output_height = height;
        if (recreate)
        {
            CreateRenderTargets(false, true);
            CreateSamplers();
        }
    }

    private static void TickAdvanceFrameConstantBufferRing()
    {
        s_frame_cb_ring_slot++;
        if (s_frame_cb_ring_slot == renderer_draw_data_buffer_count)
        {
            s_frame_cb_ring_slot = 0;
            GetBuffer(Renderer_Buffer.ConstantFrame)!.ResetOffset();
        }
    }

    // the frame constant buffer as uploaded this frame (tests)
    internal static ref readonly FrameBufferData GetFrameConstantBuffer() => ref s_cb_frame;

    private static void UpdateFrameConstantBuffer(RHI_CommandList cmd)
    {
        UpdateFrameCb_CameraAndProjectionHistory();
        UpdateFrameCb_ProjectionJitter();
        UpdateFrameCb_ViewProjectionAndCameraFields();
        UpdateFrameCb_ScalarFields();

        GetBuffer(Renderer_Buffer.ConstantFrame)!.Update(cmd, MemoryMarshal.AsBytes(new ReadOnlySpan<FrameBufferData>(in s_cb_frame)));
    }

    private static void UpdateFrameCb_CameraAndProjectionHistory()
    {
        if (GetViewCamera() is { } camera)
        {
            s_cb_frame.view_previous       = s_cb_frame.view;
            s_cb_frame.view                = camera.GetViewMatrix();
            s_cb_frame.view_inverted       = Invert(s_cb_frame.view);
            s_cb_frame.projection_previous = s_cb_frame.projection;
            s_cb_frame.projection          = camera.GetProjectionMatrix();
            s_cb_frame.projection_inverted = Invert(s_cb_frame.projection);
        }
    }

    private static void UpdateFrameCb_ProjectionJitter()
    {
        if (AntiAliasingUpsampling == Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau)
        {
            // halton-2,3 jitter, taau pass uses it to reconstruct sub-pixel detail
            static float halton(uint index, uint @base)
            {
                float result = 0.0f;
                float bk     = 1.0f;
                while (index > 0)
                {
                    bk     /= @base;
                    result += (index % @base) * bk;
                    index  /= @base;
                }
                return result;
            }

            const uint phase_count = 16;
            s_jitter_phase_index   = (s_jitter_phase_index + 1) % phase_count;

            float jx = halton(s_jitter_phase_index + 1, 2) - 0.5f;
            float jy = halton(s_jitter_phase_index + 1, 3) - 0.5f;

            // the render resolution has no dynamic scale yet (spartan: m_resolution_render * GetResolutionScale())
            s_jitter_offset = new Vector2(2.0f * jx / s_resolution_render_width, -2.0f * jy / s_resolution_render_height);
            s_cb_frame.projection *= Matrix4x4.CreateTranslation(s_jitter_offset.X, s_jitter_offset.Y, 0.0f);
        }
        else
        {
            s_jitter_offset = Vector2.Zero;
        }
    }

    private static void UpdateFrameCb_ViewProjectionAndCameraFields()
    {
        s_cb_frame.view_projection_previous = s_cb_frame.view_projection;
        s_cb_frame.view_projection          = s_cb_frame.view * s_cb_frame.projection;
        s_cb_frame.view_projection_inverted = Invert(s_cb_frame.view_projection);

        // the camera view translates by -camera_position, so relative to it only the rotation is left
        Matrix4x4 view_rotation = s_cb_frame.view;
        view_rotation.M41 = 0.0f;
        view_rotation.M42 = 0.0f;
        view_rotation.M43 = 0.0f;
        s_cb_frame.view_projection_previous_relative = s_cb_frame.view_projection_relative;
        s_cb_frame.view_projection_relative          = view_rotation * s_cb_frame.projection;

        if (GetViewCamera() is { } camera)
        {
            s_cb_frame.view_projection_previous_unjittered = s_cb_frame.view_projection_unjittered;
            s_cb_frame.view_projection_unjittered          = s_cb_frame.view * camera.GetProjectionMatrix();
            s_cb_frame.camera_near                         = camera.GetNearPlane();
            s_cb_frame.camera_far                          = camera.GetFarPlane();
            s_cb_frame.camera_position_previous            = s_cb_frame.camera_position;
            s_cb_frame.camera_position                     = camera.GetEntity()!.GetPosition();
            s_cb_frame.camera_forward                      = camera.GetEntity()!.GetForward();
            s_cb_frame.camera_right                        = camera.GetEntity()!.GetRight();
            s_cb_frame.camera_fov                          = camera.GetFovHorizontalRad();
            s_cb_frame.camera_aperture                     = camera.GetAperture();
            s_cb_frame.camera_last_movement_time           = (s_cb_frame.camera_position - s_cb_frame.camera_position_previous).LengthSquared() != 0.0f
                ? (float)s_clock.Elapsed.TotalSeconds : s_cb_frame.camera_last_movement_time;
        }
    }

    private static void UpdateFrameCb_ScalarFields()
    {
        double time = s_clock.Elapsed.TotalSeconds;
        s_cb_frame.resolution_output   = new Vector2(s_resolution_output_width, s_resolution_output_height);
        s_cb_frame.resolution_render   = new Vector2(s_resolution_render_width, s_resolution_render_height);
        s_cb_frame.taa_jitter_previous = s_cb_frame.taa_jitter_current;
        s_cb_frame.taa_jitter_current  = s_jitter_offset;
        s_cb_frame.time                = time;
        s_cb_frame.delta_time          = (float)(time - s_time_last);
        s_cb_frame.frame               = ++s_temporal_frame;
        s_cb_frame.resolution_scale    = (float)s_resolution_render_width / System.Math.Max(s_resolution_output_width, 1u);
        s_cb_frame.hdr_enabled         = RHI_Device.GetSwapChain() is { } swapchain && swapchain.IsHdr() ? 1.0f : 0.0f;
        s_cb_frame.gamma               = Gamma;
        // Renderer.cpp:1963-1974: the camera's exposure, 1 without one; auto exposure isn't ported, so the mode stays manual
        s_cb_frame.camera_exposure      = GetViewCamera() is { } camera ? camera.GetExposure() : 1.0f;
        s_cb_frame.camera_exposure_mode = 0.0f;
        s_time_last = time;
    }

    // spartan's Matrix::Invert of a singular matrix yields garbage, System.Numerics reports it: keep identity then
    private static Matrix4x4 Invert(Matrix4x4 m) => Matrix4x4.Invert(m, out Matrix4x4 inverted) ? inverted : Matrix4x4.Identity;

    // the frame constant buffer, the bindless sets are bound with the pipeline (spartan: SetStandardResources)
    private static void SetStandardResources(RHI_CommandList cmd)
    {
        cmd.SetConstantBuffer((uint)Renderer_BindingsCb.frame, GetBuffer(Renderer_Buffer.ConstantFrame)!);
    }

    // the bindless tables: material textures (unset slots fall back to the checkerboard), material and light
    // parameters, the draw data and the samplers (spartan: TickUploadBindlessDependencies / UpdateBindless*)
    private static void UpdateBindlessResources()
    {
        RHI_Device.UpdateBindlessMaterials(new RHI_Texture?[RHI_Definitions.rhi_max_array_size], GetBuffer(Renderer_Buffer.MaterialParameters));
        RHI_Device.UpdateBindlessLights(GetBuffer(Renderer_Buffer.LightParameters));
        RHI_Device.UpdateBindlessDrawData(GetBuffer(Renderer_Buffer.DrawData));
        RHI_Device.UpdateBindlessSamplers(Array.ConvertAll(s_samplers, s => s!));
        s_bindless_samplers_dirty = false;
    }
}
