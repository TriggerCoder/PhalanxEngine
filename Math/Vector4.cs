using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Vector4Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Max(this Vector4 v, float other) { return new Vector4(Math.Max(v.X, other), Math.Max(v.Y, other), Math.Max(v.Z, other), Math.Max(v.W, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Max(this Vector4 v, Vector4 other) { return Vector4.Max(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Min(this Vector4 v, float other) { return new Vector4(Math.Min(v.X, other), Math.Min(v.Y, other), Math.Min(v.Z, other), Math.Max(v.W, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Min(this Vector4 v, Vector4 other) { return Vector4.Min(v, other); }

    public static bool IsNormalized(this Vector4 v)
    {
        return Math.Abs(v.LengthSquared() - 1f) < 1E-06f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Lerp(this Vector4 v, Vector4 other, float t) { return Vector4.Lerp(v, other, t); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(this Vector4 v) { return Math.IsNaN(v.X) || Math.IsNaN(v.Y) || Math.IsNaN(v.Z) || Math.IsNaN(v.W); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(this Vector4 v) { return Math.IsFinite(v.X) && Math.IsFinite(v.Y) && Math.IsFinite(v.Z) && Math.IsFinite(v.W); }

}