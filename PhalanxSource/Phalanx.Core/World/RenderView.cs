using Phalanx.Geometry;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.World;

// phalanx: the render state World components and Input read (viewport, render-target sizes).
// It used to live on the static Renderer, but Core cannot reference Phalanx.Rendering, so the state lives here and
// Renderer forwards to it. Same values as before; becomes an instance service in roadmap step E.
public static class RenderView
{
    // TODO(port): source/rendering/Renderer.cpp, the viewport follows the swapchain/render resolution
    private static RHI_Viewport s_viewport = new RHI_Viewport(0, 0, 1280, 720);
    public static RHI_Viewport GetViewport() { return s_viewport; }
    public static void SetViewport(float width, float height) { s_viewport = new RHI_Viewport(0, 0, width, height); }
    // the sizes of the render targets; Phalanx.Rendering's renderer publishes the ones it creates (CreateRenderTargets),
    // the defaults are spartan's render resolution and the shadow atlas size
    private static readonly RHI_Texture[] s_render_targets = CreateDefaults();
    private static RHI_Texture[] CreateDefaults()
    {
        var targets = new RHI_Texture[(int)Renderer_RenderTarget.max];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = new RHI_Texture(1920, 1080);
        targets[(int)Renderer_RenderTarget.frame_output] = new RHI_Texture(1280, 720);
        targets[(int)Renderer_RenderTarget.shadow_atlas] = new RHI_Texture(8192, 8192);
        return targets;
    }
    public static RHI_Texture GetRenderTarget(Renderer_RenderTarget rt) { return s_render_targets[(int)rt]; }
    public static void SetRenderTargetSize(Renderer_RenderTarget rt, uint width, uint height) { s_render_targets[(int)rt] = new RHI_Texture(width, height); }

    // the standard meshes and the standard material (spartan: Renderer::GetStandardMesh / GetStandardMaterial), the
    // renderer publishes them when it creates them (CreateStandardMeshes / CreateStandardMaterials), Render reads them
    private static readonly IMesh?[] s_standard_meshes = new IMesh?[(int)MeshType.Max];
    private static Material? s_standard_material;
    public static IMesh? GetStandardMesh(MeshType type) { return s_standard_meshes[(int)type]; }
    public static void SetStandardMesh(MeshType type, IMesh? mesh) { s_standard_meshes[(int)type] = mesh; }
    public static Material? GetStandardMaterial() { return s_standard_material; }
    public static void SetStandardMaterial(Material? material) { s_standard_material = material; }
}
