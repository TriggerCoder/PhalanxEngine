using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Math;
public static class Vector2Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v, float other) { return new Vector2(MathEx.Max(v.X, other), MathEx.Max(v.Y, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v, Vector2 other) { return Vector2.Max(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v, float other) { return new Vector2(MathEx.Min(v.X, other), MathEx.Min(v.Y, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v, Vector2 other) { return Vector2.Min(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(this Vector2 v, Vector2 other) { return Vector2.Distance(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(this Vector2 v, Vector2 other) { return Vector2.DistanceSquared(v, other); }
}
