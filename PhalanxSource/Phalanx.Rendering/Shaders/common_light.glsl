// GLSL port of the lighting core of SpartanEngine data/shaders @ 4c07baa: the constants and cone helpers of
// shared_lighting.h, radiometric_to_photometric / get_effective_exposure / luminance / validate_output /
// get_position / microw_shadowing_cod (common.hlsl:37-71, :504-518, :794, :905, :1124), Surface::Build
// (common_structs.hlsl:73-176), Light and AngularInfo (common_structs.hlsl:210-517) and the isotropic brdf
// (brdf.hlsl:1-156), the sun and night sky helpers (common.hlsl:80-137), hdr_clamp_chroma (:495),
// direction_sphere_uv (:891) and compute_multiscatter_energy_split_sum (brdf.hlsl:129). Needs common_resources.glsl.
// Deviations: the surface has no clearcoat, anisotropy, sheen, flakes, pearl/coat tint, subsurface scattering,
// decals or ssao (bent normal = normal, occlusion = the g-buffer's); lights have no area shape, ies profiles or
// shadows; auto exposure isn't ported, the exposure is the camera's manual one. Surface::Build clamps the material
// index it decodes (see there).
#ifndef PHALANX_COMMON_LIGHT
#define PHALANX_COMMON_LIGHT

const float PI                         = 3.14159265;
const float PI2                        = 6.28318530;
const float INV_PI                     = 0.31830988;
const float FLT_MIN                    = 0.00000001;
const float FLT_MAX_16U                = 65504.0;
const float lighting_luminous_efficacy = 683.0;

float radiometric_to_photometric(float value) { return value * lighting_luminous_efficacy; }
vec3  radiometric_to_photometric(vec3 value)  { return value * lighting_luminous_efficacy; }
float photometric_to_radiometric(float value) { return value / lighting_luminous_efficacy; }
vec3  photometric_to_radiometric(vec3 value)  { return value / lighting_luminous_efficacy; }

// the manual camera exposure (auto exposure isn't ported, camera_exposure_mode stays 0)
float get_effective_exposure() { return buffer_frame.camera_exposure; }

float luminance(vec3 color) { return max(dot(color, vec3(0.2126, 0.7152, 0.0722)), FLT_MIN); }

vec4 validate_output(vec4 value)
{
    value = clamp(value, 0.0, FLT_MAX_16U);
    return mix(value, vec4(1.0), isnan(value)); // nan reads as 1, like spartan
}

// screen uv (y down) and reverse-z depth to world space
vec3 get_position(float z, vec2 uv)
{
    vec4 pos_clip  = vec4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, z, 1.0);
    vec4 pos_world = buffer_frame.view_projection_inverted * pos_clip;
    return pos_world.xyz / pos_world.w;
}

// common.hlsl:504-507, clamp to the float16 range
float saturate_16(float x) { return clamp(x, 0.0, FLT_MAX_16U); }
vec2  saturate_16(vec2 x)  { return clamp(x, 0.0, FLT_MAX_16U); }
vec3  saturate_16(vec3 x)  { return clamp(x, 0.0, FLT_MAX_16U); }
vec4  saturate_16(vec4 x)  { return clamp(x, 0.0, FLT_MAX_16U); }

// common.hlsl:640-643, flip y for dx style
vec2 uv_to_ndc(vec2 uv) { return vec2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0); }

// common.hlsl:645-648, the render target area the dynamic resolution scale leaves active (phalanx: scale 1)
vec2 get_render_resolution_active()
{
    return max(floor(buffer_frame.resolution_render * buffer_frame.resolution_scale), vec2(1.0));
}

// common.hlsl:763-770, reverse-z depth to view distance (near at z 1, far at z 0)
float linearize_depth(float z)
{
    float near = buffer_frame.camera_near;
    float far  = buffer_frame.camera_far;
    float z_n  = 2.0 * z - 1.0;
    return 2.0 * far * near / (near + far - z_n * (near - far));
}

// chan 2018 microshadowing, ao driven terminator darkening, occlusion 1 means no effect
float microw_shadowing_cod(float n_dot_l, float visibility)
{
    float aperture     = 2.0 * visibility;
    float micro_shadow = clamp(n_dot_l + aperture - 1.0, 0.0, 1.0);
    return micro_shadow * micro_shadow;
}

// shared_lighting.h
float lighting_spot_half_angle(float angle) { return clamp(angle, 0.01, 1.57079632679 - 0.001); }

struct Surface
{
    uint  flags;
    float subsurface_scattering;
    vec3  albedo;
    float alpha;
    float roughness;
    float roughness_alpha;
    float metallic;
    float occlusion;
    vec3  emissive;
    vec3  F0;
    vec2  uv;
    float depth;
    vec3  position;
    vec3  normal;
    vec3  camera_to_pixel;
    float camera_to_pixel_length;
    vec3  diffuse_energy;
};

bool surface_is_sky(Surface s)         { return s.alpha == 0.0; }
bool surface_is_opaque(Surface s)      { return s.alpha == 1.0; }
bool surface_is_transparent(Surface s) { return s.alpha > 0.0 && s.alpha < 1.0; }

// Surface::Build, point sampled through texelFetch (render resolution, same as sampler_point_clamp at texel centres)
// common_structs.hlsl:73, the foliage bit (grass blades and flowers aren't ported)
bool surface_is_foliage(Surface s) { return (s.flags & (1u << 21)) != 0u; }

Surface surface_build(uvec2 position_screen, vec2 resolution_out, bool use_albedo, bool replace_color_with_one)
{
    Surface s;
    ivec2 p = ivec2(position_screen);
    s.uv    = (vec2(position_screen) + 0.5) / resolution_out;

    vec4 sample_albedo   = use_albedo ? texelFetch(tex_albedo, p, 0) : vec4(0.0);
    vec4 sample_normal   = texelFetch(tex_normal, p, 0);
    vec4 sample_material = texelFetch(tex_material, p, 0);
    float sample_depth   = texelFetch(tex_depth, p, 0).r;
    // deviation: the index is clamped to the buffer (spartan common_structs.hlsl:89 reads it unchecked); the half the
    // g-buffer holds decodes to 0..64511 while the buffer has rhi_max_array_size (16384) entries, so a pixel the
    // g-buffer didn't write as expected can't read past it
    uint material_index = min(unpack_material_index(sample_normal.a), uint(material_parameters.length()) - 1u);
    MaterialParameters material = material_parameters[material_index];

    s.depth     = sample_depth;
    s.normal    = sample_normal.xyz;
    s.flags     = material.flags;
    s.subsurface_scattering = material.subsurface_scattering;
    s.albedo    = replace_color_with_one ? vec3(1.0) : sample_albedo.rgb;
    s.alpha     = sample_albedo.a;
    s.roughness = sample_material.r;
    s.metallic  = sample_material.g;
    s.emissive  = texelFetch(tex_emissive, p, 0).rgb;
    // glass is always dielectric, force metallic 0 so f0 stays at the 0.04 dielectric value
    bool alpha_transparent = s.alpha > 0.0 && s.alpha < 1.0;
    s.metallic       = alpha_transparent ? 0.0 : s.metallic;
    s.F0             = mix(vec3(0.04), sample_albedo.rgb, s.metallic);
    s.diffuse_energy = vec3(1.0);
    s.roughness_alpha = s.roughness * s.roughness;
    s.occlusion      = sample_albedo.a >= 0.999 ? sample_material.a : 1.0;

    s.position               = get_position(s.depth, s.uv);
    s.camera_to_pixel        = s.position - buffer_frame.camera_position;
    s.camera_to_pixel_length = length(s.camera_to_pixel);
    s.camera_to_pixel        = normalize(s.camera_to_pixel);
    return s;
}

struct Light
{
    uint  index;
    uint  flags;
    vec3  color;
    vec3  position;
    float intensity;
    vec3  to_pixel;
    vec3  forward;
    float distance_to_pixel;
    float angle;
    float far;
    vec3  radiance;
    float n_dot_l;
    float attenuation;
    float cos_outer;
    float cos_inner;
    float angle_scale;
};

bool light_is_directional(Light l) { return (l.flags & (1u << 0)) != 0u; }
bool light_is_point(Light l)       { return (l.flags & (1u << 1)) != 0u; }
bool light_is_spot(Light l)        { return (l.flags & (1u << 2)) != 0u; }
bool light_is_area(Light l)        { return (l.flags & (1u << 6)) != 0u; }
bool light_has_shadows(Light l)    { return (l.flags & (1u << 3)) != 0u; }

float light_attenuation_range(Light l, float distance_to_light)
{
    if (l.far <= 0.0)
        return 0.0;
    // smooth window so lights fade out instead of popping at the range boundary
    float ratio  = distance_to_light / l.far;
    float ratio2 = ratio * ratio;
    float window = clamp(1.0 - ratio2 * ratio2, 0.0, 1.0);
    return window * window;
}

float light_attenuation_inverse_square(float distance_to_light) { return 1.0 / (distance_to_light * distance_to_light + 0.0001); }

float light_attenuation_distance(Light l, vec3 surface_position)
{
    float d = length(surface_position - l.position);
    return light_attenuation_inverse_square(d) * light_attenuation_range(l, d);
}

float light_attenuation_angle(Light l)
{
    float cd          = dot(l.to_pixel, l.forward);
    float attenuation = clamp((cd - l.cos_outer) * l.angle_scale, 0.0, 1.0);
    return attenuation * attenuation;
}

float light_attenuation(Light l, vec3 surface_position)
{
    if (light_is_directional(l)) return 1.0;
    if (light_is_point(l))       return light_attenuation_distance(l, surface_position);
    if (light_is_spot(l))        return light_attenuation_distance(l, surface_position) * light_attenuation_angle(l);
    return 0.0; // area lights aren't ported
}

Light light_build(uint light_index, Surface surface)
{
    LightParameters p = light_parameters[light_index];
    Light l;
    l.index             = light_index;
    l.flags             = p.flags;
    l.color             = p.color.rgb;
    l.position          = p.position;
    l.intensity         = p.intensity;
    l.far               = p.range;
    l.angle             = lighting_spot_half_angle(p.angle);
    l.forward           = (light_is_point(l) && !light_is_area(l)) ? vec3(0.0, 0.0, 1.0) : p.direction;
    l.distance_to_pixel = length(surface.position - l.position);

    // spot cone trig once per pixel per light
    l.cos_outer   = cos(l.angle);
    l.cos_inner   = cos(l.angle * 0.9);
    l.angle_scale = 1.0 / max(0.000001, l.cos_inner - l.cos_outer);

    l.to_pixel    = light_is_directional(l) ? normalize(l.forward) : normalize(surface.position - l.position);
    l.n_dot_l     = clamp(dot(surface.normal, -l.to_pixel), 0.0, 1.0);
    l.attenuation = light_attenuation(l, surface.position);
    l.radiance    = l.color * l.intensity * l.attenuation * l.n_dot_l;
    return l;
}

struct AngularInfo
{
    vec3  n, l, v, h;
    float l_dot_h, v_dot_h, n_dot_v, n_dot_h, n_dot_l;
};

AngularInfo angular_info_build(Light light, Surface surface)
{
    AngularInfo a;
    a.n = surface.normal;
    a.v = -surface.camera_to_pixel;
    a.l = -light.to_pixel;
    a.h = normalize(a.l + a.v);
    a.n_dot_l = clamp(dot(a.n, a.l), 0.0, 1.0);
    a.n_dot_v = clamp(dot(a.n, a.v), 0.0, 1.0);
    a.n_dot_h = clamp(dot(a.n, a.h), 0.0, 1.0);
    a.l_dot_h = clamp(dot(a.l, a.h), 0.0, 1.0);
    a.v_dot_h = clamp(dot(a.v, a.h), 0.0, 1.0);
    return a;
}

// brdf.hlsl
vec3  compute_f90(vec3 f0) { return vec3(clamp(50.0 * dot(f0, vec3(0.33333)), 0.0, 1.0)); }
float pow5(float x) { float x2 = x * x; return x2 * x2 * x; }
vec3  F_Schlick(vec3 f0, vec3 f90, float v_dot_h) { return f0 + (f90 - f0) * pow5(1.0 - v_dot_h); }
float F_Schlick(float f0, float f90, float v_dot_h) { return f0 + (f90 - f0) * pow5(1.0 - v_dot_h); }

// burley 2012
vec3 Diffuse_Burley(vec3 diffuse_color, float roughness, float n_dot_v, float n_dot_l, float v_dot_h)
{
    float f90           = 0.5 + 2.0 * v_dot_h * v_dot_h * roughness;
    float light_scatter = F_Schlick(1.0, f90, n_dot_l);
    float view_scatter  = F_Schlick(1.0, f90, n_dot_v);
    return diffuse_color * (light_scatter * view_scatter * INV_PI);
}
vec3 BRDF_Diffuse(Surface surface, AngularInfo a) { return Diffuse_Burley(surface.albedo, surface.roughness, a.n_dot_v, a.n_dot_l, a.v_dot_h); }

// call of duty: wwii roughness to alpha remap
float D_GGX_Alpha(float roughness)
{
    float gloss = 1.0 - roughness;
    return sqrt(2.0 / (1.0 + pow(2.0, 18.0 * gloss)));
}
float D_GGX(float n_dot_h, float a2)
{
    float d = n_dot_h * n_dot_h * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d + FLT_MIN);
}
float V_SmithGGX(float n_dot_v, float n_dot_l, float a2)
{
    float ggxv = n_dot_l * sqrt(n_dot_v * n_dot_v * (1.0 - a2) + a2);
    float ggxl = n_dot_v * sqrt(n_dot_l * n_dot_l * (1.0 - a2) + a2);
    return 0.5 / max(ggxv + ggxl, FLT_MIN);
}
vec3 compute_diffuse_energy(vec3 F, float metallic) { return (1.0 - F) * (1.0 - metallic); }
vec3 compute_multiscatter_energy(vec3 f0, float n_dot_v, float roughness)
{
    float dhr         = mix(1.0 - roughness * 0.7, 1.0, pow5(1.0 - n_dot_v));
    vec3  energy_loss = (1.0 - dhr) * (1.0 - f0);
    return 1.0 + f0 * energy_loss / (1.0 - energy_loss + FLT_MIN);
}
vec3 BRDF_Specular_Isotropic(inout Surface surface, AngularInfo a)
{
    float alpha = D_GGX_Alpha(surface.roughness);
    float a2    = alpha * alpha;
    float D     = D_GGX(a.n_dot_h, a2);
    float V     = V_SmithGGX(a.n_dot_v, a.n_dot_l, a2);
    vec3  F     = F_Schlick(surface.F0, compute_f90(surface.F0), a.v_dot_h);
    vec3  Fr    = D * V * F;
    Fr                    *= compute_multiscatter_energy(surface.F0, a.n_dot_v, surface.roughness);
    surface.diffuse_energy *= compute_diffuse_energy(F, surface.metallic);
    return Fr;
}

// brdf.hlsl:129-133, split sum multiscatter compensation (kulla conty style energy from the env lut)
vec3 compute_multiscatter_energy_split_sum(vec3 f0, vec2 env_brdf)
{
    float single_scatter = max(env_brdf.x + env_brdf.y, 0.05);
    return 1.0 + f0 * (1.0 / single_scatter - 1.0);
}

// common.hlsl:80-104, the sun is light slot 0. The cpu writes the atmospheric transmittance toward the sun into the
// directional light color, so light.color is the ground level sun tint and light.intensity the top of atmosphere
// radiance (lux / 683).
// Deviation: spartan trusts slot 0 to be the sun or a zero sentinel; with local lights only Renderer.cpp fills slot 0
// with a point or spot light and the sky treats it as the sun. Phalanx orders directional lights first like spartan
// and also checks the flag here, a slot 0 that isn't directional reads as no sun (intensity 0, pointing down).
bool sun_present() { return (light_parameters[0].flags & (1u << 0)) != 0u; }

vec3 get_sun_direction()
{
    return sun_present() ? normalize(-light_parameters[0].direction) : vec3(0.0, -1.0, 0.0);
}

vec3  get_sun_color()     { return sun_present() ? light_parameters[0].color.rgb : vec3(0.0); }
float get_sun_intensity() { return sun_present() ? light_parameters[0].intensity : 0.0; }

// sun radiance before entering the atmosphere, neutral white, the sky applies its own per path transmittance so
// feeding it a pre tinted colour would extinct the light twice
vec3 get_sun_radiance_toa() { return vec3(get_sun_intensity()); }

// common.hlsl:126-137, shared night radiometric levels
const vec3  night_sky_zenith_rad  = vec3(0.00009, 0.00015, 0.00030);
const vec3  night_sky_horizon_rad = vec3(0.00022, 0.00030, 0.00048);
const vec3  night_airglow_rad     = vec3(0.00010, 0.00014, 0.00007);
// moon/sun irradiance ratio applied on top of get_sun_radiance_toa(), keep near physical ~4e-6
const float night_moon_to_sun     = 2.0e-6;

vec3 night_sky_radiance(float up_y)
{
    float h = clamp(up_y * 0.5 + 0.5, 0.0, 1.0);
    return mix(night_sky_horizon_rad, night_sky_zenith_rad, h);
}

// common.hlsl:495-499, chromaticity preserving hdr clamp, scales the whole colour when its peak channel exceeds the
// cap so the sun disc keeps its tint
vec3 hdr_clamp_chroma(vec3 color, float max_value)
{
    float peak = max(color.r, max(color.g, color.b));
    return (peak > max_value) ? color * (max_value / peak) : color;
}

// common.hlsl:891-897, direction to equirectangular uv (the skysphere panorama layout)
vec2 direction_sphere_uv(vec3 direction)
{
    float u = 0.5 + atan(direction.z, direction.x) / PI2;
    float v = 0.5 - asin(clamp(direction.y, -1.0, 1.0)) / PI;
    return vec2(u, v);
}

// subsurface_scattering.hlsl:40-45, transmitted light crosses the chlorophyll twice, a deeper yellower green
vec3 foliage_transmission_tint(vec3 albedo)
{
    float peak  = max(max(albedo.r, albedo.g), albedo.b) + 1e-4;
    vec3 chroma = clamp(albedo / peak, 0.0, 1.0);
    return chroma * chroma * vec3(0.85, 1.0, 0.8);
}

#endif
