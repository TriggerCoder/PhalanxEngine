using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public struct BoundingBox : IEquatable<BoundingBox> 
{
    private Vector3 m_min;
    private Vector3 m_max;

    public BoundingBox()
    {
        m_min = Vector3.Infinity;
        m_max = Vector3.InfinityNeg;
    }
    public BoundingBox(Vector3 min, Vector3 max)
    {
        m_min = min;
        m_max = max;
    }
    public BoundingBox(Vector3[] points, int point_count)
    {
        m_min = Vector3.Infinity;
        m_max = Vector3.InfinityNeg;

        for (int i = 0; i < point_count; i++)
        {
            m_max.x = Math.Max(m_max.x, points[i].x);
            m_max.y = Math.Max(m_max.y, points[i].y);
            m_max.z = Math.Max(m_max.z, points[i].z);

            m_min.x = Math.Min(m_min.x, points[i].x);
            m_min.y = Math.Min(m_min.y, points[i].y);
            m_min.z = Math.Min(m_min.z, points[i].z);
        }
    }
    public static bool operator ==(BoundingBox l, BoundingBox r) { return l.Equals(r); }
    public static bool operator !=(BoundingBox l, BoundingBox r) { return !l.Equals(r); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is BoundingBox other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(BoundingBox other) { return (m_min == other.m_min) && (m_max == other.m_max); }
    public override readonly int GetHashCode() { return HashCode.Combine(m_min, m_max); }

    public readonly Math.Intersection Intersects(Vector3 point)
    {
        if (point.x < m_min.x || point.x > m_max.x ||
            point.y < m_min.y || point.y > m_max.y ||
            point.z < m_min.z || point.z > m_max.z)
            return Math.Intersection.Outside;

        return Math.Intersection.Inside;
    }
    public readonly Math.Intersection Intersects(BoundingBox box)
    {
        if (box.m_max.x < m_min.x || box.m_min.x > m_max.x ||
            box.m_max.y < m_min.y || box.m_min.y > m_max.y ||
            box.m_max.z < m_min.z || box.m_min.z > m_max.z)
                return Math.Intersection.Outside;
        else if (box.m_min.x < m_min.x || box.m_max.x > m_max.x ||
                 box.m_min.y < m_min.y || box.m_max.y > m_max.y ||
                 box.m_min.z < m_min.z || box.m_max.z > m_max.z)
            return Math.Intersection.Intersects;
        return Math.Intersection.Inside;
    }
    public void Merge(BoundingBox box)
    {
        m_min.x = Math.Min(m_min.x, box.m_min.x);
        m_min.y = Math.Min(m_min.y, box.m_min.y);
        m_min.z = Math.Min(m_min.z, box.m_min.z);

        m_max.x = Math.Max(m_max.x, box.m_max.x);
        m_max.y = Math.Max(m_max.y, box.m_max.y);
        m_max.z = Math.Max(m_max.z, box.m_max.z);
    }

    public Vector3 GetMin() { return m_min; }
    public Vector3 GetMax() { return m_max; }
    public readonly Vector3 GetCenter() { return (m_max + m_min) * 0.5f; }
    public readonly Vector3 GetSize() { return m_max - m_min; }
    public readonly Vector3 GetExtents() { return (m_max - m_min) * 0.5f; }
    public readonly float GetVolume()
    {
        Vector3 size = GetSize();
        return size.x * size.y * size.z;
    }
    public readonly Vector3 GetClosestPoint(Vector3 point)
    {
        return new Vector3(Math.Max(m_min.x, Math.Min(point.x, m_max.x)),
                           Math.Max(m_min.y, Math.Min(point.y, m_max.y)),
                           Math.Max(m_min.z, Math.Min(point.z, m_max.z)));
    }
    public readonly bool Contains(Vector3 point)
    {
        return  (point.x >= m_min.x && point.x <= m_max.x) &&
                (point.y >= m_min.y && point.y <= m_max.y) &&
                (point.z >= m_min.z && point.z <= m_max.z);
    }

    private static readonly BoundingBox zero = new BoundingBox (Vector3.Zero, Vector3.Zero);
    private static readonly BoundingBox unit = new BoundingBox (Vector3.One * -0.5f, Vector3.One * 0.5f);
    private static readonly BoundingBox infinite = new BoundingBox(Vector3.InfinityNeg, Vector3.Infinity);

    public static BoundingBox Zero => zero;
    public static BoundingBox Unit => unit;
    public static BoundingBox Infinite => infinite;
}
