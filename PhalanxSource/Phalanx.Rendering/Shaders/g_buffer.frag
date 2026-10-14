// GLSL port of the cpu-driven path of SpartanEngine data/shaders/g_buffer.hlsl @ 4c07baa (main_ps :413-1013): the
// material's constant colour, roughness and metalness and the vertex normal go to the g-buffer.
// Deviations: no material textures, alpha testing, terrain, decals, emission, paint or specular aa yet; the
// previous depth in velocity.w is the raw previous ndc depth (spartan linearizes it).
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"

layout(location = 0) in vec3 in_position_world;
layout(location = 1) in vec2 in_uv;
layout(location = 2) in vec3 in_normal;
layout(location = 3) in vec3 in_tangent;
layout(location = 4) in vec4 in_position_previous;
layout(location = 5) flat in uint in_material_index;

layout(location = 0) out vec4 out_albedo;   // SV_Target0
layout(location = 1) out vec4 out_normal;   // SV_Target1
layout(location = 2) out vec4 out_material; // SV_Target2
layout(location = 3) out vec4 out_velocity; // SV_Target3
layout(location = 4) out vec3 out_emissive; // SV_Target4

void main()
{
    MaterialParameters material = material_parameters[in_material_index];

    vec4 albedo     = material.color;
    vec3 normal     = normalize(in_normal);
    float roughness = material.roughness;
    float metalness = material.metalness;
    float occlusion = 1.0;

    // velocity, both positions without jitter (uv_to_ndc of the pixel position, y flipped back to ndc up)
    vec2 position_uv           = gl_FragCoord.xy / buffer_frame.resolution_render;
    vec2 position_ndc          = vec2(position_uv.x * 2.0 - 1.0, 1.0 - position_uv.y * 2.0);
    vec2 position_ndc_previous = in_position_previous.xy / in_position_previous.w;
    position_ndc_previous.y    = -position_ndc_previous.y;
    position_ndc              -= buffer_frame.taa_jitter_current;
    position_ndc_previous     -= buffer_frame.taa_jitter_previous;

    out_albedo   = albedo;
    out_normal   = vec4(normal, pack_material_index(in_material_index));
    out_material = vec4(roughness, metalness, 0.0, occlusion);
    out_velocity = vec4(position_ndc - position_ndc_previous, 0.0, in_position_previous.z / max(in_position_previous.w, 1e-6));
    out_emissive = vec3(0.0);
}
