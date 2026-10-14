// GLSL port of SpartanEngine data/shaders/depth_prepass.hlsl @ 4c07baa, main_ps of the ALPHA_TEST_INDIRECT path
// (:59-90): non alpha tested materials early out, the rest discard where the albedo alpha is at or below
// get_alpha_threshold. Runs after g_buffer.vert in the prepass's alpha half.
// Deviations: the world position is the interpolated one (spartan reconstructs it from the depth); the uv honours
// world space uvs like the g-buffer does (spartan samples vertex.uv_misc.xy, the mesh uv, so a world space uv cutout
// is tested with different uvs than it is shaded with); no impostors.
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
    MaterialParameters material = material_parameters[in_material_index];
    if (!has_flag(material, 16u) || !has_flag(material, 2u))
        return;

    vec2 uv = resolve_material_uv(in_uv, in_position_world, in_normal, in_uv_xform_ts, in_uv_xform_ir);
    if (sample_material_texture(material, material_texture_index_albedo, uv).a <= get_alpha_threshold(in_position_world))
        discard;
}
