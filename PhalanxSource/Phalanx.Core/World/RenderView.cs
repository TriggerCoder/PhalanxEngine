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
    // TODO(port): real render targets are created in Renderer_Resources.cpp
    private static readonly RHI_Texture[] s_render_targets = { new RHI_Texture(8192, 8192) };
    public static RHI_Texture GetRenderTarget(Renderer_RenderTarget rt) { return s_render_targets[(int)rt]; }
}
