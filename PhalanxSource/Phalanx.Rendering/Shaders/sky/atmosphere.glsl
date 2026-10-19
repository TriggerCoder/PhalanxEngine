// GLSL port of SpartanEngine data/shaders/sky/atmosphere.hlsl @ 4c07baa (identical at upstream 67a6e8b): the
// atmosphere model (rayleigh, mie and ozone), the transmittance and multiple scattering integrals and the per metre
// scattering source the sky view lut marches. Needs common_resources.glsl and common_light.glsl. The transmittance
// lut is tex and the multiscatter lut tex2, the bilinear clamp sampler is implied (spartan: parameters).
#ifndef PHALANX_ATMOSPHERE
#define PHALANX_ATMOSPHERE

#include "planet.glsl"

// constants - atmosphere
const vec3  up_direction      = vec3(0.0, 1.0, 0.0);
const float earth_radius      = planet_earth_radius;
const float atmosphere_radius = planet_atmosphere_radius;
const vec3  earth_center      = planet_earth_center;

// constants - scattering coefficients at sea level
const vec3  rayleigh_scatter = vec3(5.802e-6, 13.558e-6, 33.1e-6);
const float rayleigh_height  = 8000.0;
const vec3  mie_scatter      = vec3(3.996e-6, 3.996e-6, 3.996e-6);
const vec3  mie_extinction   = vec3(4.4e-6, 4.4e-6, 4.4e-6);
const float mie_height       = 1200.0;
const float mie_g            = 0.8;

// constants - ozone
const vec3  ozone_absorption    = vec3(0.65e-6, 1.881e-6, 0.085e-6);
const float ozone_center_height = 25000.0;
const float ozone_width         = 15000.0;

// constants - ground
const vec3 ground_albedo = vec3(0.3, 0.3, 0.3);

// constants - sampling
const int transmittance_samples = 64;
const int multiscatter_samples  = 32;
const int scattering_samples    = 32;

float safe_sqrt(float x) { return sqrt(max(0.0, x)); }

vec2 ray_sphere_intersect(vec3 origin, vec3 direction, vec3 center, float radius)
{
    vec3 oc = origin - center;
    float b = dot(direction, oc);
    float radial_distance = length(oc);
    float c = (radial_distance - radius) * (radial_distance + radius);
    float discriminant = b * b - c;

    if (discriminant < 0.0)
        return vec2(-1.0, -1.0);

    float d = sqrt(discriminant);
    // avoid cancellation for the root close to the surface
    float q = -b - (b >= 0.0 ? d : -d);
    if (abs(q) < 1e-6) return vec2(0.0, 0.0);
    float other = c / q;
    return vec2(min(q, other), max(q, other));
}

float get_height(vec3 position)
{
    return length(position - earth_center) - earth_radius;
}

// keep the camera inside the atmosphere shell, shared by the sky view lut and the panorama bake
vec3 clamp_camera_to_atmosphere(vec3 cam_pos)
{
    float cam_h = get_height(cam_pos);
    if (cam_h < 1.0)
        return earth_center + normalize(cam_pos - earth_center) * (earth_radius + 1.0);
    if (cam_h > atmosphere_radius - earth_radius)
        return earth_center + normalize(cam_pos - earth_center) * (atmosphere_radius - 1.0);
    return cam_pos;
}

// density functions
float get_rayleigh_density(float height) { return exp(-height / rayleigh_height); }
float get_mie_density(float height)      { return exp(-height / mie_height); }
float get_ozone_density(float height)    { return max(0.0, 1.0 - abs(height - ozone_center_height) / ozone_width); }

// exact constant medium integral with a cancellation free vacuum limit
vec3 atmosphere_segment_weight(vec3 extinction, float distance_)
{
    vec3 weight;
    for (int channel = 0; channel < 3; ++channel)
    {
        float tau = extinction[channel] * distance_;
        weight[channel] = tau < 0.001
            ? distance_ * (1.0 - tau * 0.5 + tau * tau / 6.0)
            : (1.0 - exp(-tau)) / extinction[channel];
    }
    return weight;
}

vec3 get_extinction(float height)
{
    return rayleigh_scatter * get_rayleigh_density(height) +
           mie_extinction * get_mie_density(height) +
           ozone_absorption * get_ozone_density(height);
}

vec3 get_scattering(float height)
{
    return rayleigh_scatter * get_rayleigh_density(height) + mie_scatter * get_mie_density(height);
}

// phase functions
float rayleigh_phase(float cos_theta)
{
    return (3.0 / (16.0 * PI)) * (1.0 + cos_theta * cos_theta);
}

float cornette_shanks_phase(float cos_theta, float g)
{
    float g2    = g * g;
    float num   = 3.0 * (1.0 - g2) * (1.0 + cos_theta * cos_theta);
    float denom = (8.0 * PI) * (2.0 + g2) * pow(1.0 + g2 - 2.0 * g * cos_theta, 1.5);
    return num / max(denom, 0.0001);
}

// inverse of planet_transmittance_uv, used when baking the lookup
void transmittance_uv_to_params(vec2 uv, out float height, out float cos_zenith)
{
    float h = uv.y * uv.y;
    height = earth_radius + h * (atmosphere_radius - earth_radius);

    float rho         = safe_sqrt(max(0.0, (height - earth_radius) * (height + earth_radius)));
    float cos_horizon = -rho / height;

    if (uv.x > 0.5)
    {
        float t = (uv.x - 0.5) * 2.0;
        cos_zenith = cos_horizon + t * t * (1.0 - cos_horizon);
    }
    else
    {
        float t = 1.0 - uv.x * 2.0;
        cos_zenith = cos_horizon - t * t * (1.0 + cos_horizon);
    }
}

// optical depth from a position to the top of the atmosphere
vec3 compute_transmittance_to_top(vec3 position, vec3 direction)
{
    vec2 intersect = ray_sphere_intersect(position, direction, earth_center, atmosphere_radius);
    float t_max = intersect.y;
    if (t_max < 0.0)
        return vec3(1.0);

    // rays that hit the planet are fully occluded, zero gives every consumer the earth shadow
    vec2 ground_hit = ray_sphere_intersect(position, direction, earth_center, earth_radius);
    if (ground_hit.x >= 0.0 && dot(position - earth_center, direction) < 0.0)
        return vec3(0.0);

    // quadratic spacing resolves the dense aerosol layer close to the observer
    float previous_t   = 0.0;
    vec3 optical_depth = vec3(0.0);
    vec3 prev_ext      = get_extinction(get_height(position));

    for (int i = 1; i <= transmittance_samples; i++)
    {
        float u  = float(i) / float(transmittance_samples);
        float t  = t_max * u * u;
        float dt = t - previous_t;
        previous_t = t;
        vec3 sample_pos = position + direction * t;
        float height    = get_height(sample_pos);
        if (height < 0.0) break;

        vec3 curr_ext  = get_extinction(height);
        optical_depth += (prev_ext + curr_ext) * 0.5 * dt;
        prev_ext       = curr_ext;
    }

    return exp(-optical_depth);
}

// multi-scatter lut, infinite bounce approximation (hillaire 2020)
vec3 compute_multiscatter(float height, float cos_sun_zenith)
{
    vec3 position = earth_center + vec3(0.0, earth_radius + max(height, 1.0), 0.0);
    vec3 sun_dir  = vec3(safe_sqrt(1.0 - cos_sun_zenith * cos_sun_zenith), cos_sun_zenith, 0.0);

    vec3 luminance_sum = vec3(0.0);
    vec3 f_ms_sum      = vec3(0.0);

    const int sqrt_samples = 8;
    for (int i = 0; i < sqrt_samples; i++)
    {
        for (int j = 0; j < sqrt_samples; j++)
        {
            float u = (float(i) + 0.5) / float(sqrt_samples);
            float v = (float(j) + 0.5) / float(sqrt_samples);

            float cos_theta = u * 2.0 - 1.0;
            float sin_theta = safe_sqrt(1.0 - cos_theta * cos_theta);
            float phi       = v * PI2;
            vec3 ray_dir    = vec3(sin_theta * cos(phi), cos_theta, sin_theta * sin(phi));

            vec2 atmo_hit = ray_sphere_intersect(position, ray_dir, earth_center, atmosphere_radius);
            float t_max   = atmo_hit.y;

            vec2 ground_hit  = ray_sphere_intersect(position, ray_dir, earth_center, earth_radius);
            bool hits_ground = ground_hit.x > 0.0;
            if (hits_ground) t_max = ground_hit.x;

            // cluster samples at both endpoints, including the dense ground layer
            float previous_t       = 0.0;
            vec3 trans             = vec3(1.0);
            vec3 scatter_integral  = vec3(0.0);
            vec3 transfer_integral = vec3(0.0);

            for (int k = 0; k < multiscatter_samples; k++)
            {
                float uk = float(k + 1) / float(multiscatter_samples);
                float t  = t_max * (0.5 - 0.5 * cos(PI * uk));
                float dt = t - previous_t;
                vec3 sample_pos = position + ray_dir * ((previous_t + t) * 0.5);
                previous_t = t;
                float sample_h = get_height(sample_pos);

                vec3 scatter = get_scattering(sample_h);
                vec3 extinct = get_extinction(sample_h);

                float cos_sun  = dot(normalize(sample_pos - earth_center), sun_dir);
                vec3 trans_sun = planet_transmittance(sample_h + earth_radius, cos_sun);

                vec3 scatter_no_phase = scatter * trans_sun;
                vec3 weight           = atmosphere_segment_weight(extinct, dt);
                vec3 s_int            = scatter_no_phase * weight;
                transfer_integral    += scatter * weight * trans;

                scatter_integral += s_int * trans;
                trans            *= exp(-extinct * dt);
            }

            // ground contribution
            if (hits_ground)
            {
                vec3 ground_pos = position + ray_dir * t_max;
                vec3 ground_n   = normalize(ground_pos - earth_center);
                float ndotl     = clamp(dot(ground_n, sun_dir), 0.0, 1.0);

                vec3 trans_sun = planet_transmittance(earth_radius, dot(ground_n, sun_dir));

                luminance_sum += trans * trans_sun * ndotl * ground_albedo / PI;
            }

            float phase    = 1.0 / (4.0 * PI);
            luminance_sum += scatter_integral * phase;
            // the bounce transfer contains no solar transmittance or phase term
            f_ms_sum += transfer_integral;
        }
    }

    float sphere_samples = float(sqrt_samples * sqrt_samples);
    // isotropic scattering uses the spherical mean radiance, not its integral
    luminance_sum /= sphere_samples;
    f_ms_sum      /= sphere_samples;

    // infinite series approximation
    return luminance_sum / max(vec3(1.0) - f_ms_sum, vec3(0.001));
}

// source per metre and unit top of atmosphere irradiance, shared by the infinite sky ray and finite camera to
// surface transport
vec3 atmosphere_source(vec3 position, vec3 view_dir, vec3 light_dir)
{
    float height   = max(get_height(position), 0.0);
    float cos_sun  = dot(normalize(position - earth_center), light_dir);
    vec3 trans_sun = planet_transmittance(height + earth_radius, cos_sun);
    vec3 rayleigh  = rayleigh_scatter * get_rayleigh_density(height);
    vec3 mie       = mie_scatter * get_mie_density(height);
    float mu       = dot(view_dir, light_dir);
    vec3 single_scatter = (rayleigh * rayleigh_phase(mu) + mie * cornette_shanks_phase(mu, mie_g)) * trans_sun;
    vec2 ms_uv   = vec2(cos_sun * 0.5 + 0.5, sqrt(clamp(height / (atmosphere_radius - earth_radius), 0.0, 1.0)));
    vec2 ms_size = vec2(textureSize(tex2, 0));
    ms_uv = (ms_uv * (ms_size - 1.0) + 0.5) / ms_size;
    return single_scatter + (rayleigh + mie) * textureLod(sampler2D(tex2, samplers[sampler_bilinear_clamp]), ms_uv, 0.0).rgb;
}

#endif
