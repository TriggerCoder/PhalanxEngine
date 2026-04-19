using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Vector3 : IEquatable<Vector3>
{
    public float x;
    public float y;
    public float z;

    public Vector3()
    {
        x = 0;
        y = 0;
        z = 0;
    }
    public Vector3(float X)
    {
        x = X;
        y = X;
        z = X;
    }
    public Vector3(float[] vec)
    {
        if (vec.Length != 3)
            throw new ArgumentOutOfRangeException("array dimension is different from Vector3");

        x = vec[0];
        y = vec[1];
        z = vec[2];
    }
    public Vector3(float X, float Y, float Z)
    {
        x = X;
        y = Y;
        z = Z;
    }
    public float this[int index]
    {
        readonly get
        {
            return index switch
            {
                0 => x,
                1 => y,
                2 => z,
                _ => throw new ArgumentOutOfRangeException("index"),
            };
        }
        set
        {
            switch (index)
            {
                case 0:
                    x = value;
                    break;
                case 1:
                    y = value;
                    break;
                case 2:
                    z = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("index");
            }
        }
    }
    public static Vector3 operator +(Vector3 l, Vector3 r)
    {
        l.x += r.x;
        l.y += r.y;
        l.z += r.z;
        return l;
    }
    public static Vector3 operator -(Vector3 l, Vector3 r)
    {
        l.x -= r.x;
        l.y -= r.y;
        l.z -= r.z;
        return l;
    }
    public static Vector3 operator -(Vector3 other)
    {
        other.x = 0f - other.x;
        other.y = 0f - other.y;
        other.z = 0f - other.z;
        return other;
    }
    public static Vector3 operator *(Vector3 other, float scale)
    {
        other.x *= scale;
        other.y *= scale;
        other.z *= scale;
        return other;
    }
    public static Vector3 operator *(float scale, Vector3 other)
    {
        other.x *= scale;
        other.y *= scale;
        other.z *= scale;
        return other;
    }
    public static Vector3 operator *(Vector3 l, Vector3 r)
    {
        l.x *= r.x;
        l.y *= r.y;
        l.z *= r.z;
        return l;
    }
    public static Vector3 operator /(Vector3 other, float divisor)
    {
        other.x /= divisor;
        other.y /= divisor;
        other.z /= divisor;
        return other;
    }
    public static Vector3 operator /(Vector3 other, Vector3 divisorv)
    {
        other.x /= divisorv.x;
        other.y /= divisorv.y;
        other.z /= divisorv.z;
        return other;
    }
    public static Vector3 operator %(Vector3 other, float divisor)
    {
        other.x %= divisor;
        other.y %= divisor;
        other.z %= divisor;
        return other;
    }
    public static Vector3 operator %(Vector3 other, Vector3 divisorv)
    {
        other.x %= divisorv.x;
        other.y %= divisorv.y;
        other.z %= divisorv.z;
        return other;
    }
    public static bool operator ==(Vector3 l, Vector3 r) { return l.Equals(r); }
    public static bool operator !=(Vector3 l, Vector3 r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Vector3 other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Vector3 other){ return Math.ApproximateEquals(x, other.x) && Math.ApproximateEquals(y, other.y) && Math.ApproximateEquals(z, other.z); }
    public override readonly int GetHashCode() { return HashCode.Combine(x, y, z); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Length() { return Math.Sqrt(x * x + y * y + z * z); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float LengthSquared() { return x * x + y * y + z * z; }
    public readonly Vector3 Max(float other) { return new Vector3(Math.Max(x, other), Math.Max(y, other), Math.Max(z, other)); }
    public readonly Vector3 Max(Vector3 other) { return new Vector3(Math.Max(x, other.x), Math.Max(y, other.y), Math.Max(z, other.z)); }
    public static Vector3 Max(Vector3 a, Vector3 b) { return new Vector3(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z)); }
    public readonly Vector3 Min(float other) { return new Vector3(Math.Min(x, other), Math.Min(y, other), Math.Min(z, other)); }
    public readonly Vector3 Min(Vector3 other) { return new Vector3(Math.Min(x, other.x), Math.Min(y, other.y), Math.Min(z, other.z)); }
    public static Vector3 Min(Vector3 a, Vector3 b) { return new Vector3(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Distance(Vector3 other) { return Math.Sqrt((x - other.x) * (x - other.x) + (y - other.y) * (y - other.y) + (z - other.z) * (z - other.z)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(Vector3 a, Vector3 b) { return Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float DistanceSquared(Vector3 other) { return (x - other.x) * (x - other.x) + (y - other.y) * (y - other.y) + (z - other.z) * (z - other.z); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(Vector3 a, Vector3 b) { return (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z); }
    internal void Normalize()
    {
        float lengthsquared = LengthSquared();
        if (!Math.ApproximateEquals(lengthsquared, 1.0f) && lengthsquared > 0.0f)
        {
            float lengthinverted = 1 / Math.Sqrt(lengthsquared);
            x *= lengthinverted;
            y *= lengthinverted;
            z *= lengthinverted;
        }
        else
        {
            x = 0.0f;
            y = 0.0f;
            z = 0.0f;
        }
    }

    public static Vector3 Normalize(Vector3 other)
    {
        other.Normalize();
        return other;
    }
    public readonly Vector3 Normalized()
    {
        Vector3 result = this;
        result.Normalize();
        return result;
    }
    public readonly bool IsNormalized()
    {
        return Math.Abs(LengthSquared() - 1f) < 1E-06f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Dot(Vector3 other) { return x * other.x + y * other.y + z * other.z; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vector3 a, Vector3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 Cross(Vector3 other) { return new Vector3(y * other.z - z * other.y, z * other.x - x * other.z, x * other.y - y * other.x); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Cross(Vector3 a, Vector3 b) { return new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }

    public void ClampMagnitude(float max_length)
    {
        float sqrmag = LengthSquared();

        if (sqrmag > max_length * max_length)
        {
            float mag = Math.Sqrt(sqrmag);

            // these intermediate variables force the intermediate result to be
            // of float precision. without this, the intermediate result can be of higher
            // precision, which changes behavior.

            float normalized_x = x / mag;
            float normalized_y = y / mag;
            float normalized_z = z / mag;

            x = normalized_x * max_length;
            y = normalized_y * max_length;
            z = normalized_z * max_length;
        }
    }
    public void Floor()
    {
        x = Math.Floor(x);
        y = Math.Floor(y);
        z = Math.Floor(z);
    }
    public static Vector3 Floor(Vector3 other) { return new Vector3(Math.Floor(other.x), Math.Floor(other.y), Math.Floor(other.z)); }
    public void Round()
    {
        x = Math.Round(x);
        y = Math.Round(y);
        z = Math.Round(z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Round(Vector3 other) { return new Vector3(Math.Round(other.x), Math.Round(other.y), Math.Round(other.z)); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 Abs() { return new Vector3(Math.Abs(x), Math.Abs(y), Math.Abs(z)); }

    public readonly void FindBestAxisVectors(ref Vector3 Axis1, ref Vector3 Axis2)
    {
        float NX = Math.Abs(x);
        float NY = Math.Abs(y);
        float NZ = Math.Abs(z);

        // find best basis vectors
        if (NZ > NX && NZ > NY)	
            Axis1 = new Vector3(1, 0, 0);
        else
            Axis1 = new Vector3(0, 0, 1);

        Axis1 = (Axis1 - this * (Axis1.Dot(this))).Normalized();
        Axis2 = Axis1.Cross(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 Lerp(Vector3 other, float t) { return this * (1.0f - t) + other * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) { return a * (1.0f - t) + b * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsNaN() { return Math.IsNaN(x) || Math.IsNaN(y) || Math.IsNaN(z); }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsFinite() { return Math.IsFinite(x) && Math.IsFinite(y) && Math.IsFinite(z); }
    public override readonly string ToString() { return "X: " + x.ToString() + ", Y:" + y.ToString() + ", Z:" + z.ToString(); }

    private static readonly Vector3 zero = new Vector3(0f, 0f, 0f);
    private static readonly Vector3 one = new Vector3(1f, 1f, 1f);
    private static readonly Vector3 left = new Vector3(-1.0f, 0.0f, 0.0f);
    private static readonly Vector3 right = new Vector3(1.0f, 0.0f, 0.0f);
    private static readonly Vector3 up = new Vector3(0.0f, 1.0f, 0.0f);
    private static readonly Vector3 down = new Vector3(0.0f, -1.0f, 0.0f);
    private static readonly Vector3 forward = new Vector3(0.0f, 0.0f, 1.0f);
    private static readonly Vector3 backward = new Vector3(0.0f, 0.0f, -1.0f);
    private static readonly Vector3 infinity = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
    private static readonly Vector3 infinityneg = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

    public static Vector3 Zero => zero;
    public static Vector3 One => one;
    public static Vector3 Left => left;
    public static Vector3 Right => right;
    public static Vector3 Up => up;
    public static Vector3 Down => down;
    public static Vector3 Forward => forward;
    public static Vector3 Backward => backward;
    public static Vector3 Infinity => infinity;
    public static Vector3 InfinityNeg => infinityneg;
}