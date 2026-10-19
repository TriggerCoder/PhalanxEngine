// The views of the renderer (SceneView, ViewState, ViewHistory in Views/): what used to be one set of statics per
// renderer (the frame constants, the jitter, the taau and depth history, the screen space shadows, the per camera
// render targets, the directional cascades) is one set per view, reached through CurrentView while a view renders.
// Spartan (source/rendering/Renderer.cpp @ 4c07baa, :127-129, view() :5720, GetViewRenderTarget :5726-5738; the same at
// upstream 67a6e8b) has a primary and a preview ViewState with an active_view pointer, but only frame_output and the
// exposure textures are per view and a secondary view takes over a whole frame.
// Deviations:
// - Every camera dependent target is per view (the g-buffer, the light buffers, frame_render, the reactivity mask, the
//   depth and its history, the taau history and its ping-pong target, the screen space shadows, frame_output); the
//   shadow atlas, the sky, the luts and the ibl stay shared by the frame. Targets that live within one view's frame come
//   from a pool shared by views of the same size (RenderTargetPool).
// - The directional shadow cascades are per view (spartan: computed in the Light from the one camera); the atlas is
//   packed once per frame with one pair of cascades per rendered view.
// - The light table is composed per view: the shared entries plus the view's directional cascades and contact shadow
//   slices. There is one light buffer, the main view's table goes into it.
// - Only MainView renders. More views can be created (AddView, internal until split-screen) and own their state and
//   targets, but are neither rendered nor composited yet: a second light buffer (or a per view cascade binding), the
//   per view draw data and the composite come with split-screen.
// - Shared values that depend on a camera use the main view's: the sky bake's camera position, the sun colour's
//   altitude (Light.GetColorEffective), the shadow distance of point and spot lights (Light.IsShadowEffective) and the
//   anisotropic sampler's mip bias.
using System.Drawing;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using static Phalanx.Rendering.Renderer_Definitions;
using Texture      = Phalanx.Rendering.RHI.RHI_Texture;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using RenderView   = Phalanx.World.RenderView;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    private static readonly SceneView s_main_view = new("main", is_main: true);
    private static readonly List<SceneView> s_views = new() { s_main_view };
    private static readonly RenderTargetPool s_target_pool = new();
    private static SceneView? s_current_view;

    /// <summary>The view of the world camera, full screen. It always exists; its targets live from Initialize to Shutdown.</summary>
    public static SceneView MainView => s_main_view;

    /// <summary>Every view, in <see cref="SceneView.Order"/>.</summary>
    public static IReadOnlyList<SceneView> Views => s_views;

    /// <summary>The view being prepared or recorded during <see cref="Tick"/> (Unity's Camera.current), null outside it.</summary>
    public static SceneView? CurrentView => s_current_view;

    // the view the per view state is read from: the one rendering, else the main view (tests and tools read the main
    // view's targets after a frame)
    private static ViewState ActiveViewState => (s_current_view ?? s_main_view).State;

    internal static RenderTargetPool GetRenderTargetPool() => s_target_pool;

    // the views this frame renders: only the main view until split-screen composites the others
    private static IEnumerable<SceneView> RenderedViews() { yield return s_main_view; }

    /// <summary>A view with its own camera, history and targets at the given resolutions. It isn't rendered yet
    /// (split-screen comes later), see the header.</summary>
    internal static SceneView AddView(string name, Camera? camera, RectangleF screen_rect, uint render_width, uint render_height, uint output_width, uint output_height)
    {
        if (!s_initialized)
            throw new InvalidOperationException("The renderer is not initialized");
        var view = new SceneView(name, is_main: false) { Camera = camera, ScreenRect = screen_rect, Order = s_views.Count };
        view.State.ResetFrameState();
        SetViewResolution(view, render_width, render_height, output_width, output_height, recreate: false);
        CreateViewTargets(view, true, true);
        s_views.Add(view);
        s_views.Sort((a, b) => a.IsMain != b.IsMain ? (a.IsMain ? -1 : 1) : a.Order.CompareTo(b.Order));
        return view;
    }

    /// <summary>Removes <paramref name="view"/> and releases its targets (the main view can't be removed).</summary>
    public static void RemoveView(SceneView view)
    {
        if (view.IsMain)
            throw new InvalidOperationException("The main view can't be removed");
        if (!s_views.Remove(view))
            return;
        ReleaseViewTargets(view);
        view.State.ResetFrameState();
    }

    /// <summary>Resizes <paramref name="view"/>: its targets of that size are created again and its history starts over,
    /// the other views keep theirs. The main view follows SetResolutionRender / SetResolutionOutput.</summary>
    internal static void ResizeView(SceneView view, uint render_width, uint render_height, uint output_width, uint output_height)
    {
        if (view.IsMain)
        {
            SetResolutionRender(render_width, render_height);
            SetResolutionOutput(output_width, output_height);
            return;
        }
        SetViewResolution(view, render_width, render_height, output_width, output_height, recreate: s_initialized);
    }

    // Shutdown: secondary views go away, the main view keeps its settings but releases its targets
    private static void DestroyViews()
    {
        foreach (SceneView view in s_views.ToArray())
        {
            if (!view.IsMain)
                s_views.Remove(view);
            ReleaseViewTargets(view);
            view.State.ResetFrameState();
        }
        s_current_view = null;
    }

    // true when the resolution changed; with recreate the view's targets of that size are created again, which resets
    // its history (Renderer.cpp:1720) and no other view's
    private static bool SetViewResolution(SceneView view, uint render_width, uint render_height, uint output_width, uint output_height, bool recreate)
    {
        ViewState s = view.State;
        bool render_changed = render_width != s.RenderWidth || render_height != s.RenderHeight;
        bool output_changed = output_width != s.OutputWidth || output_height != s.OutputHeight;
        if (!render_changed && !output_changed)
            return false;
        s.RenderWidth  = render_width;
        s.RenderHeight = render_height;
        s.OutputWidth  = output_width;
        s.OutputHeight = output_height;
        if (recreate && s.HasTargets)
            CreateViewTargets(view, render_changed, output_changed);
        return true;
    }

    // the per view half of spartan's CreateRenderTargets (Renderer_Resources.cpp:631), see Renderer_Resources.cs
    private static void CreateViewTargets(SceneView view, bool create_render, bool create_output)
    {
        ViewState s = view.State;
        const RHI_Texture_Flags flags         = RHI_Texture_Flags.RHI_Texture_Uav | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit;
        const RHI_Texture_Flags gbuffer_flags = RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit;
        uint rw = s.RenderWidth, rh = s.RenderHeight, ow = s.OutputWidth, oh = s.OutputHeight;

        void Publish(RenderTarget type, uint width, uint height)
        {
            if (view.IsMain)
                RenderView.SetRenderTargetSize(type, width, height);
        }
        void Transient(RenderTarget type, uint width, uint height, RHI_Format format, RHI_Texture_Flags texture_flags)
        {
            s_target_pool.Release(s.GetTransient(type)); // the deletion queue keeps it alive for frames in flight
            s.SetTransient(type, s_target_pool.Acquire(new RenderTargetDesc(RHI_Texture_Type.Type2D, width, height, 1, 1, format, texture_flags, type.ToString()), shareable: true));
            Publish(type, width, height);
        }
        void History(RenderTarget type, RHI_Texture_Type texture_type, uint width, uint height, uint depth, RHI_Format format, RHI_Texture_Flags texture_flags)
        {
            s_target_pool.Release(s.History.Get(type));
            s.History.Set(type, s_target_pool.Acquire(new RenderTargetDesc(texture_type, width, height, depth, 1, format, texture_flags, type.ToString()), shareable: false));
            Publish(type, width, height);
        }

        if (create_render)
        {
            // g-buffer (Renderer_Resources.cpp:758-766)
            Transient(RenderTarget.gbuffer_color,    rw, rh, RHI_Format.R8G8B8A8_Unorm,     gbuffer_flags);
            Transient(RenderTarget.gbuffer_normal,   rw, rh, RHI_Format.R16G16B16A16_Float, gbuffer_flags);
            Transient(RenderTarget.gbuffer_material, rw, rh, RHI_Format.R8G8B8A8_Unorm,     gbuffer_flags);
            Transient(RenderTarget.gbuffer_emissive, rw, rh, RHI_Format.R11G11B10_Float,    gbuffer_flags);
            Transient(RenderTarget.gbuffer_velocity, rw, rh, RHI_Format.R16G16B16A16_Float, gbuffer_flags);
            Transient(RenderTarget.dlss_reactivity,  rw, rh, RHI_Format.R8G8B8A8_Unorm,     flags);
            History(RenderTarget.gbuffer_depth,      RHI_Texture_Type.Type2D, rw, rh, 1, RHI_Format.D32_Float, gbuffer_flags);
            // the temporal passes test disocclusion against the prior depth (:770); it starts cleared, see Pass_Depth_Prepass
            History(RenderTarget.gbuffer_depth_previous, RHI_Texture_Type.Type2D, rw, rh, 1, RHI_Format.D32_Float, gbuffer_flags);
            s.History.DepthHistoryCleared = false;
            Transient(RenderTarget.frame_render,     rw, rh, RHI_Format.R16G16B16A16_Float, flags);
            // light (Renderer_Resources.cpp:809-810), rgba16f instead of r11g11b10, see the deviation in Renderer_Resources.cs
            Transient(RenderTarget.light_diffuse,    rw, rh, LightBufferFormat, flags);
            Transient(RenderTarget.light_specular,   rw, rh, LightBufferFormat, flags);
            // screen space shadows (Renderer_Resources.cpp:812, upstream :873), a slice per light, without
            // RHI_Texture_ConcurrentSharing (no async compute)
            History(RenderTarget.sss, RHI_Texture_Type.Type2DArray, rw, rh, renderer_sss_slice_count, RHI_Format.R16_Float,
                    RHI_Texture_Flags.RHI_Texture_Uav | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit);
            s.History.SssNeedsClear = true;
        }
        if (create_output)
        {
            // Renderer_Resources.cpp:848-849, frame_output_2 is the post process ping-pong target
            History(RenderTarget.frame_output, RHI_Texture_Type.Type2D, ow, oh, 1, RHI_Format.R16G16B16A16_Float, flags);
            // :866, the mono history; Pass_AA_Upscale swaps it with frame_output_2 every taau frame, so both need the
            // same size and format. Both are rgba32f (spartan: rgba16f), see the deviation in Renderer_Resources.cs.
            History(RenderTarget.frame_output_2, RHI_Texture_Type.Type2D, ow, oh, 1, TaauHistoryFormat, flags);
            History(RenderTarget.taau_history,   RHI_Texture_Type.Type2D, ow, oh, 1, TaauHistoryFormat, flags);
        }

        s.HasTargets = true;
        // new targets hold no history (Renderer.cpp:1720, SetResolution resets it with the targets)
        s.History.TaauResetPending = true;
    }

    private static void ReleaseViewTargets(SceneView view)
    {
        ViewState s = view.State;
        for (int i = 0; i < (int)RenderTarget.max; i++)
        {
            var type = (RenderTarget)i;
            if (ViewState.IsTransientTarget(type))
            {
                s_target_pool.Release(s.GetTransient(type));
                s.SetTransient(type, null);
            }
            else if (ViewHistory.IsHistoryTarget(type))
            {
                s_target_pool.Release(s.History.Get(type));
                s.History.Set(type, null);
            }
        }
        s.HasTargets = false;
    }

    // whether the view resolves with taau this frame
    private static bool IsTaauActive(SceneView view) =>
        AntiAliasingUpsampling == Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau && (view.Features & ViewFeatures.Taau) != 0;

    // starts recording a view: it becomes CurrentView and its frame constants are uploaded
    private static void BeginView(RHI_CommandList cmd, SceneView view)
    {
        s_current_view = view;
        UpdateFrameConstantBuffer(cmd, view.State);
    }

    private static void EndView() => s_current_view = null;
}
