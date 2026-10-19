// The two shadow cascades of a directional light as one view sees them: Light::UpdateViewMatrix and
// UpdateProjectionMatrix (source/world/components/Light.cpp @ 4c07baa, the directional branches, constants at
// :39-44), moved out of the Light component because both cascades follow the camera. Every SceneView owns one set per
// directional light (ViewState.Cascades), the renderer fills it after it packs the shadow atlas.
// Deviations:
// - Spartan computes them inside the light from Renderer::GetCamera() (one camera). The maths is unchanged.
// - Spartan divides by a zero rectangle width before the cascade is packed and snaps to nan; an unpacked cascade isn't
//   snapped here (the same as Phalanx's Light did before).
// - Spartan uses world up unconditionally, a sun pointing straight up or down collapses the basis; the light's own up
//   is used then.
// - Without a camera the view matrices keep their last value, like Light::UpdateViewMatrix's early return.
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;

namespace Phalanx.Rendering;

internal sealed class DirectionalCascades
{
    // Light.cpp:39-44
    internal const float cascade_near_extent  = 64.0f;
    internal const float cascade_far_extent   = 2000.0f;
    internal const float cascade_depth_behind = 2000.0f;
    internal const float cascade_depth_ahead  = 8000.0f;
    internal const float cascade_depth        = cascade_depth_behind + cascade_depth_ahead;
    internal const int   Count                = 2;

    private readonly Matrix4x4[] m_view       = { Matrix4x4.Identity, Matrix4x4.Identity };
    private readonly Matrix4x4[] m_projection = { Matrix4x4.Identity, Matrix4x4.Identity };
    private readonly Frustum[] m_frustums     = new Frustum[Count];
    private readonly Rectangle[] m_atlas_rectangles = new Rectangle[Count];
    private readonly Vector2[] m_atlas_offsets      = new Vector2[Count];
    private readonly Vector2[] m_atlas_scales       = new Vector2[Count];

    public Matrix4x4 GetViewMatrix(uint cascade) => m_view[cascade];
    public Matrix4x4 GetProjectionMatrix(uint cascade) => m_projection[cascade];
    public Matrix4x4 GetViewProjectionMatrix(uint cascade) => m_view[cascade] * m_projection[cascade]; // Light.h:183
    public Rectangle GetAtlasRectangle(uint cascade) => m_atlas_rectangles[cascade];
    public Vector2 GetAtlasOffset(uint cascade) => m_atlas_offsets[cascade];
    public Vector2 GetAtlasScale(uint cascade) => m_atlas_scales[cascade];

    // Light::SetAtlasRectangle / ClearAtlasRectangles
    public void SetAtlasRectangle(uint cascade, Rectangle rectangle, float atlas_width, float atlas_height)
    {
        m_atlas_rectangles[cascade] = rectangle;
        m_atlas_offsets[cascade]    = new Vector2(rectangle.X / atlas_width, rectangle.Y / atlas_height);
        m_atlas_scales[cascade]     = new Vector2(rectangle.Width / atlas_width, rectangle.Height / atlas_height);
    }

    public void ClearAtlasRectangles()
    {
        Array.Fill(m_atlas_rectangles, Rectangle.Zero);
        Array.Fill(m_atlas_offsets, Vector2.Zero);
        Array.Fill(m_atlas_scales, Vector2.Zero);
    }

    // Light::IsInViewFrustum for a directional light: orthographic, the depth is ignored
    public bool IsInViewFrustum(Render renderable, uint cascade)
    {
        BoundingBox bounding_box = renderable.GetBoundingBox();
        return m_frustums[cascade].IsVisible(bounding_box.GetCenter(), bounding_box.GetExtents(), true);
    }

    // Light::UpdateViewMatrix and UpdateProjectionMatrix, the directional branches, for the camera of one view
    public void Update(Light light, Camera? camera, float atlas_width)
    {
        Entity entity = light.GetEntity()!;
        if (camera is not null)
        {
            // both cascades follow the camera, most of the depth sits ahead so the ground stays in range when flying
            Vector3 camera_position = camera.GetEntity()!.GetPosition();
            Vector3 light_forward   = entity.GetForward();
            Vector3 position        = camera_position - light_forward * cascade_depth_behind;
            Vector3 up = MathF.Abs(Vector3.Dot(light_forward, MathEx.Vector3.Up)) > 0.9999f ? entity.GetUp() : MathEx.Vector3.Up;
            m_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, camera_position, up);
            m_view[1] = m_view[0];

            // move the light in world units per texel to avoid shimmering
            Span<float> extents = stackalloc float[2] { cascade_near_extent, cascade_far_extent };
            for (int i = 0; i < Count; i++)
            {
                float rect_width = m_atlas_rectangles[i].Width;               // cascade rectangle width in atlas
                if (rect_width <= 0.0f)
                    continue;                                                 // not packed yet
                float atlas_scale      = rect_width / atlas_width;            // proportion of atlas used by cascade
                float effective_res    = atlas_width * atlas_scale;           // effective resolution for cascade
                float texel_size_world = (2.0f * extents[i]) / effective_res; // world units per texel
                m_view[i].M41 = MathEx.Round(m_view[i].M41 / texel_size_world) * texel_size_world; // snap X
                m_view[i].M42 = MathEx.Round(m_view[i].M42 / texel_size_world) * texel_size_world; // snap Y
                // z-translation (M43) remains unchanged for orthographic projection
            }
        }

        // near cascade (tight, camera following)
        m_projection[0] = Matrix4x4Extensions.CreateOrthoOffCenterLH(-cascade_near_extent, cascade_near_extent,
                                                                    -cascade_near_extent, cascade_near_extent, cascade_depth, 0.0f);
        // far cascade (camera following, fixed size, bigger than near cascade)
        m_projection[1] = Matrix4x4Extensions.CreateOrthoOffCenterLH(-cascade_far_extent, cascade_far_extent,
                                                                    -cascade_far_extent, cascade_far_extent, cascade_depth, 0.0f);
        m_frustums[0] = new Frustum(m_view[0], m_projection[0]);
        m_frustums[1] = new Frustum(m_view[1], m_projection[1]);
    }
}
