// Port of SpartanEngine data/shaders/shared_lighting.h @ 4c07baa, the lighting constants and cone helpers the cpu
// (Light) and the shaders (common_light.glsl) share. Scene RGB is linear Rec.709 photometric light divided by 683
// for storage, a consistent RGB scale, not a spectral conversion to electrical watts.
namespace Phalanx.World;

public static class Lighting
{
    public const float LuminousEfficacy           = 683.0f;
    public const float EmissiveNitsFromAlbedo     = 100000.0f;
    public const float EmissiveNitsTexture        = 10000.0f;

    // keep the cone nonzero and its shadow projection below a 180 degree fov
    public static float SpotHalfAngle(float angle) => System.Math.Clamp(angle, 0.01f, 1.57079632679f - 0.001f);

    // integral of the shader's squared linear falloff in cos(theta): the inner cone has weight one, the penumbra 1/3
    public static float SpotSolidAngle(float angle)
    {
        angle = SpotHalfAngle(angle);
        float cos_outer = MathF.Cos(angle);
        float cos_inner = MathF.Cos(angle * 0.9f);
        return 6.28318530718f * ((1.0f - cos_inner) + (cos_inner - cos_outer) / 3.0f);
    }

    public static float SpotAttenuation(float cos_theta, float angle)
    {
        angle = SpotHalfAngle(angle);
        float cos_outer = MathF.Cos(angle);
        float cos_inner = MathF.Cos(angle * 0.9f);
        float falloff   = System.Math.Clamp((cos_theta - cos_outer) / MathF.Max(cos_inner - cos_outer, 0.000001f), 0.0f, 1.0f);
        return falloff * falloff;
    }
}
