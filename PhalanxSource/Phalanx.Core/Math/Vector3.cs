using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Math;
public static class Vector3Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Abs(this Vector3 v) { return Vector3.Abs(v); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Max(this Vector3 v, float other) { return new Vector3(MathEx.Max(v.X, other), MathEx.Max(v.Y, other), MathEx.Max(v.Z, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Max(this Vector3 v, Vector3 other) { return Vector3.Max(v,other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Min(this Vector3 v, float other) { return new Vector3(MathEx.Min(v.X, other), MathEx.Min(v.Y, other), MathEx.Min(v.Z, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Min(this Vector3 v, Vector3 other) { return Vector3.Min(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(this Vector3 v, Vector3 other) { return Vector3.Distance(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(this Vector3 v, Vector3 other) { return Vector3.DistanceSquared(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Normalized(this Vector3 v) { return Vector3.Normalize(v); }

    public static bool IsNormalized(this Vector3 v)
    {
        return MathEx.Abs(v.LengthSquared() - 1f) < 1E-06f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(this Vector3 v, Vector3 other) { return Vector3.Dot(v, other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Cross(this Vector3 v, Vector3 other) { return Vector3.Cross(v, other); }

    public static void ClampMagnitude(this Vector3 v, float max_length)
    {
        float sqrmag = v.LengthSquared();

        if (sqrmag > max_length * max_length)
        {
            float mag = MathEx.Sqrt(sqrmag);

            // these intermediate variables force the intermediate result to be
            // of float precision. without this, the intermediate result can be of higher
            // precision, which changes behavior.

            float normalized_x = v.X / mag;
            float normalized_y = v.Y / mag;
            float normalized_z = v.Z / mag;

            v.X = normalized_x * max_length;
            v.Y = normalized_y * max_length;
            v.Z = normalized_z * max_length;
        }
    }
    public static void Floor(this Vector3 v)
    {
        v.X = MathEx.Floor(v.X);
        v.Y = MathEx.Floor(v.Y);
        v.Z = MathEx.Floor(v.Z);
    }
    public static void Round(this Vector3 v)
    {
        v.X = MathEx.Round(v.X);
        v.Y = MathEx.Round(v.Y);
        v.Z = MathEx.Round(v.Z);
    }

    public static void FindBestAxisVectors(this Vector3 v, ref Vector3 Axis1, ref Vector3 Axis2)
    {
        float NX = MathEx.Abs(v.X);
        float NY = MathEx.Abs(v.Y);
        float NZ = MathEx.Abs(v.Z);

        // find best basis vectors
        if (NZ > NX && NZ > NY)	
            Axis1 = new Vector3(1, 0, 0);
        else
            Axis1 = new Vector3(0, 0, 1);

        Axis1 = Vector3.Normalize(Axis1 - v * (Axis1.Dot(v)));
        Axis2 = Axis1.Cross(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(this Vector3 v, Vector3 other, float t) { return Vector3.Lerp(v, other, t); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNaN(this Vector3 v) { return MathEx.IsNaN(v.X) || MathEx.IsNaN(v.Y) || MathEx.IsNaN(v.Z); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(this Vector3 v) { return MathEx.IsFinite(v.X) && MathEx.IsFinite(v.Y) && MathEx.IsFinite(v.Z); }
}

