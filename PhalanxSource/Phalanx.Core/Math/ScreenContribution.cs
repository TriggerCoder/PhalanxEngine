// The projected size of a bounding sphere in a view, in pixels: spartan's contribution cull @ 4c07baa
// (data/shaders/common_culling.hlsl:85-135 sphere_project_ndc_vp and :198-218 sphere_contributes, the same at 67a6e8b).
// The sphere's center is projected once with the view projection and its conservative ndc rectangle is derived from the
// lengths of the matrix's clip axes (rx = radius * |vp column x| and so on): behind the camera when cw + rw <= 0, across
// the near plane when cw - rw <= 0 (the divide is unstable there, the sphere always contributes), otherwise each ndc
// extreme is one of the four (c +- r) / (w -+ rw). Its size in pixels is the larger of the rectangle's sides, ndc
// spanning 2 units over the render resolution. An orthographic projection has w = 1 and rw = 0, the same formula.
// System.Numerics matrices are row vector (clip = p * vp) like spartan's hlsl mul(p, vp): column i is clip axis i.
using System.Numerics;

namespace Phalanx.Math;

public static class ScreenContribution
{
    public enum Projection { Behind, Straddle, Valid }

    /// <summary>Spartan's sphere_project_ndc_vp: the conservative ndc rectangle of a world sphere.</summary>
    public static Projection ProjectSphereNdc(in Matrix4x4 vp, Vector3 center, float radius, out Vector2 min_ndc, out Vector2 max_ndc)
    {
        min_ndc = max_ndc = Vector2.Zero;
        var ax_x = new Vector3(vp.M11, vp.M21, vp.M31);
        var ax_y = new Vector3(vp.M12, vp.M22, vp.M32);
        var ax_w = new Vector3(vp.M14, vp.M24, vp.M34);
        // written out in the order of EntityStore.CullRows' vector loop, so both round alike
        float cx = vp.M11 * center.X + vp.M21 * center.Y + vp.M31 * center.Z + vp.M41;
        float cy = vp.M12 * center.X + vp.M22 * center.Y + vp.M32 * center.Z + vp.M42;
        float cw = vp.M14 * center.X + vp.M24 * center.Y + vp.M34 * center.Z + vp.M44;
        float rx = radius * ax_x.Length(), ry = radius * ax_y.Length(), rw = radius * ax_w.Length();
        if (cw + rw <= 0.0f)
            return Projection.Behind;
        if (cw - rw <= 0.0f)
            return Projection.Straddle;
        float inv_close = 1.0f / (cw - rw), inv_far = 1.0f / (cw + rw);
        float xlc = (cx - rx) * inv_close, xlf = (cx - rx) * inv_far, xhc = (cx + rx) * inv_close, xhf = (cx + rx) * inv_far;
        float ylc = (cy - ry) * inv_close, ylf = (cy - ry) * inv_far, yhc = (cy + ry) * inv_close, yhf = (cy + ry) * inv_far;
        min_ndc = new Vector2(MathF.Min(MathF.Min(xlc, xlf), MathF.Min(xhc, xhf)), MathF.Min(MathF.Min(ylc, ylf), MathF.Min(yhc, yhf)));
        max_ndc = new Vector2(MathF.Max(MathF.Max(xlc, xlf), MathF.Max(xhc, xhf)), MathF.Max(MathF.Max(ylc, ylf), MathF.Max(yhc, yhf)));
        return Projection.Valid;
    }

    /// <summary>The sphere's size in pixels at this render resolution: the larger side of its ndc rectangle;
    /// +infinity across the near plane, 0 behind the camera.</summary>
    public static float SpherePixels(in Matrix4x4 vp, Vector3 center, float radius, Vector2 resolution)
    {
        switch (ProjectSphereNdc(vp, center, radius, out Vector2 min_ndc, out Vector2 max_ndc))
        {
            case Projection.Behind:   return 0.0f;
            case Projection.Straddle: return float.PositiveInfinity;
        }
        Vector2 extent = (max_ndc - min_ndc) * resolution * 0.5f;
        return MathF.Max(extent.X, extent.Y);
    }

    /// <summary>Spartan's sphere_contributes: at least min_pixels on one axis (min_pixels &lt;= 0: always).</summary>
    public static bool SphereContributes(in Matrix4x4 vp, Vector3 center, float radius, Vector2 resolution, float min_pixels)
        => min_pixels <= 0.0f || SpherePixels(vp, center, radius, resolution) >= min_pixels;

    /// <summary>The per entity threshold dither (a deviation, EntityStore.Culling.cs): the fraction of the threshold an
    /// instance slot must reach, deterministic in [1 - DitherRange, 1), so the cut thins out over a band of sizes
    /// instead of a hard edge.</summary>
    public const float DitherRange = 0.5f;
    public static float DitherScale(uint slot) => 1.0f - DitherRange * ((slot * 0x9E3779B9u) >> 8) * (1.0f / 16777216.0f);
}
