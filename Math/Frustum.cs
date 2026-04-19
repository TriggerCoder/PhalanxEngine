using System;
using System.Numerics;

namespace Phalanx;

[Serializable]
public struct Frustum
{
    private Plane[] m_planes;

    public Frustum(Matrix view, Matrix projection)
    {
        m_planes = new Plane[6];
        Matrix view_projection = view * projection;

        // near plane
        m_planes[0].normal.x = view_projection.m03 + view_projection.m02;
        m_planes[0].normal.y = view_projection.m13 + view_projection.m12;
        m_planes[0].normal.z = view_projection.m23 + view_projection.m22;
        m_planes[0].d = view_projection.m33 + view_projection.m32;
        m_planes[0].Normalize();

        // far plane
        m_planes[1].normal.x = view_projection.m03 - view_projection.m02;
        m_planes[1].normal.y = view_projection.m13 - view_projection.m12;
        m_planes[1].normal.z = view_projection.m23 - view_projection.m22;
        m_planes[1].d = view_projection.m33 - view_projection.m32;
        m_planes[1].Normalize();

        // left plane
        m_planes[2].normal.x = view_projection.m03 + view_projection.m00;
        m_planes[2].normal.y = view_projection.m13 + view_projection.m10;
        m_planes[2].normal.z = view_projection.m23 + view_projection.m20;
        m_planes[2].d = view_projection.m33 + view_projection.m30;
        m_planes[2].Normalize();

        // right plane
        m_planes[3].normal.x = view_projection.m03 - view_projection.m00;
        m_planes[3].normal.y = view_projection.m13 - view_projection.m10;
        m_planes[3].normal.z = view_projection.m23 - view_projection.m20;
        m_planes[3].d = view_projection.m33 - view_projection.m30;
        m_planes[3].Normalize();

        // top plane
        m_planes[4].normal.x = view_projection.m03 - view_projection.m01;
        m_planes[4].normal.y = view_projection.m13 - view_projection.m11;
        m_planes[4].normal.z = view_projection.m23 - view_projection.m21;
        m_planes[4].d = view_projection.m33 - view_projection.m31;
        m_planes[4].Normalize();

        // bottom plane
        m_planes[5].normal.x = view_projection.m03 + view_projection.m01;
        m_planes[5].normal.y = view_projection.m13 + view_projection.m11;
        m_planes[5].normal.z = view_projection.m23 + view_projection.m21;
        m_planes[5].d = view_projection.m33 + view_projection.m31;
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
            float d = Vector3.Dot(plane.normal, center) + plane.d;

            // projected radius of cube on plane normal
            float r = Vector3.Dot(plane.normal.Abs(), extent);

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
            float distance = Vector3.Dot(plane.normal, center) + plane.d;

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