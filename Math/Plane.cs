using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Plane : IEquatable<Plane>
{
    public Vector3 normal;
    public float d;

    public Plane()
    {
        normal = Vector3.Up;
        d = 0;
    }

    public Plane(Vector3 normal, float d)
    {
        this.normal = normal;
        this.d = d;
    }
    public Plane(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;

        Vector3 cross = Vector3.Cross(ab, ac);
        normal = Vector3.Normalize(cross);
        d = -Vector3.Dot(normal, a);
    }

    public Plane(Vector3 normal, Vector3 point)
    {
        this.normal = normal.Normalized();
        d = -this.normal.Dot(point);
    }

    public void Normalize()
    {
        float denominator = Math.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
        normal = Vector3.Normalize(normal);
        float nominator = Math.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
        float fentity = nominator / denominator;
        d *= fentity;
    }
    public static Plane Normalize(Plane other)
    {
        other.Normalize();
        return other;
    }

    public static bool operator ==(Plane l, Plane r) { return l.Equals(r); }
    public static bool operator !=(Plane l, Plane r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Plane other)
            return Equals(other);
        return false;
    }
    public override readonly int GetHashCode() { return HashCode.Combine(normal, d); }
    public readonly bool Equals(Plane other) { return Math.ApproximateEquals(d, other.d) && (normal == other.normal); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Dot(Vector3 v) { return (normal.x* v.x) + (normal.y* v.y) + (normal.z* v.z) + d; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Plane p, Vector3 v) { return p.Dot(v); }
}