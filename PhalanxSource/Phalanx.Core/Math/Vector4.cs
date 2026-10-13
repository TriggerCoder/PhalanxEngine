using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Math;
public static class Vector4Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Max(this Vector4 v, float other) { return new Vector4(MathEx.Max(v.X, other), MathEx.Max(v.Y, other), MathEx.Max(v.Z, other), MathEx.Max(v.W, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Max(this Vector4 v, Vector4 other) { return Vector4.Max(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Min(this Vector4 v, float other) { return new Vector4(MathEx.Min(v.X, other), MathEx.Min(v.Y, other), MathEx.Min(v.Z, other), MathEx.Min(v.W, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Min(this Vector4 v, Vector4 other) { return Vector4.Min(v, other); }

    public static bool IsNormalized(this Vector4 v)
    {
        return MathEx.Abs(v.LengthSquared() - 1f) < 1E-06f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Lerp(this Vector4 v, Vector4 other, float t) { return Vector4.Lerp(v, other, t); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(this Vector4 v) { return MathEx.IsNaN(v.X) || MathEx.IsNaN(v.Y) || MathEx.IsNaN(v.Z) || MathEx.IsNaN(v.W); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(this Vector4 v) { return MathEx.IsFinite(v.X) && MathEx.IsFinite(v.Y) && MathEx.IsFinite(v.Z) && MathEx.IsFinite(v.W); }

}