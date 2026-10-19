// GLSL port of the cpu-driven path of SpartanEngine data/shaders/depth_light.hlsl @ 4c07baa (main_vs :45-93): the
// shadow map vertex shader, world space into the light slice's view projection (transform[array_index]).
// pass_depth_light (shared_buffers.h:279-284): values slot 0 has_albedo, 1 light_index, 2 array_index (uint bits).
// Deviations: rigid transform only (no wind, skinning or terrain displacement), like g_buffer.vert; the instancing is
// Phalanx's (instancing.glsl).
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"
#include "common_material.glsl"
#include "instancing.glsl"

layout(location = 0) in vec3 in_position;
layout(location = 1) in uint in_uv;
layout(location = 2) in uint in_normal;
layout(location = 3) in uint in_tangent;

layout(location = 0) out vec3 out_position_world;
layout(location = 1) out vec2 out_uv;
layout(location = 2) out vec3 out_normal;
layout(location = 3) out vec3 out_tangent;
layout(location = 5) flat out uint out_material_index;
layout(location = 6) flat out vec4 out_uv_xform_ts;
layout(location = 7) flat out vec4 out_uv_xform_ir;

void main()
{
    mat4 transform, transform_previous;
    DrawData _draw = instance_draw_data(transform, transform_previous);

    uint index_light = floatBitsToUint(buffer_pass.values[0].y);
    uint index_array = floatBitsToUint(buffer_pass.values[0].z);

    // validate the light index to prevent out-of-bounds buffer access
    if (index_light >= uint(light_parameters.length()) || index_array >= 6u)
    {
        gl_Position = vec4(0.0);
        return;
    }

    vec4 position_world = transform * vec4(in_position, 1.0);
    out_position_world  = position_world.xyz;
    out_uv_xform_ts     = vec4(_draw.uv_tiling, _draw.uv_offset);
    out_uv_xform_ir     = vec4(_draw.uv_invert, _draw.uv_rotation, _draw.uv_world_space);
    out_uv              = apply_uv_transform(unpack_vertex_uv(in_uv), out_uv_xform_ts, out_uv_xform_ir);
    out_normal          = normalize(mat3(transform) * unpack_vertex_oct(in_normal));
    out_tangent         = normalize(mat3(transform) * unpack_vertex_oct(in_tangent));
    out_material_index  = _draw.material_index;

    gl_Position   = light_parameters[index_light].transform[index_array] * position_world;
    gl_Position.y = -gl_Position.y; // spartan compiles with -fvk-invert-y
}
