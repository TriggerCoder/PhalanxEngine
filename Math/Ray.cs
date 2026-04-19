using System;

namespace Phalanx;

[Serializable]
public struct Ray
{
    private Vector3 m_origin = Vector3.Zero;
    private Vector3 m_direction = Vector3.Zero;

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
        if (m_origin.x < box.GetMin().x && m_direction.x > 0.0f)
        {
            float x = (box.GetMin().x - m_origin.x) / m_direction.x;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.y >= box.GetMin().y && point.y <= box.GetMax().y && point.z >= box.GetMin().z && point.z <= box.GetMax().z)
                    distance = x;
            }
        }
        if (m_origin.x > box.GetMax().x && m_direction.x < 0.0f)
        {
            float x = (box.GetMax().x - m_origin.x) / m_direction.x;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.y >= box.GetMin().y && point.y <= box.GetMax().y && point.z >= box.GetMin().z && point.z <= box.GetMax().z)
                    distance = x;
            }
        }

        // Check for intersecting in the Y-direction
        if (m_origin.y < box.GetMin().y && m_direction.y > 0.0f)
        {
            float x = (box.GetMin().y - m_origin.y) / m_direction.y;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.x >= box.GetMin().x && point.x <= box.GetMax().x && point.z >= box.GetMin().z && point.z <= box.GetMax().z)
                    distance = x;
            }
        }
        if (m_origin.y > box.GetMax().y && m_direction.y < 0.0f)
        {
            float x = (box.GetMax().y - m_origin.y) / m_direction.y;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.x >= box.GetMin().x && point.x <= box.GetMax().x && point.z >= box.GetMin().z && point.z <= box.GetMax().z)
                    distance = x;
            }
        }

        // Check for intersecting in the Z-direction
        if (m_origin.z < box.GetMin().z && m_direction.z > 0.0f)
        {
            float x = (box.GetMin().z - m_origin.z) / m_direction.z;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.x >= box.GetMin().x && point.x <= box.GetMax().x && point.y >= box.GetMin().y && point.y <= box.GetMax().y)
                    distance = x;
            }
        }
        if (m_origin.z > box.GetMax().z && m_direction.z < 0.0f)
        {
            float x = (box.GetMax().z - m_origin.z) / m_direction.z;
            if (x < distance)
            {
                Vector3 point = m_origin + x * m_direction;
                if (point.x >= box.GetMin().x && point.x <= box.GetMax().x && point.y >= box.GetMin().y && point.y <= box.GetMax().y)
                    distance = x;
            }
        }
        return distance;
    }
    public readonly float HitDistance(Plane plane, ref Vector3 intersection_point)
    {
        float d = plane.normal.Dot(m_direction);
        if (Math.Abs(d) >= float.MinValue)
        {
            float t = -(plane.normal.Dot(m_origin) + plane.d) / d;
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
        float d = plane.normal.Dot(m_direction);
        if (Math.Abs(d) >= float.MinValue)
        {
            float t = -(plane.normal.Dot(m_origin) + plane.d) / d;
            if (t >= 0.0f)
                return t;
            else
                return float.PositiveInfinity;
        }
        else
            return float.PositiveInfinity;
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
        Vector3 centeredOrigin = m_origin - sphere.center;
        float squaredRadius = sphere.radius * sphere.radius;

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