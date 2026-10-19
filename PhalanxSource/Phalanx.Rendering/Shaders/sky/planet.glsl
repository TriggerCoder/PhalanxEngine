// GLSL port of SpartanEngine data/shaders/sky/planet.hlsl @ 4c07baa (identical at upstream 67a6e8b): the planet
// constants and the transmittance lookup the atmosphere and the sky passes share. The transmittance lut is always
// bound as tex (common_resources.glsl), spartan passes it in as a parameter.
#ifndef PHALANX_PLANET
#define PHALANX_PLANET

const float planet_earth_radius      = 6360e3;
const float planet_atmosphere_radius = 6460e3;
const vec3  planet_earth_center      = vec3(0.0, -planet_earth_radius, 0.0);

// shared by atmosphere and clouds so their extinction lookup cannot drift apart
vec2 planet_transmittance_uv(float radius, float cos_zenith)
{
    radius = clamp(radius, planet_earth_radius, planet_atmosphere_radius);
    float h   = sqrt((radius - planet_earth_radius) / (planet_atmosphere_radius - planet_earth_radius));
    float rho = sqrt(max(0.0, (radius - planet_earth_radius) * (radius + planet_earth_radius)));
    float horizon = -rho / radius;
    // square root warp dedicates angular resolution to sunrise and sunset
    float x = cos_zenith >= horizon
        ? 0.5 + 0.5 * sqrt(clamp((cos_zenith - horizon) / (1.0 - horizon), 0.0, 1.0))
        : 0.5 - 0.5 * sqrt(clamp((horizon - cos_zenith) / (1.0 + horizon), 0.0, 1.0));
    return vec2(x, h);
}

vec3 planet_transmittance(float radius, float cos_zenith)
{
    vec2 uv = planet_transmittance_uv(radius, cos_zenith);
    if (uv.x < 0.5)
        return vec3(0.0);
    vec2 size = vec2(textureSize(tex, 0));
    // do not interpolate across the discontinuity into planet occluded texels
    uv.x = max(uv.x, 0.5 + 0.5 / size.x);
    return textureLod(sampler2D(tex, samplers[sampler_bilinear_clamp]), uv, 0.0).rgb;
}

#endif
