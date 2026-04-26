using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Vector2Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v, float other) { return new Vector2(Math.Max(v.X, other), Math.Max(v.Y, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v, Vector2 other) { return Vector2.Max(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v, float other) { return new Vector2(Math.Min(v.X, other), Math.Min(v.Y, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v, Vector2 other) { return Vector2.Min(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(this Vector2 v, Vector2 other) { return Vector2.Distance(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(this Vector2 v, Vector2 other) { return Vector2.DistanceSquared(v, other); }
}
