// SpartanEngine shared GPU structs, GLSL edition (Phalanx source of truth).
// Translated from SpartanEngine data/shaders/shared_buffers.h @ 4c07baa298ddbc4fecf8704986f85b60c67da4aa.
//
// Layout rules:
//  - each struct is tagged '// @layout std140' (uniform buffers) or '// @layout std430' (storage buffers,
//    push constants). All padding is explicit: the natural member order already satisfies the rule, so
//    the Phalanx.Generators source generator can verify there is no hidden padding (error PLX002/PLX003).
//  - vec3 has 16-byte alignment, vec2 8, double 8; struct size rounds up to its largest alignment
//    (std140: and to 16). Arrays: std430 stride = element size, std140 stride rounds up to 16.
//  - matrices are plain (column_major) mat4. The CPU uploads System.Numerics.Matrix4x4 bytes unchanged
//    (row-major, row-vector math v * M). Read as column-major, GLSL sees transpose(M), so GLSL code writes
//    the conventional 'M * v' and gets exactly the CPU's 'v * M'. Do not transpose on upload.
//  - '// @default(x)' carries the C++ default initializer to the generated C# struct.
//  - '// @hlsl-offset N' notes members whose offset differs from the HLSL/C++ packing at that commit.

#ifndef PHALANX_SHARED_BUFFERS_GLSL
#define PHALANX_SHARED_BUFFERS_GLSL

// @layout std140  (uniform buffer (set 0, binding 0))
struct FrameBufferData
{
    mat4   view;
    mat4   view_inverted;
    mat4   view_previous;
    mat4   projection;
    mat4   projection_inverted;
    mat4   projection_previous;
    mat4   view_projection;
    mat4   view_projection_inverted;
    mat4   view_projection_orthographic;
    mat4   view_projection_unjittered;
    mat4   view_projection_previous;
    mat4   view_projection_previous_unjittered;
    vec2   resolution_render;
    vec2   resolution_output;
    vec2   taa_jitter_current;
    vec2   taa_jitter_previous;
    float  camera_aperture;
    float  delta_time;
    uint   frame;
    uint   options;
    vec3   camera_position;
    float  camera_near;
    vec3   camera_forward;
    float  camera_far;
    float  camera_last_movement_time;
    float  hdr_enabled;
    float  hdr_max_nits;
    float  padding_restir;
    vec3   camera_position_previous;
    float  resolution_scale;
    double time;
    float  camera_fov;
    float  restir_pt_scale;
    vec3   wind;
    float  gamma;
    vec3   camera_right;
    float  camera_exposure;
    float  restir_pt_light_count;
    float  restir_pt_emissive_tri_count;
    float  cloud_coverage;
    float  hdr_sdr_white_nits;
    mat4   view_right;
    mat4   view_inverted_right;
    mat4   projection_right;
    mat4   projection_inverted_right;
    mat4   view_projection_right;
    mat4   view_projection_inverted_right;
    mat4   view_projection_previous_right;
    mat4   view_projection_unjittered_right;
    mat4   view_projection_previous_unjittered_right;
    vec3   camera_position_right;
    float  camera_exposure_mode;
    uint   is_multiview;
    uint  _pad0;
    vec2   cloud_seed_offset;  // @hlsl-offset 1540
    uint   padding_mv2;  // @hlsl-offset 1548
    uint   cluster_count_x;  // @hlsl-offset 1552
    uint   cluster_count_y;  // @hlsl-offset 1556
    uint   cluster_count_z;  // @hlsl-offset 1560
    uint   cluster_light_count;  // @hlsl-offset 1564
    float  cluster_z_scale;  // @hlsl-offset 1568
    float  cluster_z_bias;  // @hlsl-offset 1572
    uint   volumetric_light_count;  // @hlsl-offset 1576
    float  cluster_padding1;  // @hlsl-offset 1580
    uint  _pad1;
    uint  _pad2;
    uint  _pad3;
    vec4   ocean_cascade_length;  // @hlsl-offset 1584 patch length (meters) per cascade
    float  ocean_sea_level;  // @hlsl-offset 1600
    float  ocean_choppiness;  // @hlsl-offset 1604
    float  ocean_displacement_scale;  // @hlsl-offset 1608
    float  ocean_normal_strength;  // @hlsl-offset 1612
    uint   ocean_cascade_count;  // @hlsl-offset 1616
    float  ocean_enabled;  // @hlsl-offset 1620
    float  ocean_turbidity;  // @hlsl-offset 1624
    float  ocean_caustics_intensity;  // @hlsl-offset 1628
    vec4   terrain_height_mapping;  // @hlsl-offset 1632 xy = world min xz, zw = 1 / world size
    float  terrain_height_y;  // @hlsl-offset 1648
    float  terrain_height_enabled;  // @hlsl-offset 1652
    float  terrain_maps_enabled;  // @hlsl-offset 1656
    float  terrain_blend_height;  // @hlsl-offset 1660
    uint   terrain_blend_material;  // @hlsl-offset 1664 bindless index of the terrain surface material
    float  terrain_blend_tiling;  // @hlsl-offset 1668 terrain uv repeats per metre, matches the surface material
    float  puddliness;  // @hlsl-offset 1672 0 dry, 1 standing water in every low spot of terrain and roads
    float  padding_terrain_blend_1;  // @hlsl-offset 1676
    vec4   ocean_shore_mapping;  // @hlsl-offset 1680 xy = world xz of the field origin, zw = 1 / world size
    vec4   ocean_shore_wave;  // @hlsl-offset 1696 x = offshore swell height (m), y = period (s), z = wrapped time (s), w = enabled
    vec4   ocean_shore_swell;  // @hlsl-offset 1712 xy = direction the swell travels, z = reach offshore (m), w = unused
    vec4   radial_blur_hubs[64];  // @hlsl-offset 1728 xyz = current pivot, w = unwrapped radians/frame
    vec4   radial_blur_axes[64];  // @hlsl-offset 2752 xyz = world rotation axis, w = object mask ID
    vec4   radial_blur_previous_hubs[64];  // @hlsl-offset 3776 xyz = previous pivot
    float  radial_blur_hub_count;  // @hlsl-offset 4800
    float  time_of_day;  // @hlsl-offset 4804
    float  padding_radial_1;  // @hlsl-offset 4808
    float  padding_radial_2;  // @hlsl-offset 4812
    vec4   celestial_moon;  // @hlsl-offset 4816 xyz observer direction, w illuminated fraction
    vec4   celestial_sun;  // @hlsl-offset 4832 xyz observer direction, w lunar angular radius
    vec4   equatorial_x;  // @hlsl-offset 4848
    vec4   equatorial_y;  // @hlsl-offset 4864
    vec4   equatorial_z;  // @hlsl-offset 4880
    vec4   weather;  // @hlsl-offset 4896
    vec4   rain_occlusion;  // @hlsl-offset 4912
    vec4   rain_vehicle;  // @hlsl-offset 4928
    vec4   rain_vehicle_axis[3];  // @hlsl-offset 4944
    vec4   rain_car_origin;  // @hlsl-offset 4992
    vec4   rain_car_box;  // @hlsl-offset 5008
    vec4   rain_car_atlas;  // @hlsl-offset 5024
    vec4   rain_car_micro;  // @hlsl-offset 5040
    vec4   rain_car_faces[6];  // @hlsl-offset 5056
    mat4   view_projection_relative;  // @hlsl-offset 5152
    mat4   view_projection_previous_relative;  // @hlsl-offset 5216
    mat4   view_projection_relative_right;  // @hlsl-offset 5280
    mat4   view_projection_previous_relative_right;  // @hlsl-offset 5344
    vec4   grass_far_tint;  // @hlsl-offset 5408 xyz = grass material colour, w = 1 when active
    vec4   grass_far_fade;  // @hlsl-offset 5424 x = undercoat start, y = last ring fade start, z = last ring end, w = signed patch size
    vec4   grass_far_gate;  // @hlsl-offset 5440 x = biome min (negative ignores), y = max slope cos, z = ground mask bits, w = mask channel
    vec4   grass_far_patch;  // @hlsl-offset 5456 x = patch coverage, y = patch edge, zw = unused
};  // 5488 bytes (hlsl/c++: 5472)

// @layout std430  (push constant block (Vulkan push constants use std430 rules))
struct PassBufferData
{
    uint   draw_index;  // @default(0)
    uint   material_index;  // @default(0)
    uint   is_transparent;  // @default(0)
    uint   eye_index;  // @default(0) 0 = left / monoscopic, 1 = right
    vec4   values[5];
};  // 96 bytes (hlsl/c++: 96)

// @layout std430  (storage buffer element)
struct MaterialParameters
{
    uint   texture_indices[16];
    vec4   color;  // @default(vec4(0.0))
    float  roughness;  // @default(0.0)
    float  metalness;  // @default(0.0)
    float  normal;  // @default(0.0)
    float  height;  // @default(0.0)
    uint   flags;  // @default(0)
    float  local_width;  // @default(0.0)
    float  local_height;  // @default(0.0)
    float  emissive_strength;  // @default(0.0) 0-1 scale for emissive_from_albedo, 1 maps to the composition nits
    float  subsurface_scattering;
    float  sheen;
    float  anisotropic;
    float  anisotropic_rotation;
    float  clearcoat;
    float  clearcoat_roughness;
    float  flake_strength;
    float  flake_scale;
    vec4   pearl_color;  // @default(vec4(1.0))
    vec4   coat_tint;  // @default(vec4(1.0))
    float  pearl_strength;
    float  ior;
    float  absorption;  // @default(0.0) beer lambert dye density for glass, independent of alpha
    float  thickness;  // @default(0.0) shell thickness in meters for glass parallax and optical path
    vec2   terrain_slope_range;  // @default(vec2(0.0)) radians, weight ramps in across the range
    vec2   terrain_height_range;  // @default(vec2(0.0)) meters
    float  terrain_curvature_influence;  // @default(0.0) positive favours concave, negative favours convex
    float  terrain_flow_influence;  // @default(0.0) positive favours water channels
    float  terrain_occlusion_influence;  // @default(0.0) positive favours crevices and valley floors
    float  terrain_insolation_influence;  // @default(0.0) positive favours sun facing slopes
    float  terrain_wear_influence;  // @default(0.0) positive favours eroded bedrock
    float  terrain_deposition_influence;  // @default(0.0) positive favours accumulated sediment
    float  terrain_talus_influence;  // @default(0.0) positive favours scree below cliffs
    float  terrain_weight_bias;  // @default(1.0) overall priority against the other layers
    float  terrain_tiling_scale;  // @default(1.0) multiplies the terrain uv, per layer texel density
    float  terrain_blend_contrast;  // @default(0.2) height blend band width, smaller is sharper
    float  terrain_porosity;  // @default(0.5) how much the layer darkens when wet
    float  terrain_macro_strength;  // @default(1.0) large scale colour breakup amount
    vec4   terrain_world_mapping;  // @default(vec4(0.0)) xy = world min xz, zw = 1 / world size xz
    float  terrain_sea_level;  // @default(0.0)
    float  terrain_snow_level;  // @default(0.0)
    uint   terrain_layer_base;  // @default(0) bindless material index of layer 0
    uint   terrain_layer_count;  // @default(0)
    uint   terrain_layer_stride;  // @default(0) bindless indices between consecutive layers
    uint   terrain_flags;  // @default(0)
    float  terrain_snow_amount;  // @default(1.0) global snow multiplier, 0 disables the snow layer
    float  terrain_wetness;  // @default(0.0) global wetness added on top of the flow driven amount
    float  terrain_blend;  // @default(1.0)
    float  terrain_blend_sharpness;  // @default(0.5)
    float  terrain_coating;  // @default(0.0)
    float  terrain_coating_scale;  // @default(3.0)
};  // 320 bytes (hlsl/c++: 320)

// @layout std430  (storage buffer element)
struct LightParameters
{
    vec4   color;
    vec3   position;
    float  intensity;
    vec3   direction;
    float  range;
    vec3   direction_right;
    float  angle;
    uint   flags;
    uint   screen_space_shadow_slice_index;
    float  area_width;
    float  area_height;
    mat4   transform[6];
    vec2   atlas_offsets[6];
    vec2   atlas_scales[6];
    vec2   atlas_texel_sizes[6];
};  // 608 bytes (hlsl/c++: 608)

// @layout std430  (storage buffer element)
struct Aabb
{
    vec3   min;
    float  padding1;
    vec3   max;
    float  padding2;
};  // 32 bytes (hlsl/c++: 32)

// @layout std430  (storage buffer element)
struct GeometryInfo
{
    uint   vertex_offset;
    uint   index_offset;
    vec2   uv_tiling;  // @default(vec2(1.0, 1.0))
    vec2   uv_offset;  // @default(vec2(0.0))
    vec2   uv_invert;  // @default(vec2(0.0))
    float  uv_rotation;  // @default(0.0)
    float  uv_world_space;  // @default(0.0)
    uint   material_index;
    uint   padding_rt;
    uint   decal_offset;  // @default(0)
    uint   decal_count;  // @default(0)
    uint  _pad0;
    uint  _pad1;
    vec4   object_to_world_0;  // @hlsl-offset 56
    vec4   object_to_world_1;  // @hlsl-offset 72
    vec4   object_to_world_2;  // @hlsl-offset 88
    vec4   world_to_object_0;  // @hlsl-offset 104
    vec4   world_to_object_1;  // @hlsl-offset 120
    vec4   world_to_object_2;  // @hlsl-offset 136
};  // 160 bytes (hlsl/c++: 152)

// @layout std430  (storage buffer element)
struct EmissiveTriangle
{
    vec3   v0;  // world space vertex 0
    float  weight;  // per triangle picking weight (area * lum(emission))
    vec3   v1;  // world space vertex 1
    float  cdf;  // prefix sum of weight up to and including this triangle
    vec3   v2;  // world space vertex 2
    float  area;  // world space triangle area
    vec3   normal;  // world space triangle normal (front side)
    float  pad0;  // @default(0.0)
    vec3   emission;  // emitted radiance per unit projected area (world units)
    float  pad1;  // @default(0.0)
};  // 80 bytes (hlsl/c++: 80)

// @layout std430  (storage buffer element)
struct IndirectDrawArgs
{
    uint   index_count;  // @default(0)
    uint   instance_count;  // @default(0)
    uint   first_index;  // @default(0)
    int    vertex_offset;  // @default(0)
    uint   first_instance;  // @default(0)
};  // 20 bytes (hlsl/c++: 20)

// @layout std430  (storage buffer element)
struct IndirectDispatchArgs
{
    uint   group_count_x;  // @default(0)
    uint   group_count_y;  // @default(1)
    uint   group_count_z;  // @default(1)
};  // 12 bytes (hlsl/c++: 12)

// @layout std430  (storage buffer element)
struct DecalParameters
{
    mat4   world_to_decal;
    vec4   color;  // linear albedo and opacity
    vec4   surface;  // roughness, relief in metres, seed, grass fraction
    uint   source_material;  // @default(0xffffffffu)
    uint   kind;  // @default(0) 0: deposit, 1: paint scratch (same buffer layout)
    uint   padding1;  // @default(0)
    uint   padding2;  // @default(0)
};  // 112 bytes (hlsl/c++: 112)

// @layout std430  (storage buffer element)
struct DrawData
{
    mat4   transform;
    mat4   transform_previous;
    uint   decal_offset;  // @default(0)
    uint   decal_count;  // @default(0)
    uint   motion_blur_id;  // @default(0)
    uint   material_index;  // @default(0)
    uint   is_transparent;  // @default(0)
    uint   aabb_index;  // @default(0)
    uint   lod_first_index;  // @default(0) global first index into the geometry index buffer for this lod
    uint   flags;  // @default(0)
    uint   instance_offset;  // @default(0) offset into the global instance buffer
    uint   instance_index;  // @default(0) per-draw instance to fetch, vs uses this in place of sv_instanceid
    uint   lod_vertex_offset;  // @default(0) global vertex offset for this lod (added to lod-local indices)
    uint   lod_meshlet_offset;  // @default(0) global first meshlet index for this lod, phase b expands meshlets from here
    uint   lod_meshlet_count;  // @default(0) meshlet count for this lod, phase b loops over this many meshlets per surviving instance
    uint   meshlet_vertex_base;  // @default(0) 32-bit global base; MeshletBounds offsets remain mesh-local
    uint   meshlet_micro_base;  // @default(0)
    uint  _pad0;
    vec2   uv_tiling;  // @default(vec2(1.0, 1.0)) @hlsl-offset 188
    vec2   uv_offset;  // @default(vec2(0.0)) @hlsl-offset 196
    vec2   uv_invert;  // @default(vec2(0.0)) @hlsl-offset 204
    float  uv_rotation;  // @default(0.0) @hlsl-offset 212
    float  uv_world_space;  // @default(0.0) @hlsl-offset 216
    vec3   lod_aabb_min;  // @default(vec3(0.0)) @hlsl-offset 220
    float  lod_aabb_diag;  // @default(0.0) @hlsl-offset 232
    vec3   lod_aabb_extent;  // @default(vec3(0.0)) @hlsl-offset 236
    float  max_render_distance_squared;  // @default(0.0) @hlsl-offset 248
    uint   previous_vertex_offset;  // @default(0) @hlsl-offset 252 relative arena offset, zero means unchanged pose
    uint   impostor_texel_offset;  // @default(0) @hlsl-offset 256
    uint   impostor_layout;  // @default(0) @hlsl-offset 260 frames | resolution << 8 | layers << 24
    uint  _pad1;
    vec3   impostor_center;  // @default(vec3(0.0)) @hlsl-offset 264
    float  impostor_radius;  // @default(0.0) @hlsl-offset 276
    vec2   impostor_uv_min;  // @default(vec2(0.0)) @hlsl-offset 280
    vec2   impostor_uv_scale;  // @default(vec2(1.0)) @hlsl-offset 288
};  // 304 bytes (hlsl/c++: 296)

// @layout std430  (storage buffer element)
struct CullTask
{
    uint   draw_index;  // @default(0)
    uint   lod_reference_draw_index;  // @default(0)
    uint   instance_index;  // @default(0)
    uint   instance_count;  // @default(1)
};  // 16 bytes (hlsl/c++: 16)

// @layout std430  (storage buffer element)
struct SurvivingInstance
{
    uint   draw_index;  // @default(0)
    uint   instance_index;  // @default(0)
};  // 8 bytes (hlsl/c++: 8)

// @layout std430  (storage buffer element)
struct ShadowCaster
{
    uint   draw_data_index;  // @default(0)
    uint   instance_offset;  // @default(0) global instance pool offset, 0 when not instanced
    uint   instance_count;  // @default(1)
    uint   flags;  // @default(0)
    uint   index_count_directional;  // @default(0)
    uint   first_index_directional;  // @default(0)
    int    vertex_offset_directional;  // @default(0)
    uint   index_count_local;  // @default(0)
    uint   first_index_local;  // @default(0)
    int    vertex_offset_local;  // @default(0)
    uint   padding0;  // @default(0)
    uint   padding1;  // @default(0)
    vec3   bounds_min;  // @default(vec3(0.0))
    float  max_shadow_distance;  // @default(0.0) 0 disables the per instance distance test
    vec3   bounds_max;  // @default(vec3(0.0))
    float  padding2;  // @default(0.0)
};  // 80 bytes (hlsl/c++: 80)

// @layout std430  (storage buffer element)
struct DrawLod
{
    uint   first_index;  // @default(0)
    uint   vertex_offset;  // @default(0)
    uint   meshlet_offset;  // @default(0)
    uint   meshlet_count;  // @default(0)
    vec3   aabb_min;  // @default(vec3(0.0))
    float  aabb_diag;  // @default(0.0)
    vec3   aabb_extent;  // @default(vec3(0.0))
    uint   previous_vertex_offset;  // @default(0)
};  // 48 bytes (hlsl/c++: 48)

// @layout std430  (storage buffer element)
struct TessellatedDraw
{
    vec3   bounds_min;  // @default(vec3(0.0))
    float  max_distance_squared;  // @default(0.0) 0 disables the distance test
    vec3   bounds_max;  // @default(vec3(0.0))
    uint   flags;  // @default(0)
    uint   lod_offset;  // @default(0)
    uint   lod_count;  // @default(1)
    uint   first_instance;  // @default(0)
    uint   instance_count;  // @default(1)
    vec3   lod_extents;  // @default(vec3(0.0))
    float  padding0;  // @default(0.0)
};  // 64 bytes (hlsl/c++: 64)

// @layout std430  (storage buffer element)
struct CachedTreeWind
{
    vec4   current_axis_drive;
    vec4   current_phase_gust_micro;
    vec4   previous_axis_drive;
    vec4   previous_phase_gust_micro;
};  // 64 bytes (hlsl/c++: 64)

// @layout std430  (storage buffer element)
struct MeshletInstance
{
    uint   draw_index;  // @default(0)
    uint   meshlet_index;  // @default(0)
    uint   instance_index;  // @default(0)
    uint   padding0;  // @default(0) root-wind cache slot + 1, zero uses the local evaluator
};  // 16 bytes (hlsl/c++: 16)

// @layout std430  (storage buffer element)
struct MeshletBounds
{
    uint   center_xy;  // @default(0)
    uint   center_z_radius;  // @default(0)
    uint   cone_axis_cutoff;  // @default(0)
    uint   first_index_tri_count;  // @default(0)
    uint   first_vertex_vert_count;  // @default(0)
    uint   first_micro;  // @default(0)
};  // 24 bytes (hlsl/c++: 24)

// @layout std430  (storage buffer element)
struct PulledVertex
{
    vec3   position;
    uint   uv;  // f16 x | f16 y
    uint   normal;  // s16 oct.x | s16 oct.y
    uint   tangent;  // s16 oct.x | s16 oct.y
    uint  _pad0;
    uint  _pad1;
};  // 32 bytes (hlsl/c++: 24)

// @layout std430  (storage buffer element)
struct PackedInstance
{
    float  pos_x;
    float  pos_y;
    float  pos_z;
    uint   rotation_xy;  // signed normalized 16-bit quaternion components
    uint   rotation_zw;
    float  scale_x;
    float  scale_y;
    float  scale_z;
};  // 32 bytes (hlsl/c++: 32)

// @layout std430  (storage buffer element)
struct GrassInstance
{
    float  pos_x;
    float  pos_y;
    float  pos_z;
    uint   normal_yaw_scale;  // normal_oct (high 16) | yaw_packed (8) | scale_packed (8)
};  // 16 bytes (hlsl/c++: 16)

// @layout std430  (storage buffer element)
struct Particle
{
    vec3   position;
    float  lifetime;  // @default(0.0) remaining
    vec3   previous_position;
    float  previous_size;  // @default(0.0)
    vec4   ground_plane;  // captured at birth; zero normal disables ballistic ground collision
    vec3   velocity;
    float  max_lifetime;  // @default(0.0) initial
    vec4   color;  // current rgba
    float  size;  // @default(0.0) current
    uint   emitter_index;  // @default(0) emitter that spawned this particle
    float  start_size;  // @default(0.0) birth size, captured so later emitter changes do not resize live particles
    float  end_size;  // @default(0.0) death size, captured for the same reason
    vec4   birth_color;
    vec4   wake_origin;  // xyz = shed contact point, w = circulation strength
    vec4   wake_axis;  // xyz = travel direction at birth, w = tire radius
    vec4   birth_effect;  // buoyancy, cooling rate, rollup, emissive strength
};  // 160 bytes (hlsl/c++: 160)

// @layout std430  (storage buffer element)
struct EmitterParams
{
    vec4   ground_plane;
    vec3   position;
    float  emission_rate;  // @default(0.0)
    float  lifetime;  // @default(0.0)
    float  start_speed;  // @default(0.0)
    float  start_size;  // @default(0.0)
    float  end_size;  // @default(0.0)
    vec4   start_color;
    vec4   end_color;
    float  gravity_modifier;  // @default(0.0)
    float  radius;  // @default(0.0)
    float  delta_time;  // @default(0.0)
    uint   max_particles;  // @default(0) total active particle buffer span
    uint   range_start;  // @default(0)
    uint   range_count;  // @default(0)
    uint   emit_count;  // @default(0)
    uint   frame;  // @default(0)
    uint   emitter_count;  // @default(0)
    uint   blend_mode;  // @default(0)
    uint   lighting_mode;  // @default(0)
    uint   render_mode;  // @default(0)
    float  volume_density;  // @default(1.0)
    float  volume_anisotropy;  // @default(0.35)
    float  volume_shadowing;  // @default(0.5)
    uint  _pad0;
    vec3   emission_direction;  // @hlsl-offset 140
    float  emission_cone_angle;  // @default(0.0) @hlsl-offset 152
    float  directional_blend;  // @default(0.0) @hlsl-offset 156
    float  soft_depth_scale;  // @default(20.0) @hlsl-offset 160
    float  drag;  // @default(1.2) @hlsl-offset 164
    float  turbulence_strength;  // @default(0.3) @hlsl-offset 168
    float  wind_influence;  // @default(0.0) @hlsl-offset 172
    float  velocity_inheritance;  // @default(0.0) @hlsl-offset 176
    float  velocity_stretch;  // @default(0.0) @hlsl-offset 180
    float  emissive_strength;  // @default(0.0) @hlsl-offset 184
    uint   flipbook_rows;  // @default(1) @hlsl-offset 188
    uint   flipbook_columns;  // @default(1) @hlsl-offset 192
    float  flipbook_fps;  // @default(0.0) @hlsl-offset 196
    uint  _pad1;
    vec3   emitter_velocity;  // @hlsl-offset 200
    uint   volume_splat_stride;  // @default(1) @hlsl-offset 212
    vec3   vortex_center;  // @hlsl-offset 216
    float  vortex_strength;  // @default(0.0) @hlsl-offset 228
    vec3   vortex_axis;  // @hlsl-offset 232
    float  vortex_radius;  // @default(0.0) @hlsl-offset 244
    float  thermal_strength;  // @default(0.0) @hlsl-offset 248
    float  thermal_decay;  // @default(2.5) @hlsl-offset 252
    float  rollup_strength;  // @default(0.0) @hlsl-offset 256
    float  wake_strength;  // @default(0.0) @hlsl-offset 260
    float  churn_strength;  // @default(0.0) @hlsl-offset 264
    uint   collision_traced;  // @default(0) @hlsl-offset 268
    float  collision_clearance;  // @default(0.0) @hlsl-offset 272
    uint   rain_occluded;  // @default(0) @hlsl-offset 276
};  // 288 bytes (hlsl/c++: 280)

#endif // PHALANX_SHARED_BUFFERS_GLSL
