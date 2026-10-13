using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Vector3Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Abs(this Vector3 v) { return Vector3.Abs(v); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Max(this Vector3 v, float other) { return new Vector3(Math.Max(v.X, other), Math.Max(v.Y, other), Math.Max(v.Z, other)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Max(this Vector3 v, Vector3 other) { return Vector3.Max(v,other); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Min(this Vector3 v, float other) { return new Vector3(Math.Min(v.X, other), Math.Min(v.Y, other), Math.Min(v.Z, other)); }

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
        return Math.Abs(v.LengthSquared() - 1f) < 1E-06f;
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
            float mag = Math.Sqrt(sqrmag);

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
        v.X = Math.Floor(v.X);
        v.Y = Math.Floor(v.Y);
        v.Z = Math.Floor(v.Z);
    }
    public static void Round(this Vector3 v)
    {
        v.X = Math.Round(v.X);
        v.Y = Math.Round(v.Y);
        v.Z = Math.Round(v.Z);
    }

    public static void FindBestAxisVectors(this Vector3 v, ref Vector3 Axis1, ref Vector3 Axis2)
    {
        float NX = Math.Abs(v.X);
        float NY = Math.Abs(v.Y);
        float NZ = Math.Abs(v.Z);

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
    public static bool IsNaN(this Vector3 v) { return Math.IsNaN(v.X) || Math.IsNaN(v.Y) || Math.IsNaN(v.Z); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(this Vector3 v) { return Math.IsFinite(v.X) && Math.IsFinite(v.Y) && Math.IsFinite(v.Z); }
}

