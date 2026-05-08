using System.Numerics;
using System.Runtime.InteropServices;

namespace Phalanx;

// constant buffer - updates once per frame
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FrameBufferData
{
    public Matrix4x4 view;
    public Matrix4x4 view_inverted;
    public Matrix4x4 view_previous;
    public Matrix4x4 projection;
    public Matrix4x4 projection_inverted;
    public Matrix4x4 projection_previous;
    public Matrix4x4 view_projection;
    public Matrix4x4 view_projection_inverted;
    public Matrix4x4 view_projection_orthographic;
    public Matrix4x4 view_projection_unjittered;
    public Matrix4x4 view_projection_previous;
    public Matrix4x4 view_projection_previous_unjittered;

    public Vector2 resolution_render;
    public Vector2 resolution_output;

    public Vector2 taa_jitter_current;
    public Vector2 taa_jitter_previous;

    public float camera_aperture;
    public float delta_time;
    public uint frame;
    public uint options;

    public Vector3 camera_position;
    public float camera_near;

    public Vector3 camera_forward;
    public float camera_far;

    public float camera_last_movement_time;
    public float hdr_enabled;
    public float hdr_max_nits;
    public float padding;

    public Vector3 camera_position_previous;
    public float resolution_scale;

    public double time;
    public float camera_fov;
    public float padding2;

    public Vector3 wind;
    public float gamma;

    public Vector3 camera_right;
    public float camera_exposure;

    // clouds
    public float cloud_coverage;
    public float cloud_shadows;
    public float padding3;
    public float padding4;

    // vr stereo - right eye matrices (left eye uses the primary matrices above)
    public Matrix4x4 view_right;
    public Matrix4x4 projection_right;
    public Matrix4x4 view_projection_right;
    public Matrix4x4 view_projection_inverted_right;
    public Matrix4x4 view_projection_previous_right;
    public uint is_multiview;
    public uint padding_mv0;
    public uint padding_mv1;
    public uint padding_mv2;
    public void SetBit(bool set, uint bit) { options = set ? (options | bit) : (options & ~bit); }
}

// push constant buffer - carries per-draw and per-pass data
// draw_index indexes into the bindless draw data buffer for transforms and material info
// material_index and is_transparent are pass-level state for compute shaders
// values[] carries generic per-pass parameters (3 x float4)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PassBufferData
{
    public uint draw_index;
    public uint material_index;
    public uint is_transparent;
    public uint padding;

    public Vector4 values0;
    public Vector4 values1;
    public Vector4 values2;
    public PassBufferData()
    {
        draw_index = 0;
        material_index = 0;
        is_transparent = 0;
        padding = 0;
    }
    public void SetF3Value(Vector3 value) { SetF3Value(value.X, value.Y, value.Z); }
    public void SetF3Value(float x, float y = 0f, float z = 0f)
    {
        values0.X = x;
        values0.Y = y;
        values0.Z = z;
    }

    public void SetF3Value2(Vector3 value) { SetF3Value2(value.X, value.Y, value.Z); }
    public void SetF3Value2(float x, float y, float z)
    {
        values1.X = x;
        values1.Y = y;
        values1.Z = z;
    }

    public void SetF4Value(Color value) { values2 = value; }

    public void SetF4Value(float x, float y, float z, float w)
    {
        values2.X = x;
        values2.Y = y;
        values2.Z = z;
        values2.W = w;
    }
    public void SetF2Value(float x, float y)
    {
        values0.W = x;
        values1.W = y;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MaterialParameters
{
    public Color color;

    public Vector2 tiling;
    public Vector2 offset;
    public Vector2 invert_uv;

    public float roughness;
    public float metallness;
    public float normal;
    public float height;

    public uint flags;
    public float local_width;
    public float padding;
    public float subsurface_scattering;

    public float sheen;
    public float local_height;
    public float world_space_uv;
    public float padding2;

    public float anisotropic;
    public float anisotropic_rotation;
    public float clearcoat;
    public float clearcoat_roughness;

    public MaterialParameters()
    {
        color = Vector4.Zero;
        tiling = Vector2.Zero;
        offset = Vector2.Zero;
        invert_uv = Vector2.Zero;

        roughness = 0.0f;
        metallness = 0.0f;
        normal = 0.0f;
        height = 0.0f;

        flags = 0;
        local_width = 0.0f;
        local_height = 0.0f;
        world_space_uv = 0.0f;
    }
    public bool HasTextureAlbedo() { return (flags & (1u << 2)) != 0; }
    public bool HasTextureNormal() { return (flags & (1u << 1)) != 0; }
    public bool HasTextureOcclusion() { return (flags & (1u << 7)) != 0; }
    public bool HasTextureRoughness() { return (flags & (1u << 3)) != 0; }
    public bool HasTextureMetalness() { return (flags & (1u << 4)) != 0; }
    public bool HasTextureEmissive() { return (flags & (1u << 6)) != 0; }
    public bool EmissiveFromAlbedo() { return (flags & (1u << 15)) != 0; }
    public void SetFlag(uint bit, bool value) { flags = value ? (flags | bit) : (flags & ~bit); }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LightParameters
{
    public Color color;
    public Vector3 position;
    public float intensity;
    public Vector3 direction;
    public float range;
    public float angle;
    public uint flags;
    public uint screen_space_shadow_slice_index;
    public float area_width;
    public float area_height;

    public Matrix4x4 transform0;
    public Matrix4x4 transform1;
    public Matrix4x4 transform2;
    public Matrix4x4 transform3;
    public Matrix4x4 transform4;
    public Matrix4x4 transform5;

    public Vector2 atlas_offsets0;
    public Vector2 atlas_offsets1;
    public Vector2 atlas_offsets2;
    public Vector2 atlas_offsets3;
    public Vector2 atlas_offsets4;
    public Vector2 atlas_offsets5;

    public Vector2 atlas_scales0;
    public Vector2 atlas_scales1;
    public Vector2 atlas_scales2;
    public Vector2 atlas_scales3;
    public Vector2 atlas_scales4;
    public Vector2 atlas_scales5;

    public Vector2 atlas_texel_sizes0;
    public Vector2 atlas_texel_sizes1;
    public Vector2 atlas_texel_sizes2;
    public Vector2 atlas_texel_sizes3;
    public Vector2 atlas_texel_sizes4;
    public Vector2 atlas_texel_sizes5;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Aabb
{
    public Vector3 min;
    public float is_occluder;
    public Vector3 max;
    public float padding2;
}

// per-blas-instance offsets into the global geometry buffer (indexed by InstanceIndex() in rt shaders)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GeometryInfo
{
    public uint vertex_offset;
    public uint index_offset;
}

// gpu-driven indirect draw arguments (matches VkDrawIndexedIndirectCommand layout)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct IndirectDrawArgs
{
    public uint index_count;
    public uint instance_count;
    public uint first_index;
    public int vertex_offset;
    public uint first_instance;
    public IndirectDrawArgs()
    {
        index_count = 0;
        instance_count = 0;
        first_index = 0;
        vertex_offset = 0;
        first_instance = 0;
    }
}

// per-draw data for gpu-driven rendering (indexed by draw_id in shaders)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DrawData
{
    public Matrix4x4 transform;
    public Matrix4x4 transform_previous;
    public uint material_index;
    public uint is_transparent;
    public uint aabb_index;
    public uint padding;
    public DrawData()
    {
        material_index = 0;
        is_transparent = 0;
        aabb_index = 0;
        padding = 0;
    }
}

// vertex pulling - global geometry buffer exposed as a structured buffer
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PulledVertex
{
    public Vector3 position;
    public Vector2 uv;
    public Vector3 normal;
    public Vector3 tangent;
}

// vertex pulling - instance buffer exposed as packed uint data (10 bytes per instance)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PackedInstance
{
    public uint pos_xy;       // position_x (half16) | position_y (half16)
    public uint pos_z_norm;   // position_z (half16) | normal_oct (uint16)
    public uint yaw_scale;    // yaw_packed (uint8) | scale_packed (uint8) | padding (uint16)
}

// gpu particle (64 bytes)
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Particle
{
    public Vector3 position;
    public float lifetime;          // remaining lifetime
    public Vector3 velocity;
    public float max_lifetime;      // initial lifetime
    public Color color;             // current RGBA
    public float size;              // current size
    public Vector3 padding;         // explicit padding to keep 64-byte alignment
    public Particle()
    {
        lifetime = 0.0f;
        max_lifetime = 0.0f;
        size = 0.0f;
    }
}

// gpu emitter parameters
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EmitterParams
{
    public Vector3 position;
    public float emission_rate;
    public float lifetime;
    public float start_speed;
    public float start_size;
    public float end_size;
    public Color start_color;
    public Color end_color;
    public float gravity_modifier;
    public float radius;
    public float delta_time;
    public uint max_particles;
    public uint frame;
    public uint emitter_count;
    public float padding1;
    public float padding2;
    public EmitterParams()
    {
        emission_rate = 0.0f;
        lifetime = 0.0f;
        start_speed = 0.0f;
        start_size = 0.0f;
        end_size = 0.0f;
        gravity_modifier = 0.0f;
        radius = 0.0f;
        delta_time = 0.0f;
        max_particles = 0;
        frame = 0;
        emitter_count = 0;
        padding1 = 0.0f;
        padding2 = 0.0f;
    }
}