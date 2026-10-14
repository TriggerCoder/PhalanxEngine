// GLSL port of the cpu-driven path of SpartanEngine data/shaders/g_buffer.hlsl @ 4c07baa (main_vs :382-401, with
// transform_to_world_space / transform_to_clip_space of common_vertex_processing.hlsl reduced to a rigid transform,
// the per-draw uv transform of :1006-1030).
// Deviations: also the depth prepass vertex shader (spartan: depth_prepass.hlsl), world space positions with view_projection (spartan: camera relative positions with the relative
// matrices), no wind, instancing, skinning or multiview.
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"
#include "common_material.glsl"

// Vertex_PosUvNorTan_Cpu: POSITION float3, TEXCOORD / NORMAL / TANGENT packed uints
layout(location = 0) in vec3 in_position;
layout(location = 1) in uint in_uv;
layout(location = 2) in uint in_normal;
layout(location = 3) in uint in_tangent;

layout(location = 0) out vec3 out_position_world;
layout(location = 1) out vec2 out_uv;
layout(location = 2) out vec3 out_normal;
layout(location = 3) out vec3 out_tangent;
layout(location = 4) out vec4 out_position_previous;
layout(location = 5) flat out uint out_material_index;
layout(location = 6) flat out vec4 out_uv_xform_ts; // tiling, offset
layout(location = 7) flat out vec4 out_uv_xform_ir; // invert, rotation, world space

// the depth prepass runs this same module, invariant keeps both passes' depth bit identical for the equal test
invariant gl_Position;

void main()
{
    DrawData _draw = draw_data[buffer_pass.draw_index];

    vec4 position_world          = _draw.transform * vec4(in_position, 1.0);
    vec4 position_world_previous = _draw.transform_previous * vec4(in_position, 1.0);
    out_position_world    = position_world.xyz;
    out_uv_xform_ts       = vec4(_draw.uv_tiling, _draw.uv_offset);
    out_uv_xform_ir       = vec4(_draw.uv_invert, _draw.uv_rotation, _draw.uv_world_space);
    out_uv                = apply_uv_transform(unpack_vertex_uv(in_uv), out_uv_xform_ts, out_uv_xform_ir);
    out_normal            = normalize(mat3(_draw.transform) * unpack_vertex_oct(in_normal));
    out_tangent           = normalize(mat3(_draw.transform) * unpack_vertex_oct(in_tangent));
    out_material_index    = _draw.material_index;
    out_position_previous = buffer_frame.view_projection_previous * position_world_previous;

    gl_Position   = buffer_frame.view_projection * position_world;
    gl_Position.y = -gl_Position.y; // spartan compiles with -fvk-invert-y
    out_position_previous.y = -out_position_previous.y;
}
