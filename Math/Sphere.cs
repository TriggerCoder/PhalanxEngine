using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct Sphere : IEquatable<Sphere>
{
    public Vector3 Center;
    public float Radius;
    public Sphere()
    {
        Center = Vector3.Zero;
        Radius = 1;
    }
    public Sphere(Vector3 Center, float Radius)
    {
        this.Center = Center;
        this.Radius = Radius;
    }

    public static bool operator ==(Sphere l, Sphere r) { return l.Equals(r); }
    public static bool operator !=(Sphere l, Sphere r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Sphere other)
            return Equals(other);
        return false;
    }
    public override readonly int GetHashCode() { return HashCode.Combine(Center, Radius); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Sphere other) { return Math.ApproximateEquals(Radius, other.Radius) && (Center == other.Center); }
}