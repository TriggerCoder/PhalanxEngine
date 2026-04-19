using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Vector4 : IEquatable<Vector4>
{
    public float x;
    public float y;
    public float z;
    public float w;
    public Vector4()
    {
        x = 0;
        y = 0;
        z = 0;
        w = 0;
    }
    public Vector4(float X)
    {
        x = X;
        y = X;
        z = X;
        w = X;
    }
    public Vector4(float[] vec)
    {
        int lenght = vec.Length;
        if ((lenght != 4) || (lenght != 3))
            throw new ArgumentOutOfRangeException("array dimension is different from Vector4");

        x = vec[0];
        y = vec[1];
        z = vec[2];
        if (lenght > 3)
            w = vec[3];
        else
            w = 0;
    }
    public Vector4(float X, float Y, float Z, float W)
    {
        x = X;
        y = Y;
        z = Z;
        w = W;
    }
    public Vector4(Vector4 other, float W = 0)
    {
        x = other.x;
        y = other.y;
        z = other.z;
        w = W;
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
                3 => w,
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
                case 3:
                    w = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("index");
            }
        }
    }
    public static Vector4 operator +(Vector4 l, Vector4 r)
    {
        l.x += r.x;
        l.y += r.y;
        l.z += r.z;
        l.w += r.w;
        return l;
    }
    public static Vector4 operator -(Vector4 l, Vector4 r)
    {
        l.x -= r.x;
        l.y -= r.y;
        l.z -= r.z;
        l.w -= r.w;
        return l;
    }
    public static Vector4 operator -(Vector4 other)
    {
        other.x = 0f - other.x;
        other.y = 0f - other.y;
        other.z = 0f - other.z;
        other.w = 0f - other.w;
        return other;
    }
    public static Vector4 operator *(Vector4 other, float scale)
    {
        other.x *= scale;
        other.y *= scale;
        other.z *= scale;
        other.w *= scale;
        return other;
    }
    public static Vector4 operator *(float scale, Vector4 other)
    {
        other.x *= scale;
        other.y *= scale;
        other.z *= scale;
        other.w *= scale;
        return other;
    }
    public static Vector4 operator *(Vector4 l, Vector4 r)
    {
        l.x *= r.x;
        l.y *= r.y;
        l.z *= r.z;
        l.w *= r.w;
        return l;
    }
    public static Vector4 operator /(Vector4 other, float divisor)
    {
        other.x /= divisor;
        other.y /= divisor;
        other.z /= divisor;
        other.w /= divisor;
        return other;
    }
    public static Vector4 operator /(Vector4 other, Vector4 divisorv)
    {
        other.x /= divisorv.x;
        other.y /= divisorv.y;
        other.z /= divisorv.z;
        other.w /= divisorv.w;
        return other;
    }
    public static Vector4 operator %(Vector4 other, float divisor)
    {
        other.x %= divisor;
        other.y %= divisor;
        other.z %= divisor;
        other.w %= divisor;
        return other;
    }
    public static Vector4 operator %(Vector4 other, Vector4 divisorv)
    {
        other.x %= divisorv.x;
        other.y %= divisorv.y;
        other.z %= divisorv.z;
        other.w %= divisorv.w;
        return other;
    }
    public static bool operator ==(Vector4 l, Vector4 r) { return l.Equals(r); }
    public static bool operator !=(Vector4 l, Vector4 r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Vector4 other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Vector4 other) { return Math.ApproximateEquals(x, other.x) && Math.ApproximateEquals(y, other.y) && Math.ApproximateEquals(z, other.z) && Math.ApproximateEquals(w, other.w); }
    public override readonly int GetHashCode() { return HashCode.Combine(x, y, z, w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Length() { return Math.Sqrt(x * x + y * y + z * z + w * w); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float LengthSquared() { return x * x + y * y + z * z + w * w; }
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
    public readonly Vector4 Normalized()
    {
        Vector4 result = this;
        result.Normalize();
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector4 Lerp(Vector4 other, float t) { return this * (1.0f - t) + other * t; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) { return a * (1.0f - t) + b * t; }

    public override readonly string ToString() { return "X: " + x.ToString() + ", Y:" + y.ToString() + ", Z:" + z.ToString() + ", W:" + w.ToString(); }

    private static readonly Vector4 zero = new Vector4(0f, 0f, 0f, 0f);
    private static readonly Vector4 one = new Vector4(1f, 1f, 1f, 1f);
    private static readonly Vector4 infinity = new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
    private static readonly Vector4 infinityneg = new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

    public static Vector4 Zero => zero;
    public static Vector4 One => one;
    public static Vector4 Infinity => infinity;
    public static Vector4 InfinityNeg => infinityneg;
}