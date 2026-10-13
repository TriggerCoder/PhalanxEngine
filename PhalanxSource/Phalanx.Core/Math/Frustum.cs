using System;
using System.Numerics;

namespace Phalanx;

[Serializable]
public struct Frustum
{
    private Plane[] m_planes;

    public Frustum(Matrix4x4 view, Matrix4x4 projection)
    {
        m_planes = new Plane[6];
        Matrix4x4 view_projection = view * projection;

        // near plane
        m_planes[0].Normal.X = view_projection.M14 + view_projection.M13;
        m_planes[0].Normal.Y = view_projection.M24 + view_projection.M23;
        m_planes[0].Normal.Z = view_projection.M34 + view_projection.M33;
        m_planes[0].D = view_projection.M44 + view_projection.M43;
        m_planes[0].Normalize();

        // far plane
        m_planes[1].Normal.X = view_projection.M14 - view_projection.M13;
        m_planes[1].Normal.Y = view_projection.M24 - view_projection.M23;
        m_planes[1].Normal.Z = view_projection.M34 - view_projection.M33;
        m_planes[1].D = view_projection.M44 - view_projection.M43;
        m_planes[1].Normalize();

        // left plane
        m_planes[2].Normal.X = view_projection.M14 + view_projection.M11;
        m_planes[2].Normal.Y = view_projection.M24 + view_projection.M21;
        m_planes[2].Normal.Z = view_projection.M34 + view_projection.M31;
        m_planes[2].D = view_projection.M44 + view_projection.M41;
        m_planes[2].Normalize();

        // right plane
        m_planes[3].Normal.X = view_projection.M14 - view_projection.M11;
        m_planes[3].Normal.Y = view_projection.M24 - view_projection.M21;
        m_planes[3].Normal.Z = view_projection.M34 - view_projection.M31;
        m_planes[3].D = view_projection.M44 - view_projection.M41;
        m_planes[3].Normalize();

        // top plane
        m_planes[4].Normal.X = view_projection.M14 - view_projection.M12;
        m_planes[4].Normal.Y = view_projection.M24 - view_projection.M22;
        m_planes[4].Normal.Z = view_projection.M34 - view_projection.M32;
        m_planes[4].D = view_projection.M44 - view_projection.M42;
        m_planes[4].Normalize();

        // bottom plane
        m_planes[5].Normal.X = view_projection.M14 + view_projection.M12;
        m_planes[5].Normal.Y = view_projection.M24 + view_projection.M22;
        m_planes[5].Normal.Z = view_projection.M34 + view_projection.M32;
        m_planes[5].D = view_projection.M44 + view_projection.M42;
        m_planes[5].Normalize();
    }

    public readonly bool IsVisible(Vector3 center, Vector3 extent, bool ignore_depth = false) { return CheckCube(center, extent, ignore_depth) != Math.Intersection.Outside; }

    internal readonly Math.Intersection CheckCube(Vector3 center, Vector3 extent, bool ignore_depth)
    {
        Helpers.PLXAssert(!center.IsNaN() && !extent.IsNaN());

        bool intersects = false;

        // skip near and far plane checks if depth is to be ignored
        int start = ignore_depth ? 2 : 0;

        for (int i = start; i < 6; i++)
        {
            Plane plane = m_planes[i];

            // signed distance from cube center to plane
            float d = Vector3.Dot(plane.Normal, center) + plane.D;

            // projected radius of cube on plane Normal
            float r = Vector3.Dot(plane.Normal.Abs(), extent);

            // if the cube is completely outside any plane, then it's outside
            if (d + r < 0.0f)
                return Math.Intersection.Outside;

            // if the cube intersects the plane, mark as intersecting
            if (d - r < 0.0f)
                intersects = true;
        }

        // return the final classification
        return intersects ? Math.Intersection.Intersects : Math.Intersection.Inside;
    }

    internal readonly Math.Intersection CheckSphere(Vector3 center, float radius, bool ignore_depth)
    {
        Helpers.PLXAssert(!center.IsNaN() && radius > 0.0f);

        // skip near and far plane checks if depth is to be ignored
        int start = ignore_depth ? 2 : 0;

        // calculate our distances to each of the planes
        for (int i = start; i < 6; i++)
        {
            Plane plane = m_planes[i];

            // find the distance to this plane
            float distance = Vector3.Dot(plane.Normal, center) + plane.D;

            // if this distance is < -sphere.radius, we are outside
            if (distance < -radius)
                return Math.Intersection.Outside;

            // else if the distance is between +- radius, then we intersect
            if (distance < radius)
                return Math.Intersection.Intersects;
        }

        // otherwise we are fully in view
        return Math.Intersection.Inside;
    }
}