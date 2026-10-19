// Spartan's ViewState (source/rendering/Renderer_Internal.h @ 4c07baa, ~:275-290: Cb_Frame, the camera history, the
// jitter, temporal_frame) plus what Phalanx keeps per view on top: the render and output resolution, the transient
// render targets, the history (ViewHistory), the screen space shadow lights, the directional shadow cascades, the draw
// lists and the view's light table.
using System.Numerics;
using Phalanx.World;
using Texture      = Phalanx.Rendering.RHI.RHI_Texture;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

internal sealed class ViewState
{
    public ViewState(SceneView view) => View = view;

    public SceneView View { get; }

    // the frame constant buffer of this view (spartan: ViewState::Cb_Frame) and what builds it
    public FrameBufferData Cb;
    public uint JitterPhase;
    public Vector2 Jitter;
    public uint TemporalFrame;
    public double TimeLast;

    public uint RenderWidth, RenderHeight, OutputWidth, OutputHeight;

    public readonly ViewHistory History = new();

    // the targets written and read within one frame of this view (g-buffer, light buffers, frame_render), shareable
    // between views of the same size through the pool
    private readonly Texture?[] m_transient = new Texture?[(int)RenderTarget.max];

    public bool HasTargets { get; set; }

    // screen space shadows: the directional lights with a slice in this view's sss target, in slice order
    public readonly List<Light> SssLights = new();
    public bool SssSliceWarned;

    // directional shadow cascades, per light (spartan computes them in the light from the one camera)
    public readonly Dictionary<Light, DirectionalCascades> Cascades = new(ReferenceEqualityComparer.Instance);

    // what this view culled in this frame (Renderer.Culling.cs): its visible renderables, each with the index of its
    // per view overrides or -1, in the order of the world's renderables
    public readonly Dictionary<Render, int> Visible = new(ReferenceEqualityComparer.Instance);
    public readonly List<ViewDrawOverrides> Overrides = new();
    public int OverrideCount;

    // what this view draws: the frame's draws it culled in, with its overrides
    public readonly List<Renderer.DrawCall> DrawCalls      = new();
    public readonly List<Renderer.DrawCall> DrawCallsAlpha = new();

    // the light table as this view sees it: the shared entries with this view's directional cascades and contact
    // shadow slices
    public LightParameters[] LightParameters = Array.Empty<LightParameters>();

    public DirectionalCascades GetCascades(Light light)
    {
        if (!Cascades.TryGetValue(light, out DirectionalCascades? cascades))
        {
            cascades = new DirectionalCascades();
            Cascades.Add(light, cascades);
        }
        return cascades;
    }

    // whether a target lives in the view (the rest, sky, luts and the shadow atlas, are shared by the frame)
    public static bool IsViewTarget(RenderTarget type) => ViewHistory.IsHistoryTarget(type) || IsTransientTarget(type);

    public static bool IsTransientTarget(RenderTarget type) => type is
        RenderTarget.gbuffer_color or RenderTarget.gbuffer_normal or RenderTarget.gbuffer_material or
        RenderTarget.gbuffer_emissive or RenderTarget.gbuffer_velocity or RenderTarget.dlss_reactivity or
        RenderTarget.frame_render or RenderTarget.light_diffuse or RenderTarget.light_specular;

    public Texture? GetTarget(RenderTarget type) =>
        ViewHistory.IsHistoryTarget(type) ? History.Get(type) : m_transient[(int)type];

    public Texture? GetTransient(RenderTarget type) => m_transient[(int)type];
    public void SetTransient(RenderTarget type, Texture? texture) => m_transient[(int)type] = texture;

    // a new frame history: the frame constants start from identity and the jitter from phase 0
    public void ResetFrameState()
    {
        Cb = default;
        Cb.view = Cb.projection = Cb.view_projection = Cb.view_projection_unjittered = Matrix4x4.Identity;
        JitterPhase   = 0;
        Jitter        = Vector2.Zero;
        TemporalFrame = 0;
        TimeLast      = 0.0;
        History.CameraCutPending = false;
        History.CameraPrevious   = null;
        History.TaauResetPending = true;
        History.SssWritten       = false;
        SssLights.Clear();
        SssSliceWarned = false;
        Cascades.Clear();
        Visible.Clear();
        OverrideCount = 0;
        DrawCalls.Clear();
        DrawCallsAlpha.Clear();
        LightParameters = Array.Empty<LightParameters>();
    }
}
