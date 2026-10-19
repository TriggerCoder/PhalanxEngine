using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;
using Xunit;

namespace Phalanx.Tests;

// the atmospheric sky and its image based lighting (Renderer_Passes_Sky.cs): the panorama's colours, the brdf lut against
// a reference integration, the sh projection of a constant environment, ibl energy and the bake on change
public partial class RendererSceneTests
{
    private static Light AddSun(float time_of_day)
    {
        Entity e = World.World.CreateEntity();
        Light sun = e.AddComponent<Light>()!;
        sun.SetLightType(LightType.Directional);
        sun.SetTimeOfDay(time_of_day);
        return sun;
    }

    // rgba16f mip of the skysphere, sampled at a direction with direction_sphere_uv (common.hlsl:891)
    private sealed class Panorama
    {
        private readonly byte[] m_bytes;
        public readonly uint width, height;
        public Panorama(uint mip)
        {
            RHI_Texture sky = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.skysphere)!;
            Assert.Equal(RHI_Format.R16G16B16A16_Float, sky.GetFormat());
            RHI_Device.QueueWaitAll();
            m_bytes = RHI_Device.ReadbackTexture(sky, 0, mip);
            width   = System.Math.Max(1u, sky.GetWidth() >> (int)mip);
            height  = System.Math.Max(1u, sky.GetHeight() >> (int)mip);
        }
        public Vector3 Texel(uint x, uint y)
        {
            int i = (int)(y * width + x) * 8;
            return new Vector3(Half(m_bytes, i), Half(m_bytes, i + 2), Half(m_bytes, i + 4));
        }
        public Vector3 At(Vector3 direction)
        {
            direction = Vector3.Normalize(direction);
            float u = 0.5f + MathF.Atan2(direction.Z, direction.X) / (2.0f * MathF.PI);
            float v = 0.5f - MathF.Asin(direction.Y) / MathF.PI;
            return Texel((uint)System.Math.Clamp(u * width, 0, width - 1), (uint)System.Math.Clamp(v * height, 0, height - 1));
        }
    }

    private static Vector3 HorizonAway(Vector3 sun, float elevation_deg)
    {
        Vector3 flat = Vector3.Normalize(new Vector3(-sun.X, 0, -sun.Z) + new Vector3(0.001f, 0, 0));
        float e = elevation_deg * MathF.PI / 180.0f;
        return Vector3.Normalize(flat * MathF.Cos(e) + Vector3.UnitY * MathF.Sin(e));
    }

    [SkippableFact]
    public void Time_of_day_moves_the_sun_on_its_orbit()
    {
        Run(() =>
        {
            Light sun = AddSun(0.5f);
            Entity e = sun.GetEntity()!;
            Assert.True(Vector3.Distance(new Vector3(0, -1, 0), e.GetForward()) < 1e-4f, e.GetForward().ToString()); // noon, overhead
            Assert.Equal(0.5f, World.World.GetTimeOfDay(false));
            Assert.Equal(LightPreset.custom, sun.GetPreset());
            sun.SetTimeOfDay(0.25f);
            Assert.True(MathF.Abs(e.GetForward().Y) < 1e-4f, e.GetForward().ToString()); // sunrise, on the horizon
            sun.SetTimeOfDay(0.3f);
            Assert.True(-e.GetForward().Y > 0.3f && -e.GetForward().Z < -0.9f, e.GetForward().ToString()); // morning, up towards -z
            sun.SetTimeOfDay(0.0f);
            Assert.True(Vector3.Distance(new Vector3(0, 1, 0), e.GetForward()) < 1e-4f, e.GetForward().ToString()); // midnight, below
        });
    }

    [SkippableFact]
    public void The_zenith_is_bluer_than_the_horizon_and_the_sunset_is_redder()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Light sun = AddSun(0.45f); // 72 degrees up, off the zenith so the disc isn't sampled
            Frame(scene);
            var noon = new Panorama(0);
            Vector3 sun_dir = -sun.GetEntity()!.GetForward();
            Vector3 zenith  = noon.At(new Vector3(0.01f, 1, 0));
            Vector3 horizon = noon.At(HorizonAway(sun_dir, 3.0f));
            float Blue(Vector3 c) => c.Z / c.X;
            float Red(Vector3 c) => c.X / c.Z;
            Assert.True(zenith.X > 0 && horizon.X > 0, $"{zenith} {horizon}");
            Assert.True(Blue(zenith) > Blue(horizon) * 1.2f, $"zenith {zenith}, horizon {horizon}");
            Assert.True(Blue(zenith) > 1.5f, zenith.ToString()); // rayleigh: a clear noon sky is blue

            // sunset, the sun 3.6 degrees up: the horizon under it and the sun's light turn red
            Vector3 noon_sun_color = new(sun.GetColorEffective().R, sun.GetColorEffective().G, sun.GetColorEffective().B);
            sun.SetTimeOfDay(0.74f);
            Frame(scene);
            var sunset = new Panorama(0);
            Vector3 sunset_dir = -sun.GetEntity()!.GetForward();
            Vector3 toward_sun = sunset.At(Vector3.Normalize(new Vector3(sunset_dir.X, 0, sunset_dir.Z) * 0.998f + new Vector3(0, 0.035f, 0)) +
                                           new Vector3(0.05f, 0, 0)); // beside the disc, not on it
            Assert.True(Red(toward_sun) > Red(horizon) * 2.0f, $"sunset {toward_sun}, noon horizon {horizon}");
            Assert.True(Red(toward_sun) > 1.0f, toward_sun.ToString());
            Vector3 sunset_color = new(sun.GetColorEffective().R, sun.GetColorEffective().G, sun.GetColorEffective().B);
            Assert.True(sunset_color.X / sunset_color.Z > 2.0f * noon_sun_color.X / noon_sun_color.Z, $"{sunset_color} vs {noon_sun_color}");
            // and the sky is darker than at noon
            Assert.True(sunset.At(new Vector3(0.01f, 1, 0)).Z < zenith.Z * 0.5f);
        });
    }

    // the split sum lut (light_integration.hlsl:52-101) at texel centres against a float64 integration of the same
    // integral with 2048 x 256 stratified ggx samples, and the mirror limit where A = 1 - (1 - n.v)^5, B = (1 - n.v)^5
    private static Vector2 ReferenceSplitSum(double n_dot_v, double roughness)
    {
        double gloss = 1.0 - roughness;
        double alpha = System.Math.Sqrt(2.0 / (1.0 + System.Math.Pow(2.0, 18.0 * gloss))); // D_GGX_Alpha
        double a2    = alpha * alpha;
        var v        = new Vector3((float)System.Math.Sqrt(1.0 - n_dot_v * n_dot_v), 0, (float)n_dot_v);
        const int n_theta = 2048, n_phi = 256;
        double sum_a = 0, sum_b = 0;
        for (int i = 0; i < n_theta; i++)
        {
            double xi_y      = (i + 0.5) / n_theta;
            double cos_theta = System.Math.Sqrt((1.0 - xi_y) / (1.0 + (a2 - 1.0) * xi_y));
            double sin_theta = System.Math.Sqrt(1.0 - cos_theta * cos_theta);
            for (int j = 0; j < n_phi; j++)
            {
                double phi = 2.0 * System.Math.PI * (j + 0.5) / n_phi;
                double hx = System.Math.Cos(phi) * sin_theta, hy = System.Math.Sin(phi) * sin_theta, hz = cos_theta;
                double v_dot_h = v.X * hx + v.Z * hz;
                double lz      = 2.0 * v_dot_h * hz - v.Z;
                if (lz <= 0.0)
                    continue;
                double n_dot_l = lz, n_dot_h = hz;
                double d   = n_dot_h * n_dot_h * (a2 - 1.0) + 1.0;
                double D   = a2 / (System.Math.PI * d * d);
                double ggxv = n_dot_l * System.Math.Sqrt(n_dot_v * n_dot_v * (1.0 - a2) + a2);
                double ggxl = n_dot_v * System.Math.Sqrt(n_dot_l * n_dot_l * (1.0 - a2) + a2);
                double V   = 0.5 / (ggxv + ggxl);
                double pdf = D * n_dot_h / (4.0 * v_dot_h);
                double w   = D * V * n_dot_l / pdf;
                double fc  = System.Math.Pow(1.0 - System.Math.Clamp(v_dot_h, 0.0, 1.0), 5.0);
                sum_a += w * (1.0 - fc);
                sum_b += w * fc;
            }
        }
        double n = (double)n_theta * n_phi;
        return new Vector2((float)(sum_a / n), (float)(sum_b / n));
    }

    [SkippableFact]
    public void The_brdf_lut_matches_a_reference_integration()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Frame(scene);
            RHI_Texture lut = Renderer.GetRenderTargetTexture(Renderer_RenderTarget.lut_brdf_specular)!;
            Assert.Equal(RHI_Format.R16G16_Float, lut.GetFormat());
            Assert.Equal(512u, lut.GetWidth());
            byte[] bytes = RHI_Device.ReadbackTexture(lut, 0, 0);
            Vector2 Texel(uint x, uint y) { int i = (int)(y * 512 + x) * 4; return new Vector2(Half(bytes, i), Half(bytes, i + 2)); }

            foreach (uint x in new uint[] { 63, 255, 460 })
            {
                double n_dot_v = (x + 0.5) / 512.0;
                foreach (uint y in new uint[] { 150, 300, 460, 511 })
                {
                    double roughness = (y + 0.5) / 512.0;
                    Vector2 expected = ReferenceSplitSum(n_dot_v, roughness);
                    Vector2 actual   = Texel(x, y);
                    // spartan's 1024 hammersley samples (rotated by the tangent frame of importance_sample_ggx) are
                    // off by up to 2% in A at low n.v, the reference is converged
                    Assert.True(MathF.Abs(expected.X - actual.X) < 0.02f && MathF.Abs(expected.Y - actual.Y) < 0.006f,
                                $"n.v {n_dot_v:0.###} roughness {roughness:0.###}: expected {expected}, got {actual}");
                }

                // mirror limit, the first row is roughness 0.001 (ggx alpha 0.0028)
                float fc = MathF.Pow(1.0f - (float)n_dot_v, 5.0f);
                Vector2 mirror = Texel(x, 0);
                Assert.True(MathF.Abs(mirror.X - (1.0f - fc)) < 0.01f && MathF.Abs(mirror.Y - fc) < 0.005f, $"n.v {n_dot_v:0.###}: mirror {mirror}");
            }
            // energy: A + B never exceeds one and falls with roughness at grazing angles
            Assert.True(Texel(20, 500).X + Texel(20, 500).Y < Texel(20, 100).X + Texel(20, 100).Y);
            for (uint y = 0; y < 512; y += 31)
                for (uint x = 0; x < 512; x += 31)
                    Assert.True(Texel(x, y).X + Texel(x, y).Y <= 1.01f, $"{x}, {y}: {Texel(x, y)}");
        });
    }

    // skysphere_sh_project.comp on a constant panorama: the dc band holds the constant times sqrt(4 pi), the others are
    // zero, and the cosine convolved irradiance (sh_irradiance_l2, spherical_harmonics.hlsl) divided by pi, what
    // light_image_based uses for a lambertian surface, is the constant for every normal
    [SkippableFact]
    public void The_irradiance_of_a_constant_environment_is_the_constant()
    {
        Run(() =>
        {
            var constant = new Vector4(0.3f, 0.7f, 1.9f, 1.0f);
            const uint w = 128, h = 64;
            var pixels = new float[w * h * 4];
            for (int i = 0; i < w * h; i++) { pixels[i * 4] = constant.X; pixels[i * 4 + 1] = constant.Y; pixels[i * 4 + 2] = constant.Z; pixels[i * 4 + 3] = 1; }
            var slice = new RHI_Texture_Slice();
            slice.mips.Add(new RHI_Texture_Mip { bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray() });
            using var environment = new RHI_Texture(RHI_Texture_Type.Type2D, w, h, 1, 1, RHI_Format.R32G32B32A32_Float, RHI_Texture_Flags.RHI_Texture_Srv, "constant_environment", new List<RHI_Texture_Slice> { slice });
            using var sh = new RHI_Texture(RHI_Texture_Type.Type2D, 9, 1, 1, 1, RHI_Format.R32G32B32A32_Float, RHI_Texture_Flags.RHI_Texture_Uav | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "sh");
            using var pipeline = new RHI_Pipeline(Renderer.GetShader(Renderer_Shader.skysphere_sh_project_c)!, "sh_project");

            RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            cmd.SetPipeline(pipeline);
            cmd.SetTexture(7, environment);             // tex
            cmd.SetTexture(0, sh, storage: true);       // tex_uav
            cmd.Dispatch(1);
            cmd.InsertBarrierMemory();
            RHI_CommandList.ImmediateExecutionEnd(cmd);

            float[] c = MemoryMarshal.Cast<byte, float>(RHI_Device.ReadbackTexture(sh, 0, 0)).ToArray();
            Vector3 L(int i) => new(c[i * 4], c[i * 4 + 1], c[i * 4 + 2]);
            Vector3 rgb = new(constant.X, constant.Y, constant.Z);
            NearRel(rgb * MathF.Sqrt(4.0f * MathF.PI), L(0), 0.002f);
            for (int i = 1; i < 9; i++)
                Assert.True(L(i).Length() < 4e-3f * rgb.Length(), $"band {i}: {L(i)}"); // the 64 x 32 quadrature near the poles

            // sh_irradiance_l2 with full visibility: band factors pi, 2pi/3, pi/4
            foreach (Vector3 n in new[] { Vector3.UnitY, -Vector3.UnitY, Vector3.Normalize(new Vector3(1, 0.3f, -0.7f)) })
            {
                float x = n.X, y = n.Y, z = n.Z;
                float[] b = { 0.28209479f, -0.48860251f * y, 0.48860251f * z, -0.48860251f * x, 1.09254843f * x * y, -1.09254843f * y * z,
                              0.31539157f * (3 * z * z - 1), -1.09254843f * x * z, 0.54627422f * (x * x - y * y) };
                float[] k = { MathF.PI, 2.0943951f, 2.0943951f, 2.0943951f, 0.78539816f, 0.78539816f, 0.78539816f, 0.78539816f, 0.78539816f };
                Vector3 E = Vector3.Zero;
                for (int i = 0; i < 9; i++)
                    E += L(i) * (b[i] * k[i]);
                NearRel(rgb, E / MathF.PI, 0.005f);
            }
        });
    }

    // a sunlit floor seen from above: image based lighting adds the sky's diffuse and specular, more than nothing and
    // no more than a white surface under the brightest sky radiance could reflect
    [SkippableFact]
    public void Image_based_lighting_adds_bounded_sky_energy()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
            scene.material.SetProperty(MaterialProperty.Roughness, 1.0f);
            scene.material.SetProperty(MaterialProperty.Metalness, 0.0f);
            scene.cube.SetScale(new Vector3(20, 0.1f, 20));
            Entity camera = scene.camera.GetEntity()!;
            camera.SetPosition(new Vector3(0, 3, 0));
            camera.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2)); // looking straight down
            AddSun(0.4f);

            Renderer.ImageBasedLighting = false;
            Frame(scene);
            Vector4 off = Rgba16(Read(Renderer_RenderTarget.frame_render), Width / 2, Height / 2);
            Renderer.ImageBasedLighting = true;
            Frame(scene);
            Vector4 on = Rgba16(Read(Renderer_RenderTarget.frame_render), Width / 2, Height / 2);
            Assert.Equal(1.0f, on.W);
            var added = new Vector3(on.X - off.X, on.Y - off.Y, on.Z - off.Z);

            // the upper hemisphere of a coarse mip (the sun disc averaged away) bounds the sky radiance
            var mip = new Panorama(5);
            Vector3 max = Vector3.Zero, min = new(float.MaxValue);
            for (uint y = 0; y < mip.height / 2; y++)
                for (uint x = 0; x < mip.width; x++)
                {
                    max = Vector3.Max(max, mip.Texel(x, y));
                    min = Vector3.Min(min, mip.Texel(x, y));
                }
            Assert.True(added.X > 0.5f * 0.5f * min.X && added.Z > 0.5f * 0.5f * min.Z, $"added {added}, sky min {min}");
            Assert.True(added.X < max.X && added.Y < max.Y && added.Z < max.Z, $"added {added}, sky max {max}");
            // the sky is blue, so is what it adds to a grey floor, and the sun still dominates
            Assert.True(added.Z > added.X, added.ToString());
            Assert.True(off.X > added.X, $"direct {off}, ibl {added}");
        });
    }

    [SkippableFact]
    public void The_sky_bakes_when_it_changes_and_the_sun_takes_slot_0()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            // a point light first in the world, the sun after it: the sun still gets slot 0 (Renderer.cpp:3334-3352)
            Entity point_entity = World.World.CreateEntity();
            Light point = point_entity.AddComponent<Light>()!;
            point.SetLightType(LightType.Point);
            Light sun = AddSun(0.4f);
            Frame(scene);
            Assert.Equal(0u, sun.GetIndex());
            Assert.Equal(1u, point.GetIndex());
            ulong first = Renderer.GetSkyBakeCount();
            Assert.True(first >= 1);

            Frame(scene);
            Assert.Equal(first, Renderer.GetSkyBakeCount()); // nothing changed

            sun.SetTimeOfDay(0.4f + 0.05f / 360.0f); // 0.05 degrees
            Frame(scene);
            Assert.Equal(first, Renderer.GetSkyBakeCount());
            sun.SetTimeOfDay(0.4f + 0.2f / 360.0f);  // 0.2 degrees from the bake, the small moves add up
            Frame(scene);
            Assert.Equal(first + 1, Renderer.GetSkyBakeCount());

            sun.SetIntensity(sun.GetIntensityPhotometric() * 0.5f);
            Frame(scene);
            Assert.Equal(first + 2, Renderer.GetSkyBakeCount());

            // only local lights: slot 0 is the point light, which the sky must not take for the sun
            World.World.RemoveEntity(sun.GetEntity()!);
            Frame(scene);
            Assert.Equal(0u, point.GetIndex());
            Assert.Equal(first + 3, Renderer.GetSkyBakeCount());
            Vector3 zenith = new Panorama(0).At(new Vector3(0.01f, 1, 0));
            Assert.True(zenith.Z < 0.01f, zenith.ToString()); // the night sky, not a point light sun
        });
    }
}
