// GLSL port of SpartanEngine data/shaders/spherical_harmonics.hlsl @ 4c07baa (identical at upstream 67a6e8b): l2 real
// sh (sloan / ramamoorthi ordering) for the directional diffuse ibl. sh_load_l2 reads the 9x1 coefficient texture
// tex5. Needs common_resources.glsl and common_light.glsl.
#ifndef PHALANX_SPHERICAL_HARMONICS
#define PHALANX_SPHERICAL_HARMONICS

// 0: y00, 1-3: y1-1 y10 y11, 4-8: y2-2 .. y22
// cosine lobe band factors for irradiance from radiance sh, ramamoorthi 2001
const float sh_a0 = 3.14159265;
const float sh_a1 = 2.09439510;
const float sh_a2 = 0.78539816;

void sh_eval_basis_l2(vec3 dir, out float b[9])
{
    float x = dir.x;
    float y = dir.y;
    float z = dir.z;

    b[0] = 0.28209479177387814;
    b[1] = -0.4886025119029199 * y;
    b[2] =  0.4886025119029199 * z;
    b[3] = -0.4886025119029199 * x;
    b[4] =  1.0925484305920792 * x * y;
    b[5] = -1.0925484305920792 * y * z;
    b[6] =  0.31539156525252005 * (3.0 * z * z - 1.0);
    b[7] = -1.0925484305920792 * x * z;
    b[8] =  0.5462742152960396 * (x * x - y * y);
}

// inverse of direction_sphere_uv in common_light.glsl
vec3 direction_from_sphere_uv(vec2 uv)
{
    float phi   = (uv.x - 0.5) * PI2;
    float sin_y = sin((0.5 - uv.y) * PI);
    float cos_y = sqrt(max(1.0 - sin_y * sin_y, 0.0));
    return vec3(cos(phi) * cos_y, sin_y, sin(phi) * cos_y);
}

void sh_load_l2(out vec3 L[9])
{
    for (int i = 0; i < 9; i++)
        L[i] = texelFetch(tex5, ivec2(i, 0), 0).rgb;
}

// cosine convolved irradiance, optional ao band limit for the directional gtao cone: low ao narrows the visibility
// cone so the higher sh bands fade out
vec3 sh_irradiance_l2(vec3 dir, vec3 L[9], float ao)
{
    float b[9];
    sh_eval_basis_l2(normalize(dir), b);

    float visibility = clamp(ao, 0.0, 1.0);
    float k0         = sh_a0;
    float k1         = sh_a1 * visibility;
    float k2         = sh_a2 * visibility * visibility;

    vec3 E = vec3(0.0);
    E += L[0] * (b[0] * k0);
    E += L[1] * (b[1] * k1);
    E += L[2] * (b[2] * k1);
    E += L[3] * (b[3] * k1);
    E += L[4] * (b[4] * k2);
    E += L[5] * (b[5] * k2);
    E += L[6] * (b[6] * k2);
    E += L[7] * (b[7] * k2);
    E += L[8] * (b[8] * k2);
    // band attenuation only changes directionality, the dc band survives even at zero visibility, so integrate the
    // visible fraction as well, otherwise enclosed surfaces receive the full average sky and ao cannot darken them
    return max(E, vec3(0.0)) * visibility;
}

#endif
