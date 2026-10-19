// Material helpers shared by the g-buffer and the depth prepass, GLSL ports of SpartanEngine data/shaders @ 4c07baa:
// the material flag bits (common_structs.hlsl:56-63), srgb_to_linear (common_colorspace.hlsl:15), GET_TEXTURE +
// sample_texture (common_resources_buffers.hlsl:114, g_buffer.hlsl:52), rotate_uv_90 (common.hlsl:673),
// compute_world_space_uv (common.hlsl:684), get_alpha_threshold (common.hlsl:1249) and the per-draw uv transform
// (common_vertex_processing.hlsl:1006-1030, g_buffer.hlsl:505-525). Needs common_resources.glsl.
#ifndef PHALANX_COMMON_MATERIAL
#define PHALANX_COMMON_MATERIAL

// common_resources_buffers.hlsl:89-92, the first slot of each gpu texture type
const uint material_texture_index_albedo   = 0u;
const uint material_texture_index_normal   = 4u;
const uint material_texture_index_emission = 8u;
const uint material_texture_index_packed   = 12u;

// flag bits: 1 normal, 2 albedo, 3 roughness, 4 metalness, 6 emission, 7 occlusion, 15 emissive from albedo,
// 16 alpha tested, 17/18 albedo/emission srgb decode; Phalanx adds 31, point sampled (the illegal material)
bool has_flag(MaterialParameters m, uint bit) { return (m.flags & (1u << bit)) != 0u; }

vec3 srgb_to_linear(vec3 color)
{
    vec3 linear_low  = color / 12.92;
    vec3 linear_high = pow(max((color + 0.055) / 1.055, 0.0), vec3(2.4));
    vec3 is_high     = step(vec3(0.0404482362771082), color);
    return mix(linear_low, linear_high, is_high);
}

vec4 sample_material_texture(MaterialParameters m, uint slot, vec2 uv)
{
    uint index = m.texture_indices[slot];
    // Phalanx's bit 31: the illegal material's texture is point sampled (Renderer.MaterialFallback.cs)
    uint sampler_index = has_flag(m, 31u) ? sampler_point_wrap : sampler_anisotropic_wrap;
    return texture(sampler2D(material_textures[nonuniformEXT(index)], GET_SAMPLER(sampler_index)), uv);
}

// rotate uv in 90 degree increments: 0 = 0, 1 = 90, 2 = 180, 3 = 270
vec2 rotate_uv_90(vec2 uv, float rotation_index)
{
    uint r = uint(rotation_index) & 3u;
    vec2 centered = uv - 0.5;
    vec2 rotated  = centered;
    if (r == 1u)      rotated = vec2(-centered.y,  centered.x);
    else if (r == 2u) rotated = vec2(-centered.x, -centered.y);
    else if (r == 3u) rotated = vec2( centered.y, -centered.x);
    return rotated + 0.5;
}

vec2 compute_world_space_uv(vec3 position_world, vec3 normal_world)
{
    vec3 normal = normalize(normal_world);
    vec3 axis_v = vec3(0.0, 1.0, 0.0) - normal * dot(vec3(0.0, 1.0, 0.0), normal);

    // horizontal surfaces need a second reference axis because world up collapses onto the normal
    if (dot(axis_v, axis_v) < 1e-4)
        axis_v = vec3(0.0, 0.0, -1.0) - normal * dot(vec3(0.0, 0.0, -1.0), normal);

    axis_v      = normalize(axis_v);
    vec3 axis_u = normalize(cross(axis_v, normal));
    return vec2(dot(position_world, axis_u), dot(position_world, axis_v));
}

// tiling, offset, mirror and 90 degree rotation, xform_ts = (tiling, offset), xform_ir = (invert, rotation, world space)
vec2 apply_uv_transform(vec2 uv, vec4 xform_ts, vec4 xform_ir)
{
    uv = uv * xform_ts.xy + xform_ts.zw;
    vec2 invert_mask = step(0.5, xform_ir.xy);
    uv = mix(uv, 2.0 * floor(uv) + 1.0 - uv, invert_mask);
    if (xform_ir.z != 0.0)
        uv = rotate_uv_90(uv, xform_ir.z);
    return uv;
}

// the uv a fragment samples its material with: the vertex uv, or the world projected one when the draw asks for it
vec2 resolve_material_uv(vec2 uv_vertex, vec3 position_world, vec3 normal_world, vec4 xform_ts, vec4 xform_ir)
{
    if (xform_ir.w > 0.0)
        return apply_uv_transform(compute_world_space_uv(position_world, normal_world), xform_ts, xform_ir);
    return uv_vertex;
}

// 0.6, and no alpha testing beyond 700 m
float get_alpha_threshold(vec3 position_world)
{
    const float alpha_threshold_default = 0.6;
    const float alpha_max_distance      = 700.0;
    vec3 offset = position_world - buffer_frame.camera_position;
    return dot(offset, offset) > alpha_max_distance * alpha_max_distance ? 0.0 : alpha_threshold_default;
}

#endif
