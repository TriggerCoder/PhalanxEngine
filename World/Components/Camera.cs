using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx;
using Silk.NET.Core.Native;
using Silk.NET.Maths;

namespace Phalanx;
public enum ProjectionType
{
    Projection_Perspective,
    Projection_Orthographic,
}
public enum CameraFlags : uint
{
    // fps camera controls
    // x-axis movement: w, a, s, d
    // y-axis movement: q, e
    // mouse look: hold right click to enable
    CanBeControlled         = 1U << 0,
    IsControlled            = 1U << 1,
    WantsCursorHidden       = 1U << 2,
    IsDirty                 = 1U << 3,
    PhysicalBodyAnimation   = 1U << 4, // head bob for walking, breathing etc.
    Flashlight              = 1U << 5, // flashlight on/off
}

[Serializable]
public class Camera : Component
{
    uint m_flags = 0;
    float m_aperture = 5.6f;          // aperture value in f-stop. Controls the amount of light, depth of field and chromatic aberration
    float m_shutter_speed = 1.0f / 125.0f; // length of time for which the camera shutter is open (sec). Also controls the amount of motion blur
    float m_iso = 200.0f;        // sensitivity to light
    float m_fov_horizontal_rad = 90.0f * Math.DegToRad;
    float m_near_plane = 0.1f;
    float m_far_plane = 10000.0f; // a good maximum for a 32 bit reverse-z depth buffer
    ProjectionType m_projection_type = ProjectionType.Projection_Perspective;
    Matrix4x4 m_view = Matrix4x4.Identity;
    Matrix4x4 m_projection = Matrix4x4.Identity;
    Matrix4x4 m_projection_non_reverse_z = Matrix4x4.Identity;
    Matrix4x4 m_view_projection = Matrix4x4.Identity;
    Matrix4x4 m_view_projection_non_reverse_z = Matrix4x4.Identity;
    Matrix4x4 m_matrix_previous = Matrix4x4.Identity;
    Vector2 m_mouse_last_position = Vector2.Zero;
    Vector3 m_movement_speed = Vector3.Zero;
    float m_movement_scroll_accumulator = 0.0f;
    float m_mouse_sensitivity = 0.2f;
    float m_mouse_smoothing = 0.5f;
    bool m_lerp_to_target_p = false;
    bool m_lerp_to_target_r = false;
    bool m_is_walking = false;
    float m_jump_velocity = 0.0f;
    float m_lerp_to_target_alpha = 0.0f;
    float m_lerp_to_target_distance = 0.0f;
    float m_jump_time = 0.0f;
    Vector3 m_lerp_to_target_position = Vector3.Zero;
    Quaternion m_lerp_to_target_rotation = Quaternion.Identity;
    Vector3 m_lerp_from_position = Vector3.Zero;
    Quaternion m_lerp_from_rotation = Quaternion.Identity;
    Entity m_flashlight = null;
    RHI_Viewport m_last_known_viewport;
    Frustum m_frustum;
    List<Entity> m_selected_entities = new List<Entity>();

    // pre-allocated buffers for picking (to avoid heap allocations)
    List<RayHitResult> m_pick_hits;
    List<int> m_pick_indices;
    List<RHI_Vertex_PosTexNorTan> m_pick_vertices;

    public Camera(Entity entity) : base(entity)
    {
        m_entity_owner.SetPosition(new Vector3(0.0f, 3.0f, -5.0f));
        SetFlag(CameraFlags.CanBeControlled, true);
        SetFlag(CameraFlags.PhysicalBodyAnimation, true);
        m_pick_hits.EnsureCapacity(256);
        m_pick_indices.EnsureCapacity(65536);
        m_pick_vertices.EnsureCapacity(65536);
    }
    // matrices
    public Matrix4x4 GetViewMatrix() { return m_view; }
    public Matrix4x4 GetProjectionMatrix() { return m_projection; }
    public Matrix4x4 GetViewProjectionMatrix() { return m_view_projection; }

    // ray casting
    public Ray ComputePickingRay()
    {
        Ray ray;

        ray.m_origin    = GetEntity().GetPosition();
        ray.m_direction = ScreenToWorldCoordinates(Input.GetMousePositionRelativeToEditorViewport(), 1.0f);

        return ray;
    }

    // picks the nearest entity under the mouse cursor
    public void Pick()
    {
        if (!Input.GetMouseIsInViewport())
        {
            ClearSelection();
            return;
        }

        Ray ray = ComputePickingRay();
        m_pick_hits.Clear();

        List<Entity>entities = World.GetEntities();
        foreach (Entity entity in entities)
        {
            if (!entity.GetComponent<Render>())
                continue;

            BoundingBox aabb = entity.GetComponent<Render>().GetBoundingBox();
            float distance = ray.HitDistance(aabb);
            if (float.IsInfinity(distance))
                continue;

            m_pick_hits.Add(new RayHitResult(entity, Vector3.Zero, distance, distance == 0.0f));
        }

        Vector2 cursor = Input.GetMousePosition();
        float best_screen_dist = float.MaxValue;
        float best_depth = float.MaxValue;
        Entity? best_entity = null;

        // mesh-based triangle picking
        if (m_pick_hits.Count != 0)
        {
            foreach (RayHitResult broad_hit in m_pick_hits)
            {
                Render renderable = broad_hit.m_entity.GetComponent<Render>();

                // query mesh size first to reserve exact capacity and avoid allocations
                int index_count = renderable.GetIndexCount();
                int vertex_count = renderable.GetVertexCount();

                // reserve exact capacity needed to avoid heap allocations in GetGeometry.resize()
                // only reserve if current capacity is insufficient
                if (m_pick_indices.Capacity < index_count)
                    m_pick_indices.EnsureCapacity(index_count);
                if (m_pick_vertices.Capacity < vertex_count)
                    m_pick_vertices.EnsureCapacity(vertex_count);

                // clear and reuse pre-allocated buffers 
                m_pick_indices.Clear();
                m_pick_vertices.Clear();

                renderable.GetGeometry(m_pick_indices, m_pick_vertices);
                if ((m_pick_indices.Count == 0) || (m_pick_vertices.Count == 0))
                    continue;

                Matrix4x4 transform = broad_hit.m_entity.GetMatrix();

                for (int i = 0; i < m_pick_indices.Count; i += 3)
                {
                    Vector3 p1 = m_pick_vertices[m_pick_indices[i]].pos;
                    Vector3 p2 = m_pick_vertices[m_pick_indices[i + 1]].pos;
                    Vector3 p3 = m_pick_vertices[m_pick_indices[i + 2]].pos;

                    p1 = Vector3.Transform(p1, transform);
                    p2 = Vector3.Transform(p2, transform);
                    p3 = Vector3.Transform(p3, transform);

                    float distance = ray.HitDistance(p1, p2, p3);
                    if (float.IsInfinity(distance))
                        continue;

                    Vector3 world_hit = ray.GetStart() + ray.GetDirection() * distance;

                    // project to clip space
                    Vector4 clip = Vector4.Transform(new Vector4(world_hit, 1.0f), GetViewProjectionMatrix());
                    if (clip.W == 0.0f)
                        continue;

                    // ndc → screen
                    Vector2 screen_pos = new Vector2(
                        (clip.X / clip.W * 0.5f + 0.5f) * Renderer.GetViewport().Width,
                        (clip.Y / clip.W * 0.5f + 0.5f) * Renderer.GetViewport().Height
                    );

                    float screen_dist = (screen_pos - cursor).Length();

                    // prefer smallest screen distance, then depth
                    if (screen_dist < best_screen_dist || (screen_dist == best_screen_dist && distance < best_depth))
                    {
                        best_screen_dist = screen_dist;
                        best_depth = distance;
                        best_entity = broad_hit.m_entity;
                    }
                }
            }
        }

        // spline control point picking
        {
            float pick_radius_px = 20.0f;
            float best_spline_dist = float.MaxValue;
            Entity? best_spline_entity = null;

            // ray.m_direction is set to a world-space position (from ScreenToWorldCoordinates),
            // not a normalized direction, so compute the actual direction ourselves
            Vector3 ray_origin = ray.GetStart();
            Vector3 ray_dir = ray.GetDirection() - ray_origin;
            ray_dir = ray_dir.Normalized();

            foreach (Entity entity in entities)
            {
                Spline spline = entity.GetComponent<Spline>();
                if (!spline)
                    continue;

                for (int i = 0; i < entity.GetChildrenCount(); i++)
                {
                    Entity? point_entity = entity.GetChildByIndex(i);
                    if (point_entity == null)
                        continue;

                    Vector3 world_pos = point_entity.GetPosition();

                    // depth along the ray direction
                    float depth = (world_pos - ray_origin).Dot(ray_dir);
                    if (depth <= 0.0f)
                        continue;

                    // perpendicular distance from the ray to this point: ||(P - O) x D||
                    Vector3 to_point = world_pos - ray_origin;
                    float distance_from_ray = to_point.Cross(ray_dir).Length();

                    // convert pick radius from screen pixels to world-space at this depth
                    float viewport_width = Renderer.GetViewport().Width;
                    float meters_per_pixel = (2.0f * depth * Math.Tan(GetFovHorizontalRad() * 0.5f)) / viewport_width;
                    float pick_threshold = pick_radius_px * meters_per_pixel;

                    if (distance_from_ray > pick_threshold)
                        continue;
                    if (distance_from_ray < best_spline_dist)
                    {
                        best_spline_dist = distance_from_ray;
                        best_spline_entity = point_entity;
                    }
                }
            }

            if (best_spline_entity != null)
                best_entity = best_spline_entity;
        }

        // handle ctrl for multi-select
        if (best_entity != null)
        {
            if (Input.GetKey(KeyCode.Ctrl_Left) || Input.GetKey(KeyCode.Ctrl_Right))
                ToggleSelection(best_entity);
            else
                SetSelectedEntity(best_entity);
        }
        else
        {
            if (!(Input.GetKey(KeyCode.Ctrl_Left) || Input.GetKey(KeyCode.Ctrl_Right)))
                ClearSelection();
        }
    }

    // converts a world point to a screen point
    public void WorldToScreenCoordinates(Vector3 position_world, ref Vector2 position_screen)
    {
        Vector3 position_clip = Vector3.Transform(position_world, m_view_projection_non_reverse_z);
        // convert clip space position to screen space position
        RHI_Viewport viewport = Renderer.GetViewport();
        float viewport_half_width = viewport.Width * 0.5f;
        float viewport_half_height = viewport.Height * 0.5f;
        position_screen.X = (position_clip.X / position_clip.Z) * viewport_half_width + viewport_half_width;
        position_screen.Y = (position_clip.Y / position_clip.Z) * -viewport_half_height + viewport_half_height;
    }

    // converts a world bounding box to a screen rectangle
    public Rectangle WorldToScreenCoordinates(BoundingBox bounding_box)
    {
        Vector3 min = bounding_box.GetMin();
        Vector3 max = bounding_box.GetMax();

        Vector3[] corners = new Vector3[8];

        corners[0] = min;
        corners[1] = new Vector3(max.X, min.Y, min.Z);
        corners[2] = new Vector3(min.X, max.Y, min.Z);
        corners[3] = new Vector3(max.X, max.Y, min.Z);
        corners[4] = new Vector3(min.X, min.Y, max.Z);
        corners[5] = new Vector3(max.X, min.Y, max.Z);
        corners[6] = new Vector3(min.X, max.Y, max.Z);
        corners[7] = max;

        Rectangle rectangle_screen_Space = new Rectangle();
        Vector2 position_screen_space = Vector2.Zero;

        foreach (Vector3 corner in corners)
        {
            WorldToScreenCoordinates(corner, ref position_screen_space);
            rectangle_screen_Space.Merge(position_screen_space);
        }

        return rectangle_screen_Space;
    }
    // converts a screen point to a world point. Z can be 0.0f to 1.0f and it will lerp between the near and far plane
    Vector3 ScreenToWorldCoordinates(Vector2 position_screen, float z)
    {
        Vector3 position_clip;
        RHI_Viewport viewport = Renderer.GetViewport();
        position_clip.X = (position_screen.X / viewport.Width) * 2.0f - 1.0f;
        position_clip.Y = (position_screen.Y / viewport.Height) * -2.0f + 1.0f;
        position_clip.Z = Math.Clamp(z, 0.0f, 1.0f);

        // compute world space position
        Matrix4x4 view_projection_inverted = m_view_projection_non_reverse_z.Inverted();
        Vector4 position_world = Vector4.Transform(new Vector4(position_clip, 1.0f), view_projection_inverted);

        return new Vector3(position_world.X, position_world.Y, position_world.Z) / position_world.W;
    }

    // aperture
    public float GetAperture() { return m_aperture; }
    public void SetAperture(float aperture) { m_aperture = aperture; }

    // shutter speed
    public float GetShutterSpeed() { return m_shutter_speed; }
    public void SetShutterSpeed(float shutter_speed) { m_shutter_speed = shutter_speed; }

    // iso
    public float GetIso() { return m_iso; }
    public void SetIso(float iso) { m_iso = iso; }

    public float GetExposure()
    {
        // computed ev (using squared aperture for photometric accuracy)
        // note: this calculates the exposure scale factor (1/l_avg)
        float ev100 = Math.Log2((m_aperture * m_aperture) / m_shutter_speed * 100.0f / m_iso);

        // standard standard output sensitivity (sos) calculation
        // 1.2 is a common calibration constant (matches ue5/frostbite)
        // this maps the average scene luminance to middle grey (0.18)
        const float calibration_constant = 1.2f;
        float base_exposure = 1.0f / (calibration_constant * Math.Pow(2.0f, ev100));

        return base_exposure;
    }

    // planes/projection
    public void SetProjection(ProjectionType projection)
    {
        m_projection_type = projection;
        SetFlag(CameraFlags.IsDirty, true);
    }
    public float GetNearPlane() { return m_near_plane; }
    public float GetFarPlane() { return m_far_plane; }
    public ProjectionType GetProjectionType() { return m_projection_type; }

    // fov
    public float GetFovHorizontalRad() { return m_fov_horizontal_rad; }
    public float GetFovVerticalRad() { return 2.0f * Math.Atan(Math.Tan(m_fov_horizontal_rad / 2.0f) * (Renderer.GetViewport().Height / Renderer.GetViewport().Width)); }
    public float GetFovHorizontalDeg() { return m_fov_horizontal_rad * Math.RadToDeg; }
    public void SetFovHorizontalDeg(float fov)
    {
        m_fov_horizontal_rad = fov * Math.DegToRad;
        SetFlag(CameraFlags.IsDirty, true);
    }
    public float GetAspectRatio() { return Renderer.GetViewport().GetAspectRatio(); }

    // frustum
    public bool IsInViewFrustum(BoundingBox bounding_box)
    {
        Vector3 center = bounding_box.GetCenter();
        Vector3 extents = bounding_box.GetExtents();

        return m_frustum.IsVisible(center, extents);
    }
    public bool IsInViewFrustum(Render renderable)
    {
        BoundingBox  box = renderable.GetBoundingBox();
        return IsInViewFrustum(box);
    }

    // flags
    public bool GetFlag(CameraFlags flag) { return (m_flags & (uint)flag) != 0; }
    public void SetFlag(CameraFlags flag, bool enable)
    {
        bool flag_present = (m_flags & (uint)flag) != 0;

        if (enable && !flag_present)
            m_flags |= (uint)(flag);
        else if (!enable && flag_present)
            m_flags &= ~(uint)(flag);
    }

    // misc
    bool IsWalking() { return m_is_walking; }

    // selection - single entity (for compatibility)
    public void SetSelectedEntity(Entity? entity)
    {
        m_selected_entities.Clear();
        if (entity != null)
            m_selected_entities.Add(entity);
    }
    public Entity? GetSelectedEntity()
    {
        return (m_selected_entities.Count == 0) ? null : m_selected_entities[0];
    }

    // selection - multiple entities
    public void AddToSelection(Entity? entity)
    {
        if (entity == null)
            return;

        // check if already selected
        foreach (Entity e in m_selected_entities)
        {
            if ((e != null) && (e.GetObjectId() == entity.GetObjectId()))
                return;
        }

        m_selected_entities.Add(entity);
    }
    public void RemoveFromSelection(Entity? entity)
    {
        if (entity == null)
            return;

        m_selected_entities.RemoveAll(e => e.GetObjectId() == entity.GetObjectId());
    }
    public void ToggleSelection(Entity? entity)
    {
        if (entity == null)
            return;

        if (IsSelected(entity))
            RemoveFromSelection(entity);
        else
            AddToSelection(entity);
    }
    public void ClearSelection() { m_selected_entities.Clear(); }
    public bool IsSelected(Entity? entity)
    {
        if (entity == null)
            return false;

        foreach (Entity e in m_selected_entities)
        {
            if ((e != null) && e.GetObjectId() == entity.GetObjectId())
                return true;
        }
        return false;
    }
    public List<Entity> GetSelectedEntities() { return m_selected_entities; }
    public int GetSelectedEntityCount() { return m_selected_entities.Count; }

    public Matrix4x4 UpdateViewMatrix()
    {
        // extract basis vectors directly from world matrix to avoid quaternion decomposition instability
        // row-major layout: row 0 = right (X), row 1 = up (Y), row 2 = forward (Z), row 3 = translation
        Matrix4x4 m = GetEntity().GetMatrix();

        Vector3 position = new Vector3(m.M41, m.M42, m.M43);
        Vector3 forward = new Vector3(m.M31, m.M32, m.M33).Normalized();
        Vector3 up = new Vector3(m.M21, m.M22, m.M23).Normalized();

        // compute view matrix
        return Matrix4x4.CreateLookAtLeftHanded(position, position + forward, up);
    }
    public Matrix4x4 ComputeProjection(float near_plane, float far_plane)
    {
        if (m_projection_type == ProjectionType.Projection_Perspective)
            return Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(GetFovVerticalRad(), GetAspectRatio(), near_plane, far_plane);
        else if (m_projection_type == ProjectionType.Projection_Orthographic)
            return Matrix4x4.CreateOrthographicLeftHanded(Renderer.GetViewport().Width, Renderer.GetViewport().Height, near_plane, far_plane);

        return Matrix4x4.Identity;
    }

    public void FocusOnSelectedEntity()
    {
        // only do this in editor mode
        if (Engine.IsFlagSet(EngineMode.Playing))
            return;

        if (GetSelectedEntity() is Entity entity)
        {
            Log.LogInfo("Focusing on entity " + entity.GetObjectName() + "...");

            m_lerp_to_target_position = entity.GetPosition();
            Vector3 target_direction = (m_lerp_to_target_position - GetEntity().GetPosition()).Normalized();

            // if the entity has a renderable component, we can get a more accurate target position
            // ...otherwise we apply a simple offset so that the rotation vector doesn't suffer
            if (entity.GetComponent<Render>() is Render renderable)
                m_lerp_to_target_position -= target_direction * renderable.GetBoundingBox().GetExtents().Length() * 2.0f;
            else
                m_lerp_to_target_position -= target_direction;
            Helpers.PLXAssert(!float.IsNaN(m_lerp_to_target_distance));

            // store start state so the lerp interpolates between two fixed endpoints
            m_lerp_from_position = GetEntity().GetPosition();
            m_lerp_from_rotation = GetEntity().GetRotation();
            m_lerp_to_target_alpha = 0.0f;
            m_movement_speed = Vector3.Zero;
            m_lerp_to_target_rotation = QuaternionExtensions.FromLookRotation(entity.GetPosition() - m_lerp_to_target_position).Normalized();
            m_lerp_to_target_distance = Vector3.Distance(m_lerp_to_target_position, m_lerp_from_position);

            float lerp_angle = Math.Acos(Quaternion.Dot(m_lerp_to_target_rotation.Normalized(), m_lerp_from_rotation.Normalized())) * Math.RadToDeg;

            m_lerp_to_target_p = m_lerp_to_target_distance > 0.1f ? true : false;
            m_lerp_to_target_r = lerp_angle > 1.0f ? true : false;
        }
    }
}