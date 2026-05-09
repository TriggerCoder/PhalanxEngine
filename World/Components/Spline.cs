using System;
using System.Numerics;

namespace Phalanx;

// cross-section profile that gets extruded along the spline
public enum SplineProfile : uint
{
    Road,    // flat strip
    Wall,    // vertical quad
    Tube,    // circular cross-section
    Fence,   // thin tall rectangle
    Channel, // u-shaped gutter/trench
    Max
}

[Serializable]
public class Spline : Component
{
    // prefix used to identify control point child entities
    private const string prefix_control_point = "spline_point_";
    private const string prefix_instance = "spline_instance_";

    // spline
    private bool m_closed_loop = false;
    private uint m_resolution = 20;
    private float m_road_width = 8.0f;
    private bool m_needs_road_regeneration = false;
    private bool m_mesh_enabled = false;

    // profile
    private SplineProfile m_profile = SplineProfile.Road;
    private float m_height = 3.0f;
    private float m_thickness = 0.3f;
    private uint m_tube_sides = 12;

    // width variation
    private float m_road_width_end = 8.0f;

    // uv tiling
    private float m_uv_tiling_u = 1.0f;
    private float m_uv_tiling_v = 1.0f;

    // sidewalk/curb
    private bool m_sidewalk_enabled = false;
    private float m_sidewalk_width = 2.0f;
    private float m_curb_height = 0.15f;

    // terrain conforming
    private bool m_conform_to_terrain = false;
    private float m_terrain_offset = 0.01f;

    // instancing
    private float m_instance_spacing = 5.0f;
    private bool m_align_instances_to_spline = true;
    private string m_instance_mesh_path = string.Empty;
    private float m_instance_random_offset = 0.0f;
    private float m_instance_random_scale_min = 1.0f;
    private float m_instance_random_scale_max = 1.0f;
    private float m_instance_random_yaw = 0.0f;

    // material name to restore after mesh regeneration
    private string m_saved_material_name = string.Empty;

    // generated mesh
    private Mesh m_mesh;

    // snapshot of previous state for auto-regeneration
    private bool m_prev_closed_loop = false;
    private uint m_prev_resolution = 0;
    private float m_prev_road_width = 0.0f;
    private float m_prev_road_width_end = 0.0f;
    private SplineProfile m_prev_profile = SplineProfile.Road;
    private float m_prev_height = 0.0f;
    private float m_prev_thickness = 0.0f;
    private uint m_prev_tube_sides = 0;
    private float m_prev_uv_tiling_u = 0.0f;
    private float m_prev_uv_tiling_v = 0.0f;
    private bool m_prev_sidewalk_enabled = false;
    private float m_prev_sidewalk_width = 0.0f;
    private float m_prev_curb_height = 0.0f;
    private bool m_prev_conform_to_terrain = false;
    private float m_prev_terrain_offset = 0.0f;
    private List<Vector3> m_prev_control_points = new List<Vector3>();

    public Spline(Entity entity) : base(entity) { }
    ~Spline()
    {
        ClearRoadMesh();

        // don't call ClearInstances() here because during destruction the world's entity
        // list may contain dangling pointers (e.g. shutdown deletes entities in a loop),
        // and RemoveEntity -> AcquireChildren would iterate over freed memory.
        // the world already removes all descendants when an entity is removed or shut down.
    }

    // gather control point world positions from child entities
    private List<Vector3> GetControlPoints()
    {
        List<Vector3> points = new List<Vector3>();

        if (m_entity_owner == null)
            return points;

        int child_count = m_entity_owner.GetChildrenCount();
        points.EnsureCapacity(child_count);

        for (int i = 0; i < child_count; i++)
        {
            Entity? child = m_entity_owner.GetChildByIndex(i);
            if ((child != null) && (child.GetObjectName().StartsWith(prefix_control_point)))
            {
                // only include control point children, not instances
                points.Add(child.GetPosition());
            }
        }
        return points;
    }

    // gather control point positions local to the spline entity
    private List<Vector3> GetControlPointsLocal()
    {
        List<Vector3> points = new List<Vector3>();

        if (m_entity_owner == null)
            return points;

        int child_count = m_entity_owner.GetChildrenCount();
        points.EnsureCapacity(child_count);

        for (int i = 0; i < child_count; i++)
        {
            if (m_entity_owner.GetChildByIndex(i) is Entity child)
            {
                // only include control point children, not instances
                if (child.GetObjectName().StartsWith(prefix_control_point))
                {
                    points.Add(child.GetPositionLocal());
                }
            }
        }
        return points;
    }

    // resolve the current profile into a set of 2d cross-section points (in right-up plane)
    private List<Vector2> GetProfilePoints() { return GetProfilePointsForWidth(m_road_width); }
    private List<Vector2> GetProfilePointsForWidth(float width)
    {
        List<Vector2> profile = new List<Vector2>();
        float half_width = width * 0.5f;
        float half_thickness = m_thickness * 0.5f;

        switch (m_profile)
        {
            case SplineProfile.Road:
            {
                if (m_sidewalk_enabled)
                {
                    // left sidewalk outer -> curb drop -> road -> curb rise -> right sidewalk outer
                    float outer_left = -(half_width + m_sidewalk_width);
                    float outer_right = (half_width + m_sidewalk_width);
                    profile.Add(new Vector2(outer_left, m_curb_height));
                    profile.Add(new Vector2(-half_width, m_curb_height));
                    profile.Add(new Vector2(-half_width, 0.0f));
                    profile.Add(new Vector2(half_width, 0.0f));
                    profile.Add(new Vector2(half_width, m_curb_height));
                    profile.Add(new Vector2(outer_right, m_curb_height));
                }
                else
                {
                    profile.Add(new Vector2(-half_width, 0.0f));
                    profile.Add(new Vector2(half_width, 0.0f));
                }
            }
            break;
            case SplineProfile.Wall:
                profile.Add(new Vector2(-half_thickness, 0.0f));
                profile.Add(new Vector2(-half_thickness, m_height));
                profile.Add(new Vector2(half_thickness, m_height));
                profile.Add(new Vector2(half_thickness, 0.0f));
            break;
            case SplineProfile.Tube:
            {
                int sides = Math.Max(3, (int)m_tube_sides);
                for (int i = 0; i < sides; i++)
                {
                    float angle = ((float)(i) / (float)(sides)) * 2.0f * Math.Pi;
                    float x = Math.Cos(angle) * half_width;
                    float y = Math.Sin(angle) * half_width;
                    profile.Add(new Vector2(x, y));
                }
            }
            break;
            case SplineProfile.Fence:
                profile.Add(new Vector2(-half_thickness, 0.0f));
                profile.Add(new Vector2(-half_thickness, m_height));
                profile.Add(new Vector2(half_thickness, m_height));
                profile.Add(new Vector2(half_thickness, 0.0f));
            break;
            case SplineProfile.Channel:
                profile.Add(new Vector2(-half_width, m_height));
                profile.Add(new Vector2(-half_width, 0.0f));
                profile.Add(new Vector2(half_width, 0.0f));
                profile.Add(new Vector2(half_width, m_height));
            break;
            default:
                profile.Add(new Vector2(-half_width, 0.0f));
                profile.Add(new Vector2(half_width, 0.0f));
            break;
        }

        return profile;
    }

    // whether the current profile forms a closed loop cross-section (e.g. tube)
    public bool IsProfileClosed() { return m_profile == SplineProfile.Tube; }
    private void MapToSpan(float t, List<Vector3> points, uint span_index, float local_t)
    {
        uint span_count = m_closed_loop ? (uint)(points.Count) : (uint)(points.Count) - 1;

        // clamp t to [0, 1]
        t = Math.Max(0.0f, Math.Min(1.0f, t));

        // scale t to span range
        float scaled_t = t * (float)(span_count);
        span_index = (uint)(scaled_t);
        local_t = scaled_t - (float)(span_index);

        // handle the edge case where t = 1.0
        if (span_index >= span_count)
        {
            span_index = span_count - 1;
            local_t = 1.0f;
        }
    }
    private Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;

        // catmull-rom matrix form
        return 0.5f *  (
                        (2.0f * p1) +
                        (-p0 + p2) * t +
                        (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 +
                        (-p0 + 3.0f * p1 - 3.0f * p2 + p3) * t3
                       );
    }

    private Vector3 CatmullRomTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;

        // first derivative of the catmull-rom formula
        return 0.5f *  (
                        (-p0 + p2) +
                        (4.0f * p0 - 10.0f * p1 + 8.0f * p2 - 2.0f * p3) * t +
                        (-3.0f * p0 + 9.0f * p1 - 9.0f * p2 + 3.0f * p3) * t2
                       );
    }
    private Vector3 EvaluatePoint(List<Vector3> points, float t)
    {
        if (points.Count == 0)
            return Vector3.Zero;
        if (points.Count == 1)
            return points[0];

        uint span_index = 0;
        float local_t = 0.0f;
        MapToSpan(t, points, span_index, local_t);

        int point_count = points.Count;
        int i1 = (int)(span_index);
        int i2 = m_closed_loop ? (i1 + 1) % point_count : Math.Min(i1 + 1, point_count - 1);
        int i0 = m_closed_loop ? (i1 - 1 + point_count) % point_count : Math.Max(i1 - 1, 0);
        int i3 = m_closed_loop ? (i2 + 1) % point_count : Math.Min(i2 + 1, point_count - 1);

        return CatmullRom(points[i0], points[i1], points[i2], points[i3], local_t);
    }
    private Vector3 EvaluateTangent(List<Vector3> points, float t)
    {
        if (points.Count < 2)
            return Math.Vector3.Forward;

        uint span_index = 0;
        float local_t = 0.0f;
        MapToSpan(t, points, span_index, local_t);

        int point_count = points.Count;
        int i1 = (int)span_index;
        int i2 = m_closed_loop ? (i1 + 1) % point_count : Math.Min(i1 + 1, point_count - 1);
        int i0 = m_closed_loop ? (i1 - 1 + point_count) % point_count : Math.Max(i1 - 1, 0);
        int i3 = m_closed_loop ? (i2 + 1) % point_count : Math.Min(i2 + 1, point_count - 1);

        Vector3 tangent = CatmullRomTangent(points[i0], points[i1], points[i2], points[i3], local_t);

        return Vector3.Normalize(tangent);
    }

    // evaluation - t is normalized [0, 1] across the entire spline
    public Vector3 GetPoint(float t) { return EvaluatePoint(GetControlPoints(), t); }
    public Vector3 GetTangent(float t) { return EvaluateTangent(GetControlPoints(), t); }
    public float GetLength(uint samples_per_span = 10)
    {
        List<Vector3> points = GetControlPoints();
        if (points.Count < 2)
            return 0.0f;

        uint span_count = m_closed_loop ? (uint)(points.Count) : (uint)(points.Count) - 1;
        uint total_samples = span_count * samples_per_span;
        float length = 0.0f;
        Vector3 prev_point = EvaluatePoint(points, 0.0f);

        for (int i = 1; i <= total_samples; i++)
        {
            float t = (float)(i) / (float)(total_samples);
            Vector3 curr_point = EvaluatePoint(points, t);
            length += prev_point.Distance(curr_point);
            prev_point = curr_point;
        }
        return length;
    }

    // control point management (children of the owning entity)
    public uint GetControlPointCount()
    {
        if (m_entity_owner == null)
            return 0;

        // count only children that are control points (not instances)
        uint count = 0;
        int child_count = m_entity_owner.GetChildrenCount();
        for (int i = 0; i < child_count; i++)
        {
            Entity? child = m_entity_owner.GetChildByIndex(i);
            if ((child != null) && (child.GetObjectName().StartsWith(prefix_control_point)))
                count++;
        }
        return count;
    }
    public void AddControlPoint(Vector3 local_position)
    {
        if (m_entity_owner == null)
            return;

        Entity point = World.CreateEntity();

        // name the point based on its index
        uint index = GetControlPointCount();
        point.SetObjectName(prefix_control_point + index.ToString());
        point.SetParent(m_entity_owner);
        point.SetPositionLocal(local_position);
    }
    public void RemoveLastControlPoint()
    {
        if ((m_entity_owner == null) || (m_entity_owner.GetChildrenCount() == 0))
            return;

        // find the last control point child (not an instance)
        Entity? last_point = null;
        int child_count = m_entity_owner.GetChildrenCount();
        for (int i = child_count; i > 0; i--)
        {
            if (m_entity_owner.GetChildByIndex(i - 1) is Entity child)
            {
                if (child.GetObjectName().StartsWith(prefix_control_point))
                {
                    last_point = child;
                    break;
                }
            }
        }

        if (last_point != null)
            World.RemoveEntity(last_point);
    }

    // mesh generation - extrudes the current profile along the spline
    public void GenerateRoadMesh()
    {
        // need at least 2 control points
        List<Vector3> spline_points = GetControlPointsLocal();
        if (spline_points.Count < 2)
        {
            Log.LogWarning("need at least 2 control points to generate a mesh");
            return;
        }

        // preserve the user-assigned material before clearing the old mesh
        if ((m_entity_owner != null) && (m_saved_material_name.Length == 0))
        {
            if (m_entity_owner.GetComponent<Render>() is Render renderable)
            {
                if (renderable.GetMaterial() is Material material)
                    m_saved_material_name = material.GetObjectName();
            }
        }

        // clean up any previous mesh
        ClearRoadMesh();

        // resolve the profile and extrude it along the spline
        List<Vector2> profile_points = GetProfilePoints();
        bool close_profile = IsProfileClosed();
        GenerateMesh(spline_points, profile_points, close_profile);
    }
    public void ClearRoadMesh()
    {
        if (m_mesh != null)
        {
            // preserve the current material so it can be restored on next regeneration
            if ((m_entity_owner != null) && (m_saved_material_name.Length == 0))
            {
                if (m_entity_owner.GetComponent<Render>() is Render renderable)
                {
                    if (renderable.GetMaterial() is Material material)
                        m_saved_material_name = material.GetObjectName();
                }
            }

            // remove the renderable and physics components to avoid dangling mesh pointers
            if (m_entity_owner != null)
            {
                m_entity_owner.RemoveComponent<Physics>();
                m_entity_owner.RemoveComponent<Render>();
            }
            m_mesh.reset();
        }
    }
    public bool HasRoadMesh() { return m_mesh != null; }

    // mesh generation toggle
    public bool GetMeshEnabled() { return m_mesh_enabled; }
    public void SetMeshEnabled(bool enabled) { m_mesh_enabled = enabled; }

    // instanced mesh placement along the spline
    public void SpawnInstances()
    {
        if (m_entity_owner == null)
            return;

        // clear any existing instances first
        ClearInstances();

        List<Vector3> points = GetControlPointsLocal();
        if (points.Count < 2)
        {
            Log.LogWarning("need at least 2 control points to spawn instances");
            return;
        }

        float spline_length = GetLength();
        if (spline_length < m_instance_spacing)
        {
            Log.LogWarning("spline is shorter than instance spacing");
            return;
        }

        // walk along the spline at arc-length intervals and place instances
        uint instance_count = (uint)(spline_length / m_instance_spacing);
        uint total_samples = (uint)(points.Count) * m_resolution * 4; // dense sampling for arc-length
        float step = 1.0f / (float)(total_samples);

        float accumulated_distance = 0.0f;
        float next_spawn_distance = 0.0f;
        Vector3 prev_position = EvaluatePoint(points, 0.0f);
        uint spawned = 0;

        for (uint i = 0; i <= total_samples; i++)
        {
            float t = (float)(i) * step;
            Vector3 position = EvaluatePoint(points, t);

            if (i > 0)
                accumulated_distance += position.Distance(prev_position);
            prev_position = position;

            if (accumulated_distance >= next_spawn_distance)
            {
                Entity instance = World.CreateEntity();
                instance.SetObjectName(prefix_instance + spawned.ToString());
                instance.SetParent(m_entity_owner);

                // apply random lateral offset perpendicular to the spline
                Vector3 final_position = position;
                if (m_instance_random_offset > 0.0f)
                {
                    Vector3 tangent_dir = EvaluateTangent(points, t);
                    tangent_dir = Vector3.Normalize(tangent_dir);
                    Vector3 lateral = tangent_dir.Cross(Math.Vector3.Up);
                    lateral = Vector3.Normalize(lateral);
                    float offset = Math.Rand(-m_instance_random_offset, m_instance_random_offset);
                    final_position = final_position + lateral * offset;
                }
                instance.SetPositionLocal(final_position);

                // rotation: align to spline + optional random yaw
                Quaternion rotation = Quaternion.Identity;
                if (m_align_instances_to_spline)
                {
                    Vector3 tangent = EvaluateTangent(points, t);
                    tangent = Vector3.Normalize(tangent);
                    rotation = QuaternionExtensions.FromLookRotation(tangent, Math.Vector3.Up);
                }
                if (m_instance_random_yaw > 0.0f)
                {
                    float yaw = Math.Rand(-m_instance_random_yaw, m_instance_random_yaw);
                    rotation = rotation * QuaternionExtensions.FromAxisAngle(Math.Vector3.Up, yaw * Math.DegToRad);
                }
                instance.SetRotationLocal(rotation);

                // random scale
                if (m_instance_random_scale_min != 1.0f || m_instance_random_scale_max != 1.0f)
                {
                    float scale = Math.Rand(m_instance_random_scale_min, m_instance_random_scale_max);
                    instance.SetScaleLocal(new Vector3(scale, scale, scale));
                }

                Render renderable = instance.AddComponent<Render>();
                renderable->SetMesh(MeshType.Cylinder);
                renderable->SetDefaultMaterial();

                spawned++;
                next_spawn_distance += m_instance_spacing;
            }
        }

//        Log.LogInfo("spawned %u instances along spline (%.1f m, spacing %.1f m)", spawned, spline_length, m_instance_spacing);
    }
    public void ClearInstances()
    {
        if (m_entity_owner == null)
            return;

        // collect instance children (iterate in reverse to safely remove)
        List<Entity> instances_to_remove = new List<Entity>();
        int child_count = m_entity_owner.GetChildrenCount();
        for (int i = 0; i < child_count; i++)
        {
            if (m_entity_owner.GetChildByIndex(i) is Entity child)
            {
                if (child.GetObjectName().StartsWith(prefix_instance))
                    instances_to_remove.Add(child);
            }
        }

        for (int i = 0; i < instances_to_remove.Count; i++)
        {
            Entity instance = instances_to_remove[i];
            World.RemoveEntity(instance);
        }
    }

    // spline properties
    public bool GetClosedLoop() { return m_closed_loop; }
    public void SetClosedLoop(bool closed) { m_closed_loop = closed; }
    public uint GetResolution() { return m_resolution; }
    public void SetResolution(uint resolution) { m_resolution = resolution; }
    public float GetRoadWidth() { return m_road_width; }
    public void SetRoadWidth(float width) { m_road_width = width; }

    // profile properties
    public SplineProfile GetProfile() { return m_profile; }
    public void SetProfile(SplineProfile profile) { m_profile = profile; }
    public float GetHeight() { return m_height; }
    public void SetHeight(float height) { m_height = height; }
    public float GetThickness() { return m_thickness; }
    public void SetThickness(float thickness) { m_thickness = thickness; }
    public uint GetTubeSides() { return m_tube_sides; }
    public void SetTubeSides(uint sides) { m_tube_sides = sides; }

    // width variation (interpolated from start to end along the spline)
    public float GetRoadWidthEnd() { return m_road_width_end; }
    public void SetRoadWidthEnd(float width) { m_road_width_end = width; }

    // uv tiling
    public float GetUvTilingU() { return m_uv_tiling_u; }
    public void SetUvTilingU(float tiling) { m_uv_tiling_u = tiling; }
    public float GetUvTilingV() { return m_uv_tiling_v; }
    public void SetUvTilingV(float tiling) { m_uv_tiling_v = tiling; }

    // sidewalk/curb (road profile only)
    public bool GetSidewalkEnabled() { return m_sidewalk_enabled; }
    public void SetSidewalkEnabled(bool enabled) { m_sidewalk_enabled = enabled; }
    public float GetSidewalkWidth() { return m_sidewalk_width; }
    public void SetSidewalkWidth(float width) { m_sidewalk_width = width; }
    public float GetCurbHeight() { return m_curb_height; }
    public void SetCurbHeight(float height) { m_curb_height = height; }

    // terrain conforming
    public bool GetConformToTerrain() { return m_conform_to_terrain; }
    public void SetConformToTerrain(bool conform) { m_conform_to_terrain = conform; }
    public float GetTerrainOffset() { return m_terrain_offset; }
    public void SetTerrainOffset(float offset) { m_terrain_offset = offset; }

    // instancing properties
    public float GetInstanceSpacing() { return m_instance_spacing; }
    public void SetInstanceSpacing(float spacing) { m_instance_spacing = spacing; }
    public bool GetAlignInstancesToSpline() { return m_align_instances_to_spline; }
    public void SetAlignInstancesToSpline(bool align) { m_align_instances_to_spline = align; }
    public string GetInstanceMeshPath() { return m_instance_mesh_path; }
    public void SetInstanceMeshPath(string path) { m_instance_mesh_path = path; }

    // procedural placement randomization
    public float GetInstanceRandomOffset() { return m_instance_random_offset; }
    public void SetInstanceRandomOffset(float offset) { m_instance_random_offset = offset; }
    public float GetInstanceRandomScaleMin() { return m_instance_random_scale_min; }
    public void SetInstanceRandomScaleMin(float scale) { m_instance_random_scale_min = scale; }
    public float GetInstanceRandomScaleMax() { return m_instance_random_scale_max; }
    public void SetInstanceRandomScaleMax(float scale) { m_instance_random_scale_max = scale; }
    public float GetInstanceRandomYaw() { return m_instance_random_yaw; }
    public void SetInstanceRandomYaw(float degrees) { m_instance_random_yaw = degrees; }
}