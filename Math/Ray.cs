using System;
using System.Numerics;

namespace Phalanx;

[Serializable]
public struct Ray
{
    public Vector3 m_origin = Vector3.Zero;
    public Vector3 m_direction = Vector3.Zero;

    public Ray(Vector3 start, Vector3 direction)
    {
        m_origin    = start;
        m_direction = direction.Normalized();
    }

    public readonly float HitDistance(BoundingBox box)
    {
        // check for ray origin being inside the box
        if (box.Intersects(m_origin) == Math.Intersection.Inside)
            return 0.0f;

        float distance = float.PositiveInfinity;

        // Check for intersecting in the X-direction
        if (m_origin.X < box.GetMin().X && m_direction.X > 0.0f)
        {
            float x = (box.GetMin().X - m_origin.X) / m_direction.X;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.Y >= box.GetMin().Y && point.Y <= box.GetMax().Y && point.Z >= box.GetMin().Z && point.Z <= box.GetMax().Z)
                    distance = x;
            }
        }
        if (m_origin.X > box.GetMax().X && m_direction.X < 0.0f)
        {
            float x = (box.GetMax().X - m_origin.X) / m_direction.X;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.Y >= box.GetMin().Y && point.Y <= box.GetMax().Y && point.Z >= box.GetMin().Z && point.Z <= box.GetMax().Z)
                    distance = x;
            }
        }

        // Check for intersecting in the Y-direction
        if (m_origin.Y < box.GetMin().Y && m_direction.Y > 0.0f)
        {
            float x = (box.GetMin().Y - m_origin.Y) / m_direction.Y;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.X >= box.GetMin().X && point.X <= box.GetMax().X && point.Z >= box.GetMin().Z && point.Z <= box.GetMax().Z)
                    distance = x;
            }
        }
        if (m_origin.Y > box.GetMax().Y && m_direction.Y < 0.0f)
        {
            float x = (box.GetMax().Y - m_origin.Y) / m_direction.Y;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.X >= box.GetMin().X && point.X <= box.GetMax().X && point.Z >= box.GetMin().Z && point.Z <= box.GetMax().Z)
                    distance = x;
            }
        }

        // Check for intersecting in the Z-direction
        if (m_origin.Z < box.GetMin().Z && m_direction.Z > 0.0f)
        {
            float x = (box.GetMin().Z - m_origin.Z) / m_direction.Z;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.X >= box.GetMin().X && point.X <= box.GetMax().X && point.Y >= box.GetMin().Y && point.Y <= box.GetMax().Y)
                    distance = x;
            }
        }
        if (m_origin.Z > box.GetMax().Z && m_direction.Z < 0.0f)
        {
            float x = (box.GetMax().Z - m_origin.Z) / m_direction.Z;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.X >= box.GetMin().X && point.X <= box.GetMax().X && point.Y >= box.GetMin().Y && point.Y <= box.GetMax().Y)
                    distance = x;
            }
        }
        return distance;
    }
    public readonly float HitDistance(Plane plane, ref Vector3 intersection_point)
    {
        float d = plane.Normal.Dot(m_direction);
        if (Math.Abs(d) >= float.MinValue)
        {
            float t = -(plane.Normal.Dot(m_origin) + plane.D) / d;
            if (t >= 0.0f)
            {
                intersection_point = m_origin + t * m_direction;
                return t;
            }
            else
                return float.PositiveInfinity;
        }
        else
            return float.PositiveInfinity;
    }
    public readonly float HitDistance(Plane plane)
    {
        float d = plane.Normal.Dot(m_direction);
        if (Math.Abs(d) >= float.MinValue)
        {
            float t = -(plane.Normal.Dot(m_origin) + plane.D) / d;
            if (t >= 0.0f)
                return t;
            else
                return float.PositiveInfinity;
        }
        else
            return float.PositiveInfinity;
    }

    public readonly float HitDistance(Vector3 v1, Vector3 v2, Vector3 v3)
    {
        Vector3 out_normal = Vector3.One;
        Vector3 out_bary = Vector3.One;
        return HitDistance(v1, v2, v3, ref out_normal, ref out_bary);
    }

    public readonly float HitDistance(Vector3 v1, Vector3 v2, Vector3 v3, ref Vector3 out_normal, ref Vector3 out_bary)
    {
        // Based on Fast, Minimum Storage Ray/Triangle Intersection by M�ller & Trumbore
        // http://www.graphics.cornell.edu/pubs/1997/MT97.pdf
        // Calculate edge vectors
        Vector3 edge1 = v2 -v1;
        Vector3 edge2 = v3 -v1;

        // Calculate determinant & check backfacing
        Vector3 p = m_direction.Cross(edge2);
        float det = edge1.Dot(p);

        if (det >= float.MinValue)
        {
            // Calculate u & v parameters and test
            Vector3 t = m_origin -v1;
            float u = t.Dot(p);
            if (u >= 0.0f && u <= det)
            {
                Vector3 q = t.Cross(edge1);
                float v = m_direction.Dot(q);
                if (v >= 0.0f && u + v <= det)
                {
                    float distance = edge2.Dot(q) / det;

                    // Discard hits behind the ray
                    if (distance >= 0.0f)
                    {
                        // There is an intersection, so calculate distance & optional normal
                        out_normal = edge1.Cross(edge2);
                        out_bary = new Vector3(1 - (u / det) - (v / det), u / det, v / det);
                        return distance;
                    }
                }
            }
        }
        return float.PositiveInfinity;
    }

    public readonly float HitDistance(Sphere sphere)
    {
        Vector3 centeredOrigin = m_origin - sphere.Center;
        float squaredRadius = sphere.Radius * sphere.Radius;

        // Check if ray originates inside the sphere
        if (centeredOrigin.LengthSquared() <= squaredRadius)
            return 0.0f;

        // Calculate intersection by quadratic equation
        float a = m_direction.Dot(m_direction);
        float b = 2.0f * centeredOrigin.Dot(m_direction);
        float c = centeredOrigin.Dot(centeredOrigin) - squaredRadius;
        float d = b * b - 4.0f * a * c;

        // No solution
        if (d < 0.0f)
            return float.PositiveInfinity;

        // Get the nearer solution
        float dSqrt = Math.Sqrt(d);
        float dist = (-b - dSqrt) / (2.0f * a);
        if (dist >= 0.0f)
            return dist;
        else
            return (-b + dSqrt) / (2.0f * a);
    }
    public readonly float Distance(Vector3 point)
    {
        Vector3 closest_point = m_origin + (m_direction * (point - m_origin).Dot(m_direction));
        return (closest_point - point).Length();
    }
    public readonly float Distance(Vector3 point,ref Vector3 closest_point)
    {
        closest_point = m_origin + (m_direction * (point - m_origin).Dot(m_direction));
        return (closest_point - point).Length();
    }
    public readonly Vector3 ClosestPoint(Ray ray)
    {
        // Algorithm based on http://paulbourke.net/geometry/lineline3d/
        Vector3 p13 = m_origin - ray.m_origin;
        Vector3 p43 = ray.m_direction;
        Vector3 p21 = m_direction;

        float d1343 = p13.Dot(p43);
        float d4321 = p43.Dot(p21);
        float d1321 = p13.Dot(p21);
        float d4343 = p43.Dot(p43);
        float d2121 = p21.Dot(p21);

        float d = d2121 * d4343 - d4321 * d4321;
        if (Math.Abs(d) < float.MinValue)
            return m_origin;

        float n = d1343 * d4321 - d1321 * d4343;
        float a = n / d;

        return m_origin + a * m_direction;
    }
    public readonly Vector3 GetStart() { return m_origin; }
    public readonly Vector3 GetDirection() { return m_direction; }
    public readonly bool IsDefined() { return m_origin != m_direction && m_direction != Vector3.Zero; }
}

public class RayHitResult
{
    public Entity m_entity;
    public Vector3 m_position;
    public float m_distance;
    public bool m_inside;
    public RayHitResult(Entity entity, Vector3 position, float distance, bool is_inside)
    {
        m_entity = entity;
        m_position = position;
        m_distance = distance;
        m_inside = is_inside;
    }
}