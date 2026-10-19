// What IPreDraw.OnPreDraw gets: the view about to draw the entity and the per view overrides of that draw.
using System.Drawing;
using System.Numerics;

namespace Phalanx.World;

/// <summary>A view of the world as the renderer draws it (Phalanx.Rendering's SceneView implements it).</summary>
public interface ISceneView
{
    string Name { get; }
    /// <summary>Whether this is the renderer's main view (the world camera, full screen).</summary>
    bool IsMain { get; }
    /// <summary>The view's own layer mask; the effective mask is this and the camera's <see cref="Camera.CullMask"/>.</summary>
    uint CullMask { get; }
    /// <summary>Where the view's image goes in the output, normalised to [0, 1].</summary>
    RectangleF ScreenRect { get; }
    int Order { get; }
    /// <summary>The camera the view renders from now.</summary>
    Camera? GetCamera();
}

/// <summary>
/// The view an <see cref="IPreDraw"/> callback runs for. It only lives for the call (a ref struct, it can't be kept),
/// and what is set through it applies to the entity's draw in this view this frame: other views and the next frame
/// draw the entity as usual. The shadow maps are shared by the views and don't see these overrides.
/// </summary>
public readonly ref struct ViewContext
{
    private readonly ViewDrawOverrides m_overrides;

    internal ViewContext(ISceneView view, int view_index, Camera? camera, Entity entity, Render render, ViewDrawOverrides overrides)
    {
        View        = view;
        ViewIndex   = view_index;
        Camera      = camera;
        Entity      = entity;
        Render      = render;
        m_overrides = overrides;
    }

    /// <summary>The view about to draw the entity.</summary>
    public ISceneView View { get; }
    /// <summary>The position of the view among the views drawn this frame (0 is the first).</summary>
    public int ViewIndex { get; }
    /// <summary>The camera of <see cref="View"/>.</summary>
    public Camera? Camera { get; }
    /// <summary>The entity being drawn.</summary>
    public Entity Entity { get; }
    /// <summary>The entity's renderable.</summary>
    public Render Render { get; }
    public bool IsMainView => View.IsMain;

    /// <summary>Whether the entity is drawn by this view; false hides it in this view only (its shadow stays).</summary>
    public bool Visible => !m_overrides.Hidden;
    public void SetVisible(bool visible) => m_overrides.Hidden = !visible;

    /// <summary>Draws the entity with <paramref name="material"/> in this view (null: its own). The material's
    /// parameters are shared, only which material this draw uses is per view.</summary>
    public void SetMaterial(Material? material) => m_overrides.Material = material;
    public Material? MaterialOverride => m_overrides.Material;

    /// <summary>The uv tiling and offset of this view's draw, instead of the renderable's.</summary>
    public void SetUvTransform(Vector2 tiling, Vector2 offset)
    {
        m_overrides.UvTiling = tiling;
        m_overrides.UvOffset = offset;
    }

    /// <summary>Runs <paramref name="action"/> at the start of the next world tick (see <see cref="World.Defer"/>).</summary>
    public void Defer(Action action) => World.Defer(action);
}

// one entity's overrides in one view for one frame, reset before every view's callbacks
internal sealed class ViewDrawOverrides
{
    public bool Hidden;
    public Material? Material;
    public Vector2? UvTiling, UvOffset;

    public bool ChangesDraw => Material != null || UvTiling.HasValue;

    public void Reset()
    {
        Hidden   = false;
        Material = null;
        UvTiling = null;
        UvOffset = null;
    }
}
