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
    // phalanx: the state lives in Phalanx.Core (RenderView) so World components can read it without referencing Rendering
    public static RHI_Viewport GetViewport() { return RenderView.GetViewport(); }
    public static RHI_Texture GetRenderTarget(Renderer_RenderTarget rt) { return RenderView.GetRenderTarget(rt); }
    public static void SetViewport(float width, float height) { RenderView.SetViewport(width, height); }
}