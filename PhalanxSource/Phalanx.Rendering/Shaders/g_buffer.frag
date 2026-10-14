// GLSL port of the cpu-driven path of SpartanEngine data/shaders/g_buffer.hlsl @ 4c07baa (main_ps :413-1013): the
// material's colour, roughness and metalness scaled by its bindless textures (albedo :634, emission :695, normal map
// :708, packed occlusion/roughness/metalness :742, all through sampler_anisotropic_wrap) and the normal go to the
// g-buffer.
// World space uvs follow :505-525; alpha tested draws negate velocity.w (:1004), their cutout is the prepass's.
// Deviations: no parallax, terrain, vegetation, decals, paint, impostors or specular aa yet; the previous depth in
// velocity.w is the raw previous ndc depth (spartan linearizes it).
#version 460
#extension GL_GOOGLE_include_directive : require

#include "common_resources.glsl"
#include "common_material.glsl"

layout(location = 0) in vec3 in_position_world;
layout(location = 1) in vec2 in_uv;
layout(location = 2) in vec3 in_normal;
layout(location = 3) in vec3 in_tangent;
layout(location = 4) in vec4 in_position_previous;
layout(location = 5) flat in uint in_material_index;
layout(location = 6) flat in vec4 in_uv_xform_ts;
layout(location = 7) flat in vec4 in_uv_xform_ir;

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
    vec3 emission   = vec3(0.0);
    vec2 uv         = resolve_material_uv(in_uv, in_position_world, normal, in_uv_xform_ts, in_uv_xform_ir);

    // albedo sampling
    if (has_flag(material, 2u))
    {
        vec4 albedo_sample = sample_material_texture(material, material_texture_index_albedo, uv);
        if (has_flag(material, 17u))
            albedo_sample.rgb = srgb_to_linear(albedo_sample.rgb);
        albedo *= albedo_sample;
    }

    // alpha: the opaque pass forces alpha to 1 (step(albedo_sample.a, 1.0) * pass_is_opaque(), the only pass so far)
    albedo.a = 1.0;

    // emission
    if (has_flag(material, 6u))
    {
        vec3 emissive = sample_material_texture(material, material_texture_index_emission, uv).rgb;
        if (has_flag(material, 18u))
            emissive = srgb_to_linear(emissive);
        emission = emissive;
    }
    // emissive_from_albedo strength is authored 0-1
    if (has_flag(material, 15u))
        emission = vec3(material.emissive_strength);

    // normal mapping
    if (has_flag(material, 1u))
    {
        vec3 normal_sample  = sample_material_texture(material, material_texture_index_normal, uv).xyz;
        vec3 tangent_normal = normalize(normal_sample * 2.0 - 1.0);

        // reconstruct z for bc5 two-channel normal maps
        tangent_normal.z = sqrt(max(0.0, 1.0 - dot(tangent_normal.xy, tangent_normal.xy)));

        float normal_intensity = clamp(max(0.01, material.normal), 0.0, 1.0);
        tangent_normal.xy     *= normal_intensity;

        // make_tangent_to_world_matrix (common.hlsl:735), rows t, b, n multiplied by the row vector
        vec3 n = normal;
        vec3 t = normalize(in_tangent - dot(in_tangent, n) * n);
        vec3 b = cross(n, t);
        normal = normalize(tangent_normal.x * t + tangent_normal.y * b + tangent_normal.z * n);
    }

    // packed material texture (occlusion, roughness, metalness)
    bool has_occlusion = has_flag(material, 7u), has_roughness = has_flag(material, 3u), has_metalness = has_flag(material, 4u);
    if (has_occlusion || has_roughness || has_metalness)
    {
        vec4 packed = sample_material_texture(material, material_texture_index_packed, uv);
        occlusion   = mix(occlusion, packed.r, float(has_occlusion));
        roughness  *= mix(1.0, packed.g, float(has_roughness));
        metalness  *= mix(1.0, packed.b, float(has_metalness));
    }

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
    // the depth sign identifies cutout coverage without another mask texture
    float previous_depth = in_position_previous.z / max(in_position_previous.w, 1e-6);
    if (has_flag(material, 16u))
        previous_depth = -previous_depth;
    out_velocity = vec4(position_ndc - position_ndc_previous, 0.0, previous_depth);
    out_emissive = emission;
}
