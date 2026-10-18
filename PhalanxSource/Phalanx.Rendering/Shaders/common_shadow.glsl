// GLSL port of SpartanEngine data/shaders/shadow_mapping.hlsl @ 4c07baa (the whole file: vogel disk pcf with a pcss
// penumbra estimate, slope scaled normal offset bias, near/far cascade blend and the point light cube faces) and of
// the helpers it reads from common.hlsl: ndc_to_uv (:635), cascade_edge_fade (:1136-1148), cascade_contains (:1150),
// light_get_transform / light_get_atlas_scale / light_get_atlas_texel_size (:1269-1282), light_compare_depth
// (:1291-1318) and light_sample_depth (:1320-1340). The atlas is tex2. Needs common_light.glsl.
// vogel_depth and directional_filter_radius are upstream 67a6e8b's (see there), which fix pinned's texel grid stair
// steps. Deviations: compute_normal_offset takes the clip x scale from column 0, not row 0, of the view_projection
// and scales a spot / point light's texel by the light space depth (see there); otherwise none in the maths. The
// foliage normal flip is kept, but no material sets the foliage bit yet. buffer_frame.options bits 28-30 are phalanx
// diagnostics (Renderer.ShadowDiagnostics, the editor's --shadow-* flags); spartan uses bits 0-4.
#ifndef PHALANX_COMMON_SHADOW
#define PHALANX_COMMON_SHADOW

// shadow mapping constants
const uint  g_shadow_sample_count            = 32u;
const float g_shadow_filter_size             = 2.5;
const float g_shadow_cascade_blend_threshold = 0.8;
const uint  g_penumbra_sample_count          = 8u;
const float g_penumbra_filter_size           = 128.0;
const float g_shadow_sample_reciprocal       = 1.0 / float(g_shadow_sample_count);
const float g_minimum_penumbra_size          = 0.5;
const float g_maximum_penumbra_size          = 64.0;
const float g_contact_hardening_factor       = 0.5;
const float g_base_bias_texels               = 0.6;
const float g_slope_bias_texels              = 2.2;

// phalanx diagnostics, off by default (Renderer_ShadowDiagnostics)
const uint g_shadow_option_bias_upstream   = 1u << 28; // spartan's normal offset: row 0 clip scale, unit depth texel
const uint g_shadow_option_blocker_nearest = 1u << 29; // point sampled blocker search
const uint g_shadow_option_early_out       = 1u << 30; // spot / point keep the centre tap early out
bool shadow_option(uint bit) { return (buffer_frame.options & bit) != 0u; }

const vec2 g_vogel_samples_penumbra[8] = vec2[8](
    vec2( 0.250000,  0.000000),
    vec2(-0.319290,  0.292496),
    vec2( 0.048872, -0.556877),
    vec2( 0.402445,  0.524917),
    vec2(-0.738535, -0.130636),
    vec2( 0.699604, -0.445032),
    vec2(-0.234003,  0.870484),
    vec2(-0.446273, -0.859268)
);

vec2 ndc_to_uv(vec2 x) { return x * vec2(0.5, -0.5) + 0.5; }

float cascade_edge_fade(vec2 ndc_xy)
{
    vec2 a  = abs(ndc_xy);
    float m = max(a.x, a.y);
    return 1.0 - smoothstep(0.82, 0.98, m);
}

float cascade_edge_fade(vec3 ndc)
{
    float xy = cascade_edge_fade(ndc.xy);
    float z  = clamp(ndc.z * 20.0, 0.0, 1.0) * clamp((1.0 - ndc.z) * 20.0, 0.0, 1.0);
    return xy * z;
}

bool cascade_contains(vec3 ndc)
{
    vec2 a = abs(ndc.xy);
    return max(a.x, a.y) <= 1.0 && ndc.z >= 0.0 && ndc.z <= 1.0;
}

mat4 light_get_transform(Light light, uint array_index)        { return light_parameters[light.index].transform[array_index]; }
vec2 light_get_atlas_scale(Light light, uint array_index)      { return light_parameters[light.index].atlas_scales[array_index]; }
vec2 light_get_atlas_texel_size(Light light, uint array_index) { return light_parameters[light.index].atlas_texel_sizes[array_index]; }

vec2 light_atlas_uv(Light light, vec3 uv)
{
    uint array_index  = uint(uv.z);
    vec2 atlas_offset = light_parameters[light.index].atlas_offsets[array_index];
    vec2 atlas_scale  = light_parameters[light.index].atlas_scales[array_index];
    return clamp(atlas_offset + uv.xy * atlas_scale, atlas_offset, atlas_offset + atlas_scale);
}

// SampleCmpLevelZero with the reverse-z greater comparison sampler: 1 where the receiver is nearer the light
float light_compare_depth(Light light, vec3 uv, float compare)
{
    return textureLod(sampler2DShadow(tex2, samplers_comparison[sampler_compare_depth]), vec3(light_atlas_uv(light, uv), compare), 0.0);
}

float light_sample_depth(Light light, vec3 uv)
{
    uint s = shadow_option(g_shadow_option_blocker_nearest) ? sampler_point_clamp_border : sampler_bilinear_clamp_border;
    return textureLod(sampler2D(tex2, samplers[s]), light_atlas_uv(light, uv), 0.0).r;
}

vec2 rotate_2d(vec2 v, float angle)
{
    float sine = sin(angle), cosine = cos(angle);
    return vec2(v.x * cosine - v.y * sine, v.x * sine + v.y * cosine);
}

vec2 vogel_disk_sample(uint sample_index, uint sample_count, float angle)
{
    if (sample_count == g_penumbra_sample_count)
        return rotate_2d(g_vogel_samples_penumbra[sample_index], angle);

    // fallback calculation
    const float golden_angle = 2.399963;
    float radius = sqrt(float(sample_index) + 0.5) / sqrt(float(sample_count));
    float theta  = float(sample_index) * golden_angle + angle;
    return vec2(cos(theta), sin(theta)) * radius;
}

// estimate penumbra size using pcss
float compute_penumbra(Light light, float rotation_angle, vec3 sample_coords, float receiver_depth, float light_distance, vec2 texel_size_cascade_local)
{
    float penumbra          = g_minimum_penumbra_size;
    float blocker_depth_sum = 0.0;
    uint  blocker_count     = 0u;
    float search_radius     = g_penumbra_filter_size * texel_size_cascade_local.x;

    for (uint i = 0u; i < g_penumbra_sample_count; i++)
    {
        vec2 offset = vogel_disk_sample(i, g_penumbra_sample_count, rotation_angle) * search_radius;
        float depth = light_sample_depth(light, sample_coords + vec3(offset, 0.0));
        if (depth > receiver_depth + 0.0001)
        {
            blocker_depth_sum += depth;
            blocker_count++;
        }
    }

    if (blocker_count > 0u)
    {
        float blocker_depth_avg = blocker_depth_sum / float(blocker_count);
        float depth_difference  = receiver_depth - blocker_depth_avg;

        if (depth_difference > 0.0 && blocker_depth_avg > 0.0)
        {
            float light_size_factor = clamp(light_distance * 0.1, 0.0, 1.0);
            penumbra                = (depth_difference / blocker_depth_avg) * light_size_factor * search_radius;

            // contact hardening
            float contact_factor = clamp(depth_difference * 1000.0, 0.0, 1.0);
            penumbra             = mix(penumbra * g_contact_hardening_factor, penumbra, contact_factor);
        }
    }

    return clamp(penumbra, g_minimum_penumbra_size, g_maximum_penumbra_size);
}

// upstream 67a6e8b directional_filter_radius (shadow_mapping.hlsl:113-141): orthographic reverse-z stores nearer
// blockers at larger depth, the separation is converted to metres before the sun's angular radius is applied
vec2 directional_filter_radius(Light light, vec3 coords, float receiver_depth, vec2 texel)
{
    // hlsl transform[r][c] is m[r][c] here (row-major upload): column 0 / 1 / 2 are the clip x / y / z scales
    mat4 m           = light_get_transform(light, uint(coords.z));
    vec2 world_to_uv = 0.5 * vec2(length(vec3(m[0][0], m[1][0], m[2][0])), length(vec3(m[0][1], m[1][1], m[2][1])));
    float world_to_depth = length(vec3(m[0][2], m[1][2], m[2][2]));
    const float sun_angular_radius = 0.00465;
    vec2 search_radius   = max(texel * 2.0, 50.0 * sun_angular_radius * world_to_uv);
    float separation_sum = 0.0;
    uint blocker_count   = 0u;
    for (uint i = 0u; i < g_penumbra_sample_count; i++)
    {
        vec2 uv = coords.xy + g_vogel_samples_penumbra[i] * search_radius;
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
            continue;
        float blocker = light_sample_depth(light, vec3(uv, coords.z));
        if (blocker > receiver_depth + 1e-6)
        {
            separation_sum += (blocker - receiver_depth) / max(world_to_depth, 1e-6);
            blocker_count++;
        }
    }
    float separation = separation_sum / max(float(blocker_count), 1.0);
    return clamp(separation * sun_angular_radius * world_to_uv, texel * 1.5, texel * 32.0);
}

// shadow factor using vogel disk sampling, upstream 67a6e8b (shadow_mapping.hlsl:143-193): no centre tap early out
// for any light (a fully lit or shadowed centre doesn't make the footprint uniform, skipping the taps exposes the
// texel grid as rows of lines), the texel size is the cpu's 1 / slice resolution (pinned 4c07baa divided it by the
// atlas scale again, :131-139), the near sun cascade filters by the sun's angular size, out of slice taps count as lit
// and taps stay half a texel inside the slice
float vogel_depth(Light light, Surface surface, vec3 sample_coords, float receiver_depth, float filter_size_multiplier)
{
    uint cascade_index   = uint(sample_coords.z);
    bool far_directional = light_is_directional(light) && cascade_index == 1u;

    // diagnostic: the shadow-lines build's spot / point path, pinned 4c07baa's centre tap early out (:119-127)
    if (!light_is_directional(light) && shadow_option(g_shadow_option_early_out))
    {
        float center = light_compare_depth(light, sample_coords, receiver_depth);
        if (center <= 0.001)
            return 0.0;
        if (center >= 0.999)
            return 1.0;
    }

    vec2 texel_size_cascade_local = light_get_atlas_texel_size(light, cascade_index);
    float rotation_angle          = float(cascade_index) * 0.785398163;
    float shadow_factor           = 0.0;

    // the far cascade uses a fixed kernel
    vec2 filter_size = texel_size_cascade_local * g_shadow_filter_size;
    if (light_is_directional(light) && !far_directional)
    {
        filter_size = directional_filter_radius(light, sample_coords, receiver_depth, texel_size_cascade_local);
    }
    else if (!far_directional)
    {
        float light_distance = length(surface.position - light.position);
        filter_size *= compute_penumbra(light, rotation_angle, sample_coords, receiver_depth, light_distance, texel_size_cascade_local);
    }
    filter_size *= filter_size_multiplier;

    for (uint i = 0u; i < g_shadow_sample_count; i++)
    {
        vec2 offset    = vogel_disk_sample(i, g_shadow_sample_count, rotation_angle) * filter_size;
        vec2 sample_uv = sample_coords.xy + offset;

        if (any(lessThan(sample_uv, vec2(0.0))) || any(greaterThan(sample_uv, vec2(1.0))))
        {
            shadow_factor += 1.0;
            continue;
        }
        // keep the bilinear taps inside this slice instead of reading a neighbour
        vec2 uv_clamped = clamp(sample_uv, texel_size_cascade_local * 0.5, 1.0 - texel_size_cascade_local * 0.5);
        shadow_factor  += light_compare_depth(light, vec3(uv_clamped, sample_coords.z), receiver_depth);
    }

    return shadow_factor * g_shadow_sample_reciprocal;
}

// slope scaled normal offset in world space
vec3 compute_normal_offset(Surface surface, Light light, uint cascade_index)
{
    // world-space texel size from the projection. Deviation: spartan takes length(transform[0].xyz), row 0 of the
    // row-vector view_projection (shadow_mapping.hlsl:186-199 @ 4c07baa, :199-212 @ 67a6e8b), which mixes the x, y
    // and z axes' x components and is only the clip x scale when the light's right vector is world x. Column 0 is the
    // clip x scale for any orientation.
    mat4 m = light_get_transform(light, cascade_index);
    bool upstream = shadow_option(g_shadow_option_bias_upstream);
    vec3 clip_x   = upstream ? vec3(m[0][0], m[0][1], m[0][2]) : vec3(m[0][0], m[1][0], m[2][0]);
    float world_frustum_width = 2.0 / length(clip_x);
    float texel_size_world    = world_frustum_width * light_get_atlas_texel_size(light, cascade_index).x;

    // Deviation: a perspective (spot, point) projection divides by the view depth, so a texel is that depth times
    // wider than spartan's size, which holds at 1 m only. A few metres out the offset is a fraction of a texel and the
    // receiver self shadows in rows along the texel grid. The depth is the selected face's: for a point light the
    // largest axis of the light to surface vector (the face selection below uses the same test), for a spot light
    // clip w. The shadow-fix build took clip w of face 0 for point lights, which is negative on the far side of the
    // light and clamped the offset to zero there.
    if (!light_is_directional(light) && !upstream)
    {
        vec3 d = surface.position - light.position;
        float depth = light_is_point(light) ? max(max(abs(d.x), abs(d.y)), abs(d.z)) : (m * vec4(surface.position, 1.0)).w;
        texel_size_world *= max(depth, 0.0);
    }

    vec3 light_dir = light_is_directional(light) ? normalize(-light.forward) : normalize(light.position - surface.position);
    float n_dot_l  = dot(surface.normal, light_dir);
    float slope    = sqrt(clamp(1.0 - n_dot_l * n_dot_l, 0.0, 1.0));

    // far cascade texels are meters wide, extra bias keeps terrain from self shadowing
    float cascade_bias  = (light_is_directional(light) && cascade_index == 1u) ? 2.0 : 1.0;
    float offset_amount = (g_base_bias_texels + (slope * g_slope_bias_texels)) * texel_size_world * cascade_bias;

    float normal_sign = surface_is_foliage(surface) && surface.subsurface_scattering > 0.0 && n_dot_l < 0.0 ? -1.0 : 1.0;
    return surface.normal * (offset_amount * normal_sign);
}

vec3 light_project(Light light, uint index, vec3 position_world)
{
    // row-major upload: GLSL M * v is spartan's mul(v, M)
    vec4 clip = light_get_transform(light, index) * vec4(position_world, 1.0);
    return clip.xyz / clip.w;
}

float compute_shadow(Surface surface, Light light)
{
    vec3 to_light = light.position - surface.position;
    float dist_sq = dot(to_light, to_light);

    // quadratic falloff for point/spot lights
    float fade = 1.0;
    if (light_is_point(light) || light_is_spot(light))
    {
        fade = clamp(1.0 - dist_sq / (light.far * light.far), 0.0, 1.0);
        fade = fade * fade;
        if (fade <= 0.0)
            return 1.0;
    }

    float shadow = 1.0;

    // point light (cube faces in the atlas)
    if (light_is_point(light))
    {
        vec3 position_world = surface.position + compute_normal_offset(surface, light, 0u);
        vec3 light_to_pixel = position_world - light.position;
        vec3 abs_dir        = abs(light_to_pixel);

        uint face_index = (abs_dir.x >= abs_dir.y && abs_dir.x >= abs_dir.z) ? (light_to_pixel.x > 0.0 ? 0u : 1u) :
                          (abs_dir.y >= abs_dir.z)                           ? (light_to_pixel.y > 0.0 ? 2u : 3u) :
                                                                               (light_to_pixel.z > 0.0 ? 4u : 5u);

        vec3 ndc = light_project(light, face_index, position_world);
        shadow   = vogel_depth(light, surface, vec3(ndc_to_uv(ndc.xy), float(face_index)), ndc.z, 1.0);
        shadow   = mix(1.0, shadow, fade);
    }
    // directional or spot light (cascaded shadow maps)
    else
    {
        const uint near_cascade = 0u;

        vec3 position_world = surface.position + compute_normal_offset(surface, light, near_cascade);
        vec3 ndc_near       = light_project(light, near_cascade, position_world);
        vec2 uv_near        = ndc_to_uv(ndc_near.xy);
        bool near_valid     = cascade_contains(ndc_near);

        float shadow_near = 1.0;
        if (near_valid)
            shadow_near = vogel_depth(light, surface, vec3(uv_near, float(near_cascade)), ndc_near.z, 1.0);
        shadow = shadow_near;

        // directional cascade blending
        if (light_is_directional(light))
        {
            const uint far_cascade = 1u;

            vec3 position_world_far = surface.position + compute_normal_offset(surface, light, far_cascade);
            vec3 ndc_far            = light_project(light, far_cascade, position_world_far);
            vec2 uv_far             = ndc_to_uv(ndc_far.xy);
            bool far_valid          = cascade_contains(ndc_far);

            if (near_valid && far_valid)
            {
                float shadow_far = vogel_depth(light, surface, vec3(uv_far, float(far_cascade)), ndc_far.z, 1.0);

                // smooth blend based on the distance from the near cascade's center
                vec2 ndc_dist_from_center = abs(ndc_near.xy);
                float max_dist            = max(ndc_dist_from_center.x, ndc_dist_from_center.y);
                float blend_factor        = smoothstep(0.8, 0.95, max_dist);
                shadow                    = mix(shadow_near, shadow_far, blend_factor);
            }
            else if (!near_valid && far_valid)
            {
                shadow = vogel_depth(light, surface, vec3(uv_far, float(far_cascade)), ndc_far.z, 1.0);
            }

            if (far_valid)
                shadow = mix(1.0, shadow, cascade_edge_fade(ndc_far));
        }

        if (light_is_spot(light))
            shadow = mix(1.0, shadow, fade);
    }

    return shadow;
}

#endif
