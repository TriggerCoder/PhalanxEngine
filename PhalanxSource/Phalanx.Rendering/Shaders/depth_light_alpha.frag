// GLSL port of SpartanEngine data/shaders/depth_light.hlsl @ 4c07baa, main_ps (:95-111): the alpha tested half of
// the shadow maps, discarding where the albedo alpha is at or below get_alpha_threshold of the world position.
// has_albedo is pass_depth_light slot 0 (values[0].x, uint bits).
// Deviations: the uv honours world space uvs like the g-buffer does (as depth_prepass_alpha.frag).
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"
#include "common_material.glsl"

layout(location = 0) in vec3 in_position_world;
layout(location = 1) in vec2 in_uv;
layout(location = 2) in vec3 in_normal;
layout(location = 5) flat in uint in_material_index;
layout(location = 6) flat in vec4 in_uv_xform_ts;
layout(location = 7) flat in vec4 in_uv_xform_ir;

void main()
{
    bool has_albedo = floatBitsToUint(buffer_pass.values[0].x) != 0u;
    if (!has_albedo)
        return;

    MaterialParameters material = material_parameters[in_material_index];
    vec2 uv = resolve_material_uv(in_uv, in_position_world, in_normal, in_uv_xform_ts, in_uv_xform_ir);
    if (sample_material_texture(material, material_texture_index_albedo, uv).a <= get_alpha_threshold(in_position_world))
        discard;
}
