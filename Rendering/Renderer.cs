using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Phalanx;

public struct ShadowSlice
{
    public Light? light;
    public uint slice_index;
    public uint res;
    public Rectangle rect;
}

public struct PersistentLine
{
    public Vector3 from;
    public Vector3 to;
    public Color color_from;
    public Color color_to;
    public double expire_time;
}
public static class Renderer
{
    // TODO(port): source/rendering/Renderer.cpp, the viewport follows the swapchain/render resolution
    private static RHI_Viewport s_viewport = new RHI_Viewport(0, 0, 1280, 720);
    public static RHI_Viewport GetViewport() { return s_viewport; }
    // TODO(port): real render targets are created in Renderer_Resources.cpp
    private static readonly RHI_Texture[] s_render_targets = { new RHI_Texture(8192, 8192) };
    public static RHI_Texture GetRenderTarget(Renderer_RenderTarget rt) { return s_render_targets[(int)rt]; }
    public static void SetViewport(float width, float height) { s_viewport = new RHI_Viewport(0, 0, width, height); }
}