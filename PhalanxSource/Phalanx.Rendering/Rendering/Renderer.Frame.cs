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
// - AntiAliasingUpsampling's setter does what spartan's on_antialiasing_change does (ConsoleVariables.cpp:108-132):
//   XeSS and DLSS aren't integrated, so like on a gpu without them the value falls back to AA_Off_Upscale_Linear with
//   a warning, and selecting taau resets its history. FXAA isn't ported yet, it renders like AA_Off_Upscale_Linear.
// - A camera cut (NotifyCameraCut, :4886, applied in UpdateFrameConstantBuffer :2294-2310) resets the taau history and
//   makes the previous camera matrices the current ones. Spartan also cuts when the world unloads (:669-677); Phalanx
//   has no world unload event, it cuts when the view camera changes instead. The frame constants, the jitter, the
//   camera history and the cut are per view (ViewState, Renderer.Views.cs); Renderer.NotifyCameraCut cuts the main
//   view, ResetTaauHistory and a switch to taau reset every view, a resize resets the resized view.
// - resolution_scale is spartan's dynamic resolution scale (GetResolutionScale, :1925), always 1 here (no
//   r.resolution_scale or dynamic resolution yet); get_render_resolution_active in the shaders reads it.
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
    // the per view frame state (frame constants, jitter, camera history, resolution) lives in ViewState, see Renderer.Views.cs
    private static readonly System.Diagnostics.Stopwatch s_clock = new();

    /// <summary>spartan's r.antialiasing_upsampling default (ConsoleVariables.cpp:189).</summary>
    internal const Renderer_AntiAliasing_Upsampling AntiAliasingUpsamplingDefault = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
    private static Renderer_AntiAliasing_Upsampling s_antialiasing_upsampling = AntiAliasingUpsamplingDefault;

    /// <summary>
    /// spartan's r.antialiasing_upsampling (default taau). AA_Taau_Upscale_Taau jitters the projection and resolves
    /// with the temporal upsampler; AA_Off_Upscale_Linear (and AA_Fxaa_Upscale_Linear until fxaa is ported) scales
    /// frame_render to the output linearly. XeSS and DLSS aren't integrated: like spartan on a gpu without them
    /// (on_antialiasing_change, Renderer_ConsoleVariables.cpp:108-132) they log a warning and fall back to off.
    /// </summary>
    public static Renderer_AntiAliasing_Upsampling AntiAliasingUpsampling
    {
        get => s_antialiasing_upsampling;
        set
        {
            if (value == Renderer_AntiAliasing_Upsampling.AA_Xess_Upscale_Xess || value == Renderer_AntiAliasing_Upsampling.AA_Dlss_Upscale_Dlss)
            {
                Log.LogWarning($"{(value == Renderer_AntiAliasing_Upsampling.AA_Xess_Upscale_Xess ? "XeSS" : "DLSS")} isn't supported, anti-aliasing is off");
                value = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            }
            if (value == Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau)
                ResetTaauHistory(); // every view
            s_antialiasing_upsampling = value;
        }
    }

    // the formats of the documented deviations in Renderer_Resources.cs
    internal const RHI_Format TaauHistoryFormat = RHI_Format.R32G32B32A32_Float;
    internal const RHI_Format LightBufferFormat = RHI_Format.R16G16B16A16_Float;

    // Environment.cpp:150-151 at mean distance: asin(695700 km / 1 au) and asin(1737.4 km / 384400 km)
    private const float sun_angular_radius  = 0.0046505f;
    private const float moon_angular_radius = 0.0045198f;

    /// <summary>spartan's r.dlss_reactivity (default 1, ConsoleVariables.cpp:190): how strongly disocclusion and
    /// motion edges in the upscaler reactivity mask reject the taau history, 0 to 1.</summary>
    public static float UpscalerReactivity { get; set; } = 1.0f;

    /// <summary>The next frame starts a new shot (spartan: Renderer::NotifyCameraCut): the taau history is dropped and the
    /// previous camera matrices are set to the current ones, so nothing reprojects across the cut. A change of the view
    /// camera does this by itself. This is the main view's; other views have
    /// <see cref="SceneView.NotifyCameraCut"/>.</summary>
    public static void NotifyCameraCut() => s_main_view.NotifyCameraCut();

    /// <summary>Drops the taau history of every view, the next taau frame starts from the current frame only (spartan:
    /// Renderer::ResetTaauHistory). One view: <see cref="SceneView.ResetHistory"/>.</summary>
    public static void ResetTaauHistory()
    {
        foreach (SceneView view in s_views)
            view.ResetHistory();
    }

    // whether the main view's next taau resolve ignores the history (tests)
    internal static bool IsTaauHistoryResetPending() => s_main_view.State.History.TaauResetPending;

    /// <summary>Whether <see cref="Initialize(IWindow, bool)"/> (or the headless overload) ran and <see cref="Shutdown"/> did not.</summary>
    public static bool IsInitialized => s_initialized;

    /// <summary>Frames rendered since <see cref="Initialize(IWindow, bool)"/>.</summary>
    public static ulong GetFrameNumber() => s_frame_num;

    /// <summary>The main view's render resolution.</summary>
    public static (uint width, uint height) GetResolutionRender() => s_main_view.RenderResolution;
    /// <summary>The main view's output resolution (the swapchain's with a window).</summary>
    public static (uint width, uint height) GetResolutionOutput() => s_main_view.OutputResolution;

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
            CreateRenderTargets();
            CreateViewTargets(s_main_view, true, true);
            CreateSamplers();
            UpdateBindlessResources();
        }
        catch
        {
            DestroyViews();
            DestroyResources();
            RHI_Device.Destroy();
            throw;
        }

        s_frame_num          = 0;
        s_frame_cb_ring_slot = 0;
        s_main_view.State.ResetFrameState();
        s_clock.Restart();
        s_initialized = true;
        Log.LogInfo($"output {output_width}x{output_height}, render {render_width}x{render_height}{(window == null ? ", headless" : "")}, anti-aliasing {AntiAliasingUpsampling}, taau history {TaauHistoryFormat}, light buffers {LightBufferFormat}, skysphere {SkysphereFormat}");
    }

    public static void Shutdown()
    {
        if (!s_initialized)
            return;
        s_initialized = false;
        RHI_Device.QueueWaitAll();
        DestroyViews();
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

        ViewState main = s_main_view.State;
        RHI_SwapChain? swapchain = RHI_Device.GetSwapChain();
        bool resolution_valid = main.RenderWidth >= renderer_min_render_dimension && main.RenderHeight >= renderer_min_render_dimension;
        bool can_render = resolution_valid && (swapchain == null || RHI_Device.AcquireSwapChainImage());
        if (!can_render)
            return false;

        // the output follows the swapchain
        if (swapchain != null && (swapchain.GetWidth() != main.OutputWidth || swapchain.GetHeight() != main.OutputHeight))
        {
            SetResolutionOutputInternal(swapchain.GetWidth(), swapchain.GetHeight(), recreate: true);
            SetViewport(swapchain.GetWidth(), swapchain.GetHeight());
        }

        RHI_CommandList cmd = RHI_Device.BeginFrame();
        if (s_bindless_samplers_dirty)
            UpdateBindlessResources();

        TickAdvanceFrameConstantBufferRing();
        PrepareViews(); // every view culls and runs its OnPreDraw callbacks before the uploads and the draws
        TickUploadBindlessDependencies(cmd);
        ProduceFrame(cmd);

        if (swapchain is { IsImageAcquired: true })
            RHI_Device.BlitToBackBuffer(cmd, s_main_view.State.GetTarget(RenderTarget.frame_output)!);
        RHI_Device.EndFrame(); // with an acquired image: prepare for present, submit, present

        s_frame_num++;
        return true;
    }

    /// <summary>The main view's render resolution; its render-sized targets are recreated and its history reset.</summary>
    public static void SetResolutionRender(uint width, uint height) => SetResolutionRenderInternal(width, height, recreate: s_initialized);
    /// <summary>The main view's output resolution (with a window it follows the swapchain).</summary>
    public static void SetResolutionOutput(uint width, uint height) => SetResolutionOutputInternal(width, height, recreate: s_initialized);

    private static void SetResolutionRenderInternal(uint width, uint height, bool recreate)
    {
        if (!SetViewResolution(s_main_view, width, height, s_main_view.State.OutputWidth, s_main_view.State.OutputHeight, recreate))
            return;
        if (recreate)
            CreateSamplers(); // the anisotropic mip bias follows the render/output ratio
    }

    private static void SetResolutionOutputInternal(uint width, uint height, bool recreate)
    {
        if (!SetViewResolution(s_main_view, s_main_view.State.RenderWidth, s_main_view.State.RenderHeight, width, height, recreate))
            return;
        if (recreate)
            CreateSamplers();
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

    // the main view's frame constant buffer as uploaded this frame (tests)
    internal static ref readonly FrameBufferData GetFrameConstantBuffer() => ref s_main_view.State.Cb;

    private static void UpdateFrameConstantBuffer(RHI_CommandList cmd, ViewState v)
    {
        UpdateFrameCb_CameraAndProjectionHistory(v);
        UpdateFrameCb_ProjectionJitter(v);
        UpdateFrameCb_ViewProjectionAndCameraFields(v);
        UpdateFrameCb_ScalarFields(v);

        // Renderer.cpp:2294-2310: after a cut nothing reprojects, the previous matrices are the current ones
        Phalanx.World.Camera? camera = v.View.GetCamera();
        if (camera != v.History.CameraPrevious)
        {
            // the first camera has no history to cut, but the targets don't either
            v.History.CameraCutPending = true;
            v.History.CameraPrevious   = camera;
        }
        if (v.History.CameraCutPending)
        {
            v.History.CameraCutPending = false;
            v.History.TaauResetPending = true;
            v.Cb.view_previous                       = v.Cb.view;
            v.Cb.projection_previous                 = v.Cb.projection;
            v.Cb.view_projection_previous            = v.Cb.view_projection;
            v.Cb.view_projection_previous_unjittered = v.Cb.view_projection_unjittered;
            v.Cb.camera_position_previous            = v.Cb.camera_position;
            v.Cb.taa_jitter_previous                 = v.Cb.taa_jitter_current;
            v.Cb.view_projection_previous_relative   = v.Cb.view_projection_relative;
        }

        GetBuffer(Renderer_Buffer.ConstantFrame)!.Update(cmd, MemoryMarshal.AsBytes(new ReadOnlySpan<FrameBufferData>(in v.Cb)));
    }

    private static void UpdateFrameCb_CameraAndProjectionHistory(ViewState v)
    {
        if (v.View.GetCamera() is { } camera)
        {
            v.Cb.view_previous       = v.Cb.view;
            v.Cb.view                = camera.GetViewMatrix();
            v.Cb.view_inverted       = Invert(v.Cb.view);
            v.Cb.projection_previous = v.Cb.projection;
            v.Cb.projection          = camera.GetProjectionMatrix();
            v.Cb.projection_inverted = Invert(v.Cb.projection);
        }
    }

    private static void UpdateFrameCb_ProjectionJitter(ViewState v)
    {
        if (IsTaauActive(v.View))
        {
            v.JitterPhase = (v.JitterPhase + 1) % TaauJitterPhaseCount;

            // the render resolution has no dynamic scale yet (spartan: m_resolution_render * GetResolutionScale())
            v.Jitter = GetTaauJitter(v.JitterPhase, v.RenderWidth, v.RenderHeight);
            v.Cb.projection *= Matrix4x4.CreateTranslation(v.Jitter.X, v.Jitter.Y, 0.0f);
        }
        else
        {
            v.Jitter = Vector2.Zero;
        }
    }

    // Renderer.cpp:1852
    internal const uint TaauJitterPhaseCount = 16;

    // halton-2,3 jitter, taau pass uses it to reconstruct sub-pixel detail (Renderer.cpp:1836-1866): the ndc offset of
    // phase_index, within half a render pixel each way, y negated (ndc y points up, pixel rows down)
    internal static Vector2 GetTaauJitter(uint phase_index, uint render_width, uint render_height)
    {
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

        float jx = halton(phase_index + 1, 2) - 0.5f;
        float jy = halton(phase_index + 1, 3) - 0.5f;
        return new Vector2(2.0f * jx / render_width, -2.0f * jy / render_height);
    }

    private static void UpdateFrameCb_ViewProjectionAndCameraFields(ViewState v)
    {
        v.Cb.view_projection_previous = v.Cb.view_projection;
        v.Cb.view_projection          = v.Cb.view * v.Cb.projection;
        v.Cb.view_projection_inverted = Invert(v.Cb.view_projection);

        // the camera view translates by -camera_position, so relative to it only the rotation is left
        Matrix4x4 view_rotation = v.Cb.view;
        view_rotation.M41 = 0.0f;
        view_rotation.M42 = 0.0f;
        view_rotation.M43 = 0.0f;
        v.Cb.view_projection_previous_relative = v.Cb.view_projection_relative;
        v.Cb.view_projection_relative          = view_rotation * v.Cb.projection;

        if (v.View.GetCamera() is { } camera)
        {
            v.Cb.view_projection_previous_unjittered = v.Cb.view_projection_unjittered;
            v.Cb.view_projection_unjittered          = v.Cb.view * camera.GetProjectionMatrix();
            v.Cb.camera_near                         = camera.GetNearPlane();
            v.Cb.camera_far                          = camera.GetFarPlane();
            v.Cb.camera_position_previous            = v.Cb.camera_position;
            v.Cb.camera_position                     = camera.GetEntity()!.GetPosition();
            v.Cb.camera_forward                      = camera.GetEntity()!.GetForward();
            v.Cb.camera_right                        = camera.GetEntity()!.GetRight();
            v.Cb.camera_fov                          = camera.GetFovHorizontalRad();
            v.Cb.camera_aperture                     = camera.GetAperture();
            v.Cb.camera_last_movement_time           = (v.Cb.camera_position - v.Cb.camera_position_previous).LengthSquared() != 0.0f
                ? (float)s_clock.Elapsed.TotalSeconds : v.Cb.camera_last_movement_time;
        }
    }

    private static void UpdateFrameCb_ScalarFields(ViewState v)
    {
        double time = s_clock.Elapsed.TotalSeconds;
        v.Cb.resolution_output   = new Vector2(v.OutputWidth, v.OutputHeight);
        v.Cb.resolution_render   = new Vector2(v.RenderWidth, v.RenderHeight);
        v.Cb.taa_jitter_previous = v.Cb.taa_jitter_current;
        v.Cb.taa_jitter_current  = v.Jitter;
        v.Cb.time                = time;
        v.Cb.delta_time          = (float)(time - v.TimeLast);
        v.Cb.frame               = ++v.TemporalFrame;
        v.Cb.resolution_scale    = 1.0f; // GetResolutionScale(), no dynamic resolution yet
        v.Cb.hdr_enabled         = RHI_Device.GetSwapChain() is { } swapchain && swapchain.IsHdr() ? 1.0f : 0.0f;
        v.Cb.gamma               = Gamma;
        // Renderer.cpp:1963-1974: the camera's exposure, 1 without one; auto exposure isn't ported, so the mode stays manual
        v.Cb.camera_exposure      = v.View.GetCamera() is { } camera ? camera.GetExposure() : 1.0f;
        v.Cb.camera_exposure_mode = 0.0f;
        // Renderer.cpp:2008-2016. The ephemeris (Environment.cpp) isn't ported: the sun's angular radius is its mean
        // value (Environment.cpp:150 at 1 au), there is no moon (pointing down, nothing illuminated) and no equatorial
        // frame for the stars
        ref readonly LightParameters sun = ref GetLightParametersSlot0(v);
        Vector3 sun_direction = (sun.flags & 1u) != 0 ? -Vector3.Normalize(sun.direction) : new Vector3(0.0f, -1.0f, 0.0f);
        v.Cb.time_of_day    = Phalanx.World.World.GetTimeOfDay(false);
        v.Cb.celestial_moon = new Vector4(0.0f, -1.0f, 0.0f, 0.0f);
        v.Cb.celestial_sun  = new Vector4(sun_direction, moon_angular_radius);
        v.Cb.equatorial_x   = new Vector4(1.0f, 0.0f, 0.0f, sun_angular_radius);
        v.Cb.equatorial_y   = new Vector4(0.0f, 1.0f, 0.0f, 0.0f);
        v.Cb.equatorial_z   = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
        v.TimeLast = time;
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
