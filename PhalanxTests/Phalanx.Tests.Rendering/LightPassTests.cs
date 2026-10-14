using System.Numerics;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// Pass_Light read back on lavapipe against a c# port of evaluate_light (light.hlsl / brdf.hlsl), and the tonemapper
public partial class RendererSceneTests
{
    private static Vector4 Rgba16(byte[] b, uint x, uint y)
    {
        int i = Index(x, y) * 8;
        return new Vector4(Half(b, i), Half(b, i + 2), Half(b, i + 4), Half(b, i + 6));
    }

    private static Vector4 Rgba8(byte[] b, uint x, uint y)
    {
        int i = Index(x, y) * 4;
        return new Vector4(b[i] / 255f, b[i + 1] / 255f, b[i + 2] / 255f, b[i + 3] / 255f);
    }

    // r11g11b10 unsigned floats: 6/6/5 mantissa bits, 5 exponent bits, bias 15
    private static float UFloat(uint bits, int mantissa_bits)
    {
        uint m = bits & ((1u << mantissa_bits) - 1), e = bits >> mantissa_bits;
        if (e == 0) return m / (float)(1 << mantissa_bits) * System.MathF.Pow(2, -14);
        if (e == 31) return m == 0 ? float.PositiveInfinity : float.NaN;
        return (1 + m / (float)(1 << mantissa_bits)) * System.MathF.Pow(2, (int)e - 15);
    }

    private static Vector3 LightTarget(Renderer_RenderTarget target, uint x, uint y)
    {
        byte[] b = Read(target);
        Assert.Equal(RHI_Format.R11G11B10_Float, Renderer.GetRenderTargetTexture(target)!.GetFormat());
        uint v = BitConverter.ToUInt32(b, Index(x, y) * 4);
        return new Vector3(UFloat(v & 0x7FF, 6), UFloat((v >> 11) & 0x7FF, 6), UFloat(v >> 22, 5));
    }

    private static void NearRel(Vector3 expected, Vector3 actual, float tolerance)
    {
        for (int c = 0; c < 3; c++)
        {
            float e = expected[c], a = actual[c];
            Assert.True(System.MathF.Abs(e - a) <= tolerance * System.MathF.Max(System.MathF.Abs(e), 1e-4f), $"channel {c}: expected {expected}, got {actual}");
        }
    }

    // the c# reference: light_build, angular_info_build, Diffuse_Burley, BRDF_Specular_Isotropic (energy only), microshadow
    private static float Pow5(float x) => x * x * x * x * x;
    private static float Schlick(float f0, float f90, float v_dot_h) => f0 + (f90 - f0) * Pow5(1 - v_dot_h);

    private struct ReferenceSurface { public Vector3 position, normal, view, albedo_sampled; public float roughness, metallic, occlusion; }

    private static ReferenceSurface SurfaceAt(uint x, uint y)
    {
        ref readonly FrameBufferData frame = ref Renderer.GetFrameConstantBuffer();
        Vector4 color = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), x, y);
        Vector4 normal = Rgba16(Read(Renderer_RenderTarget.gbuffer_normal), x, y);
        Vector4 material = Rgba8(Read(Renderer_RenderTarget.gbuffer_material), x, y);
        float depth = BitConverter.ToSingle(Read(Renderer_RenderTarget.gbuffer_depth), Index(x, y) * 4);
        float u = (x + 0.5f) / Width, v = (y + 0.5f) / Height;
        Matrix4x4.Invert(frame.view_projection, out Matrix4x4 inverse);
        Vector4 w = Vector4.Transform(new Vector4(u * 2 - 1, (1 - v) * 2 - 1, depth, 1), inverse);
        var s = new ReferenceSurface
        {
            position = new Vector3(w.X, w.Y, w.Z) / w.W,
            normal = new Vector3(normal.X, normal.Y, normal.Z),
            albedo_sampled = new Vector3(color.X, color.Y, color.Z),
            roughness = material.X,
            metallic = material.Y,
            occlusion = color.W >= 0.999f ? material.W : 1.0f,
        };
        s.view = -Vector3.Normalize(s.position - frame.camera_position);
        return s;
    }

    // radiometric diffuse (albedo one) of one light
    private static Vector3 ReferenceDiffuse(ReferenceSurface s, Light light)
    {
        Entity e = light.GetEntity()!;
        Vector3 to_pixel; float attenuation = 1;
        if (light.GetLightType() == LightType.Directional)
            to_pixel = Vector3.Normalize(e.GetForward());
        else
        {
            float d = Vector3.Distance(s.position, e.GetPosition());
            to_pixel = Vector3.Normalize(s.position - e.GetPosition());
            float r2 = (d / light.GetRange()) * (d / light.GetRange());
            float window = System.Math.Clamp(1 - r2 * r2, 0, 1);
            attenuation = window * window / (d * d + 0.0001f);
            if (light.GetLightType() == LightType.Spot)
            {
                float angle = Lighting.SpotHalfAngle(light.GetAngle());
                float cos_outer = System.MathF.Cos(angle), cos_inner = System.MathF.Cos(angle * 0.9f);
                float a = System.Math.Clamp((Vector3.Dot(to_pixel, e.GetForward()) - cos_outer) / System.MathF.Max(1e-6f, cos_inner - cos_outer), 0, 1);
                attenuation *= a * a;
            }
        }
        Vector3 l = -to_pixel, h = Vector3.Normalize(l + s.view);
        float n_dot_l = System.Math.Clamp(Vector3.Dot(s.normal, l), 0, 1);
        float n_dot_v = System.Math.Clamp(Vector3.Dot(s.normal, s.view), 0, 1);
        float v_dot_h = System.Math.Clamp(Vector3.Dot(s.view, h), 0, 1);
        Phalanx.Math.Color c = light.GetColorEffective();
        Vector3 radiance = new Vector3(c.R, c.G, c.B) * light.GetIntensityRadiometric() * attenuation * n_dot_l;

        float f90 = 0.5f + 2 * v_dot_h * v_dot_h * s.roughness;
        float burley = Schlick(1, f90, n_dot_l) * Schlick(1, f90, n_dot_v) / System.MathF.PI;
        Vector3 f0 = Vector3.Lerp(new Vector3(0.04f), s.albedo_sampled, s.metallic);
        float f90s = System.Math.Clamp(50 * (f0.X + f0.Y + f0.Z) * 0.33333f, 0, 1);
        Vector3 F = f0 + (new Vector3(f90s) - f0) * Pow5(1 - v_dot_h);
        Vector3 energy = (Vector3.One - F) * (1 - s.metallic);
        float micro = System.Math.Clamp(n_dot_l + 2 * s.occlusion - 1, 0, 1);
        return burley * radiance * energy * micro * micro;
    }

    private static Light AddLight(LightType type, Vector3 position, Quaternion rotation, float intensity)
    {
        Entity e = World.World.CreateEntity();
        e.SetPosition(position);
        e.SetRotation(rotation);
        Light light = e.AddComponent<Light>()!;
        light.SetLightType(type);
        light.SetColor(new Phalanx.Math.Color(1, 1, 1, 1));
        light.SetIntensity(intensity);
        return light;
    }

    private static readonly uint Cx = Width / 2, Cy = Height / 2;

    [SkippableFact]
    public void A_directional_light_falls_off_with_lambert_and_burley()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            // +z into the front face (normal -z), then tilted about y
            Light sun = AddLight(LightType.Directional, new Vector3(0, 5, 0), Quaternion.Identity, 683.0f);
            float previous = float.MaxValue;
            foreach (float degrees in new[] { 0f, 30f, 60f, 80f })
            {
                sun.GetEntity()!.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * System.MathF.PI / 180));
                Frame(scene);
                Vector3 measured = LightTarget(Renderer_RenderTarget.light_diffuse, Cx, Cy);
                Vector3 expected = ReferenceDiffuse(SurfaceAt(Cx, Cy), sun);
                NearRel(expected, measured, 0.04f);
                Assert.True(measured.Y < previous, $"{degrees}: {measured.Y} !< {previous}");
                previous = measured.Y;
            }
            // from behind, nothing
            sun.GetEntity()!.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, System.MathF.PI));
            Frame(scene);
            Assert.Equal(Vector3.Zero, LightTarget(Renderer_RenderTarget.light_diffuse, Cx, Cy));
            Assert.Equal(Vector3.Zero, LightTarget(Renderer_RenderTarget.light_specular, Cx, Cy));
        });
    }

    [SkippableFact]
    public void A_point_light_falls_off_with_the_inverse_square_and_the_range_window()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Light point = AddLight(LightType.Point, new Vector3(0, 0, -1.5f), Quaternion.Identity, 100000.0f);
            point.SetRange(10.0f);
            Vector3 at_1 = default;
            foreach (float distance in new[] { 1.0f, 2.0f, 9.0f })
            {
                point.GetEntity()!.SetPosition(new Vector3(0, 0, -0.5f - distance));
                Frame(scene);
                Vector3 measured = LightTarget(Renderer_RenderTarget.light_diffuse, Cx, Cy);
                NearRel(ReferenceDiffuse(SurfaceAt(Cx, Cy), point), measured, 0.04f);
                if (distance == 1.0f) at_1 = measured;
                if (distance == 2.0f) // a quarter, times the window ratio
                {
                    float w(float d) { float r = d / 10; float x = 1 - r * r * r * r; return x * x; }
                    Assert.InRange(measured.Y / at_1.Y, 0.25f * w(2) / w(1) * 0.94f, 0.25f * w(2) / w(1) * 1.06f);
                }
            }
            // beyond the range, nothing
            point.GetEntity()!.SetPosition(new Vector3(0, 0, -11.0f));
            Frame(scene);
            Assert.Equal(Vector3.Zero, LightTarget(Renderer_RenderTarget.light_diffuse, Cx, Cy));
        });
    }

    [SkippableFact]
    public void A_spot_light_lights_inside_its_cone_fades_in_the_penumbra_and_not_outside()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            float angle = 20.0f * System.MathF.PI / 180;
            Light spot = AddLight(LightType.Spot, new Vector3(0, 0, -2.5f), Quaternion.Identity, 100000.0f);
            spot.SetRange(10.0f);
            spot.SetAngle(angle);
            Vector3 Measure(float yaw_degrees)
            {
                spot.GetEntity()!.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw_degrees * System.MathF.PI / 180));
                Frame(scene);
                Vector3 m = LightTarget(Renderer_RenderTarget.light_diffuse, Cx, Cy);
                NearRel(ReferenceDiffuse(SurfaceAt(Cx, Cy), spot), m, 0.04f);
                return m;
            }
            Vector3 inside = Measure(0), inner = Measure(17), penumbra = Measure(19), outside = Measure(21);
            Assert.True(inside.Y > 0 && inner.Y > 0);
            Assert.True(penumbra.Y > 0 && penumbra.Y < inner.Y * 0.8f, $"{penumbra} vs {inner}");
            Assert.Equal(Vector3.Zero, outside);
        });
    }

    [SkippableFact]
    public void The_tonemapper_is_monotonic_bounded_and_the_exposure_is_the_cameras()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            // iso 100, shutter 1 s, aperture sqrt(569.17): ev100 = log2(569.17), 683 * exposure = 1
            scene.camera.SetIso(100.0f);
            scene.camera.SetShutterSpeed(1.0f);
            scene.camera.SetAperture(System.MathF.Sqrt(683.0f / 1.2f));
            Frame(scene);
            Assert.Equal(1.0f / 683.0f, Renderer.GetFrameConstantBuffer().camera_exposure, 6);

            foreach (Renderer_Tonemapping tonemapper in new[] { Renderer_Tonemapping.GranTurismo7, Renderer_Tonemapping.Reinhard, Renderer_Tonemapping.Max })
            {
                Renderer.Tonemapping = tonemapper;
                float previous = -1;
                foreach (float nits in new[] { 0.0f, 0.01f, 0.05f, 0.1f, 0.2f, 0.4f, 0.8f, 1.6f, 4f, 16f, 100f, 1000f })
                {
                    // the sky is nits / 683, exposure brings it back to nits
                    Renderer.SkyFallbackNits = new Vector3(nits * 683.0f);
                    Frame(scene);
                    Vector4 p = Rgba16(Read(Renderer_RenderTarget.frame_output), 0, 0);
                    Assert.Equal(1.0f, p.W);
                    Assert.InRange(p.X, 0.0f, 1.001f);
                    Assert.True(p.X >= previous - 1e-3f, $"{tonemapper} at {nits}: {p.X} < {previous}");
                    if (tonemapper == Renderer_Tonemapping.Max)
                    {
                        float c = System.MathF.Min(nits, 1.0f);
                        float srgb = c < 0.0031306684f ? c * 12.92f : 1.055f * System.MathF.Pow(c, 1 / 2.4f) - 0.055f;
                        Assert.Equal(srgb, p.X, 2);
                    }
                    previous = p.X;
                }
            }
        });
    }
}
