// A view the renderer draws the world from: a camera, where its image goes and the state that belongs to it alone (the
// frame constants, the taau jitter and history, the depth history, the screen space shadows, the directional shadow
// cascades and its render targets). Spartan's counterpart is ViewState (source/rendering/Renderer_Internal.h @ 4c07baa,
// primary_view / preview_view, Renderer.cpp:127-129), see Renderer.Views.cs for the differences.
using System.Drawing;
using Phalanx.World;

namespace Phalanx.Rendering;

/// <summary>The renderer features a view runs. Passes that aren't ported yet get their bit when they land.</summary>
[Flags]
public enum ViewFeatures
{
    None              = 0,
    /// <summary>Taau when <see cref="Renderer.AntiAliasingUpsampling"/> selects it; without it the view upscales linearly.</summary>
    Taau              = 1 << 0,
    /// <summary>Screen space shadows when <see cref="Renderer.ScreenSpaceShadows"/> is on.</summary>
    ScreenSpaceShadows = 1 << 1,
    Default           = Taau | ScreenSpaceShadows,
}

/// <summary>
/// One view of the world. <see cref="Renderer.MainView"/> always exists and draws the world camera full screen; more
/// views come with split-screen and editor viewports. A view keeps its own camera history, taau history, exposure and
/// render targets, so a camera cut or a resize of one view leaves the others alone.
/// </summary>
public sealed class SceneView : IDisposable
{
    internal SceneView(string name, bool is_main)
    {
        Name   = name;
        IsMain = is_main;
        State  = new ViewState(this);
    }

    public string Name { get; }

    /// <summary>Whether this is <see cref="Renderer.MainView"/>, which can't be removed.</summary>
    public bool IsMain { get; }

    /// <summary>The camera the view renders from; null follows <see cref="Phalanx.World.World.GetCamera"/>.</summary>
    public Camera? Camera { get; set; }

    /// <summary>Where the view's image goes in the output, normalised to [0, 1] (split-screen layout).</summary>
    public RectangleF ScreenRect { get; set; } = new RectangleF(0.0f, 0.0f, 1.0f, 1.0f);

    public ViewFeatures Features { get; set; } = ViewFeatures.Default;

    /// <summary>The order views render and composite in, lowest first.</summary>
    public int Order { get; set; }

    /// <summary>The entity layers the view sees (no layers or culling yet, every renderable is drawn).</summary>
    public uint LayerMask { get; set; } = uint.MaxValue;

    /// <summary>False once the view was removed or the renderer shut down (the main view comes back with Initialize).</summary>
    public bool IsAlive => State.HasTargets;

    public (uint width, uint height) RenderResolution => (State.RenderWidth, State.RenderHeight);
    public (uint width, uint height) OutputResolution => (State.OutputWidth, State.OutputHeight);

    /// <summary>The camera this view renders from now.</summary>
    public Camera? GetCamera() => Camera ?? Phalanx.World.World.GetCamera();

    /// <summary>Drops this view's taau history (spartan: Renderer::ResetTaauHistory, per view).</summary>
    public void ResetHistory() => State.History.TaauResetPending = true;

    /// <summary>The next frame of this view starts a new shot (spartan: Renderer::NotifyCameraCut, per view).</summary>
    public void NotifyCameraCut() => State.History.CameraCutPending = true;

    /// <summary>Removes the view from the renderer and releases its targets; the main view can't be removed.</summary>
    public void Dispose() => Renderer.RemoveView(this);

    internal ViewState State { get; }

    public override string ToString() => $"{Name} {State.RenderWidth}x{State.RenderHeight} -> {State.OutputWidth}x{State.OutputHeight}";
}
