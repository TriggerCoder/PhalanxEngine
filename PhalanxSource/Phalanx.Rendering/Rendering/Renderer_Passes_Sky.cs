// Port of SpartanEngine source/rendering/Renderer_Passes_Sky.cpp @ 4c07baa (Pass_Skysphere :25-155,
// Pass_Skysphere_SH_Project :157-175, Pass_Lut_BrdfSpecular :389-400, Pass_Lut_AtmosphericScattering :402-440), the
// sky resources of Renderer_Resources.cpp:896-922, the convergence state of Renderer.cpp:5136-5199 and the image based
// lighting pass of Renderer_Passes_Lighting.cpp:1652-1690.
// Deviations:
// - The skysphere panorama is R16G16B16A16_Float (SkysphereFormat), spartan creates it as R11G11B10_Float
//   (Renderer_Resources.cpp:911; upstream 67a6e8b :974). The composition draws the sky straight from it, and R11G11B10
//   keeps 6 mantissa bits for red and green and 5 for blue, steps of 1.6% and 3%: a clear sky is a smooth gradient over
//   most of the screen, those steps show as bands after the tonemapper, and taau turns static steps into crawling
//   contours the same way it did with the r11g11b10 light buffers (Renderer_Resources.cs header). Phalanx has no
//   dithering to hide them (spartan's r.dithering defaults to off, Renderer_ConsoleVariables.cpp:185). Cost: 85 MB
//   with the mip chain instead of 43 MB.
// - Bake on change: spartan re-renders the sky every frame (a 1/16 bayer partial panorama, the sky view lut every 8th
//   frame, an averaging downscale and the sh every 4th) and runs eight full warmup frames with the ggx prefilter after
//   a discontinuity. That rolling update exists for the clouds and stars, which change every frame; without them the
//   sky only changes with the sun or the camera's altitude, so Phalanx bakes the whole chain (sky view lut, panorama,
//   averaging mip chain, ggx prefilter, sh) in one frame when the sky changed since the last bake and does nothing on
//   the other frames. A change is spartan's set (Renderer.cpp:5145-5159, the parts that exist): a different sun light,
//   its direction moved by more than SkyBakeAngleThreshold degrees (spartan restarts its warmup at a quaternion dot
//   below 0.99999, about 0.5 degrees, and rolls in smaller moves), its intensity by more than 1% (spartan: 5%) or the
//   camera by more than 500 m (spartan's teleport distance), plus the camera altitude by more than 50 m (spartan's
//   sky view lut follows the altitude every 8 frames). The distances are against the last bake, not the last frame, so
//   slow motion still triggers a bake once it adds up. A sun that moves every frame (a time of day animation) therefore
//   bakes every frame or two, which costs what spartan's warmup frames cost.
// - This also avoids a spartan bug: spartan runs the ggx prefilter only in warmup frames (Renderer_Passes_Sky.cpp:117)
//   while the averaging Pass_Downscale runs on every fourth frame (:110-115) and overwrites mips 1+ with plain box
//   averages, so within three frames of the warmup the specular ibl reads box filtered mips instead of the ggx lobes.
// - The ggx prefilter binds the whole skysphere mip chain as its source (spartan: SetTexture(tex, skysphere, 0,
//   mip_level), :129). On vulkan a mip specific binding resolves to the single mip view of its first mip
//   (Vulkan_DescriptorSet.cpp:199), so spartan's prefilter only ever reads mip 0 and its pdf based mip selection
//   (light_integration.hlsl:143-152) does nothing: mip m averages 512 / m bilinear taps of the 4096 wide panorama, 56
//   at the roughest mip, which undersamples the sun disc into speckles. With the whole chain bound the selection works; the shader clamps it below the mip being written, so that
//   mip is never read while it is written.
// - The atmosphere luts are produced on the first bake (spartan: the first frame that updates the sky), the brdf lut on
//   the first frame (spartan also rebuilds it when its shader hash changes, shaders don't hot reload here).
// - The skysphere has spartan's 10 mips (compute_mip_count(4096, 2048, 16), Renderer_Resources.cpp:716-726, ends at
//   8x4 although the comment at :909 says the lowest mip is 16x16).
// - No clouds (Pass_Clouds_Environment, cloud shadow), stars or moon; see sky/skysphere.comp.
// - light_image_based runs once, opaque (no transparent pass yet), without restir, ssao or ray traced reflections, see
//   light_image_based.comp.
using System.Numerics;
using Phalanx.Rendering.RHI;
using static Phalanx.Rendering.Renderer_Definitions;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using RenderView   = Phalanx.World.RenderView;
using Texture      = Phalanx.Rendering.RHI.RHI_Texture;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    /// <summary>The skysphere panorama format, R16G16B16A16_Float (spartan: R11G11B10_Float), see Renderer_Passes_Sky.cs.</summary>
    internal const RHI_Format SkysphereFormat = RHI_Format.R16G16B16A16_Float;

    /// <summary>How far, in degrees, the sun moves from where the sky was last baked before it is baked again.</summary>
    internal const float SkyBakeAngleThreshold = 0.1f;

    private const float sky_bake_intensity_threshold = 0.01f;  // relative
    private const float sky_bake_teleport_distance   = 500.0f; // m, Renderer.cpp:5147 (250000 squared)
    private const float sky_bake_altitude_threshold  = 50.0f;  // m

    private struct SkyState
    {
        public bool    brdf_lut_produced;
        public bool    atmosphere_lut_produced;
        public bool    baked;
        public bool    sun_present;
        public Vector3 sun_direction;
        public float   sun_intensity;
        public Vector3 camera_position;
        public ulong   bake_count;
    }
    private static SkyState s_sky;

    private static void ResetSkyState() => s_sky = default;

    /// <summary>How many times the sky and its image based lighting were baked since the renderer started.</summary>
    public static ulong GetSkyBakeCount() => s_sky.bake_count;

    /// <summary>Spartan has no switch for it; the sky image based lighting can be turned off to compare against
    /// direct light only (the sky still draws).</summary>
    public static bool ImageBasedLighting { get; set; } = true;

    // Renderer_Resources.cpp:896-922, the sky and ibl targets, independent of the resolution: created once. All of them
    // can be copied (spartan: only the skysphere and the sh, RHI_Texture_ClearBlit), the tests read the luts back.
    private static void CreateSkyRenderTargets()
    {
        void Create(RenderTarget type, uint width, uint height, uint mips, RHI_Format format, RHI_Texture_Flags extra = 0)
        {
            if (s_render_targets[(int)type] != null)
                return;
            s_render_targets[(int)type] = new Texture(RHI_Texture_Type.Type2D, width, height, 1, mips, format,
                RHI_Texture_Flags.RHI_Texture_Uav | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit | extra, type.ToString());
            RenderView.SetRenderTargetSize(type, width, height);
        }

        Create(RenderTarget.lut_brdf_specular,            renderer_resolution_brdf_lut, renderer_resolution_brdf_lut, 1, RHI_Format.R16G16_Float);
        Create(RenderTarget.lut_atmosphere_transmittance, 256, 64,  1, RHI_Format.R16G16B16A16_Float);
        Create(RenderTarget.lut_atmosphere_multiscatter,  32,  32,  1, RHI_Format.R16G16B16A16_Float);
        // two vertically packed 192x108 halves, sun sky on top rows, moonlit sky below
        Create(RenderTarget.lut_sky_view,                 192, 216, 1, RHI_Format.R16G16B16A16_Float);
        Create(RenderTarget.skysphere, renderer_resolution_skysphere_w, renderer_resolution_skysphere_h,
               ComputeMipCount(renderer_resolution_skysphere_w, renderer_resolution_skysphere_h, 16), SkysphereFormat,
               RHI_Texture_Flags.RHI_Texture_PerMipViews);
        // l2 sh coefficients for the directional diffuse ibl, 9 float4s in a 9x1 texture
        Create(RenderTarget.sky_sh, 9, 1, 1, RHI_Format.R32G32B32A32_Float);
    }

    // Renderer_Resources.cpp:716-726
    internal static uint ComputeMipCount(uint width, uint height, uint smallest_dimension)
    {
        uint max_dimension = System.Math.Max(width, height);
        uint mip_count     = 1;
        while (max_dimension >= smallest_dimension)
        {
            max_dimension /= 2;
            mip_count++;
        }
        return mip_count;
    }

    private static void DispatchFor(RHI_CommandList cmd, Texture texture, uint mip = 0)
    {
        uint w = System.Math.Max(1u, texture.GetWidth() >> (int)mip);
        uint h = System.Math.Max(1u, texture.GetHeight() >> (int)mip);
        cmd.Dispatch((w + 7) / 8, (h + 7) / 8);
    }

    private static void SetComputePipeline(RHI_CommandList cmd, Renderer_Shader shader, string name)
    {
        var pso = new RHI_PipelineState { name = name };
        pso.SetShader(RHI_Shader_Type.Compute, GetShader(shader)!);
        cmd.SetPipelineState(pso);
        SetStandardResources(cmd);
    }

    // Renderer.cpp:5136-5199, reduced to the state that exists (see the header): true when the sky must be baked
    private static bool UpdateSkysphereConvergenceState()
    {
        ref readonly LightParameters sun = ref GetLightParametersSlot0();
        bool sun_present    = (sun.flags & 1u) != 0;
        Vector3 direction   = sun_present ? Vector3.Normalize(sun.direction) : new Vector3(0.0f, -1.0f, 0.0f);
        float intensity     = sun_present ? sun.intensity : 0.0f;
        Vector3 camera      = s_cb_frame.camera_position;

        bool changed = !s_sky.baked || sun_present != s_sky.sun_present;
        if (!changed)
        {
            double cos   = System.Math.Clamp(Vector3.Dot(direction, s_sky.sun_direction), -1.0f, 1.0f);
            double angle = System.Math.Atan2(Vector3.Cross(direction, s_sky.sun_direction).Length(), cos) * (180.0 / System.Math.PI);
            changed = angle > SkyBakeAngleThreshold ||
                      MathF.Abs(intensity - s_sky.sun_intensity) > MathF.Abs(s_sky.sun_intensity) * sky_bake_intensity_threshold ||
                      Vector3.Distance(camera, s_sky.camera_position) > sky_bake_teleport_distance ||
                      MathF.Abs(camera.Y - s_sky.camera_position.Y) > sky_bake_altitude_threshold;
        }
        if (changed)
        {
            s_sky.baked           = true;
            s_sky.sun_present     = sun_present;
            s_sky.sun_direction   = direction;
            s_sky.sun_intensity   = intensity;
            s_sky.camera_position = camera;
        }
        return changed;
    }

    // Renderer.cpp:5364-5388, the brdf lut once and the sky when it changed
    private static void Pass_ComputeSky(RHI_CommandList cmd)
    {
        if (!s_sky.brdf_lut_produced)
        {
            Pass_Lut_BrdfSpecular(cmd);
            s_sky.brdf_lut_produced = true;
        }

        if (UpdateSkysphereConvergenceState())
        {
            if (!s_sky.atmosphere_lut_produced)
            {
                Pass_Lut_AtmosphericScattering(cmd);
                s_sky.atmosphere_lut_produced = true;
            }
            Pass_Skysphere(cmd);
            s_sky.bake_count++;
        }
    }

    private static void Pass_Lut_BrdfSpecular(RHI_CommandList cmd)
    {
        Texture lut = GetRenderTargetTexture(RenderTarget.lut_brdf_specular)!;
        cmd.BeginMarker("lut_brdf_specular");
        {
            SetComputePipeline(cmd, Renderer_Shader.light_integration_brdf_specular_lut_c, "lut_brdf_specular");
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, lut, storage: true);
            DispatchFor(cmd, lut);
            cmd.InsertBarrierMemory(); // the rhi has no hazard tracking between dispatches yet
        }
        cmd.EndMarker();
    }

    private static void Pass_Lut_AtmosphericScattering(RHI_CommandList cmd)
    {
        Texture transmittance = GetRenderTargetTexture(RenderTarget.lut_atmosphere_transmittance)!;
        Texture multiscatter  = GetRenderTargetTexture(RenderTarget.lut_atmosphere_multiscatter)!;
        cmd.BeginMarker("lut_atmospheric_scattering");
        {
            SetComputePipeline(cmd, Renderer_Shader.skysphere_transmittance_lut_c, "lut_atmosphere_transmittance");
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, transmittance, storage: true);
            DispatchFor(cmd, transmittance);
            cmd.InsertBarrierMemory();

            SetComputePipeline(cmd, Renderer_Shader.skysphere_multiscatter_lut_c, "lut_atmosphere_multiscatter");
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, transmittance);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, multiscatter, storage: true);
            DispatchFor(cmd, multiscatter);
            cmd.InsertBarrierMemory();
        }
        cmd.EndMarker();
    }

    // the whole bake, see the header for what spartan spreads over frames
    private static void Pass_Skysphere(RHI_CommandList cmd)
    {
        Texture skysphere     = GetRenderTargetTexture(RenderTarget.skysphere)!;
        Texture transmittance = GetRenderTargetTexture(RenderTarget.lut_atmosphere_transmittance)!;
        Texture multiscatter  = GetRenderTargetTexture(RenderTarget.lut_atmosphere_multiscatter)!;
        Texture sky_view      = GetRenderTargetTexture(RenderTarget.lut_sky_view)!;
        uint mip_count        = skysphere.GetMipCount();

        cmd.BeginMarker("skysphere");
        {
            // sky view lut, one small march per texel instead of integrating the atmosphere per panorama pixel
            SetComputePipeline(cmd, Renderer_Shader.skysphere_sky_view_lut_c, "skysphere_sky_view_lut");
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, sky_view, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, transmittance);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, multiscatter);
            DispatchFor(cmd, sky_view);
            cmd.InsertBarrierMemory();

            // the panorama, every pixel (spartan's warmup mode with a blend of 1, a full replace)
            SetComputePipeline(cmd, Renderer_Shader.skysphere_c, "skysphere_atmospheric_scattering");
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, skysphere, 0, 1, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, transmittance);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, multiscatter);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex3, sky_view);
            var pass = new PassBufferData();
            pass.Set(0, 1.0f); // pass_skysphere::warmup_blend
            cmd.PushConstants(pass);
            DispatchFor(cmd, skysphere);
            cmd.InsertBarrierMemory();

            // averaging mip chain (Pass_Downscale with Renderer_DownsampleFilter::Average)
            SetComputePipeline(cmd, Renderer_Shader.ffx_spd_average_one_c, "skysphere_downscale");
            for (uint mip = 1; mip < mip_count; mip++)
            {
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex, skysphere, mip - 1, 1);
                cmd.SetTexture((uint)Renderer_BindingsUav.tex, skysphere, mip, 1, storage: true);
                DispatchFor(cmd, skysphere, mip);
                cmd.InsertBarrierMemory();
            }

            // ggx prefilter of mips 1+, each from the chain below it (see the header for the source binding)
            SetComputePipeline(cmd, Renderer_Shader.light_integration_environment_filter_c, "skysphere_filter");
            for (uint mip = 1; mip < mip_count; mip++)
            {
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex, skysphere);
                cmd.SetTexture((uint)Renderer_BindingsUav.tex, skysphere, mip, 1, storage: true);
                var filter = new PassBufferData();
                filter.Set(0, mip);       // pass_light_integration::mip_level
                filter.Set(1, mip_count); // pass_light_integration::mip_count
                cmd.PushConstants(filter);
                DispatchFor(cmd, skysphere, mip);
                cmd.InsertBarrierMemory();
            }
        }
        cmd.EndMarker();

        Pass_Skysphere_SH_Project(cmd);
    }

    private static void Pass_Skysphere_SH_Project(RHI_CommandList cmd)
    {
        cmd.BeginMarker("skysphere_sh_project");
        {
            SetComputePipeline(cmd, Renderer_Shader.skysphere_sh_project_c, "skysphere_sh_project");
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, GetRenderTargetTexture(RenderTarget.skysphere)!);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, GetRenderTargetTexture(RenderTarget.sky_sh)!, storage: true);
            cmd.Dispatch(1, 1, 1);
            cmd.InsertBarrierMemory();
        }
        cmd.EndMarker();
    }

    // Renderer_Passes_Lighting.cpp:1652-1690, the opaque pass: sky diffuse (sh) and specular (prefiltered skysphere)
    // added onto the composed frame
    private static void Pass_Light_ImageBased(RHI_CommandList cmd, RHI_Texture tex_out)
    {
        if (!ImageBasedLighting)
            return;
        Texture skysphere = GetRenderTargetTexture(RenderTarget.skysphere)!;
        cmd.BeginMarker("light_image_based");
        {
            SetComputePipeline(cmd, Renderer_Shader.light_image_based_c, "light_image_based");
            SetCommonTextures(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex,  tex_out, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, GetRenderTargetTexture(RenderTarget.lut_brdf_specular)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex3, skysphere);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex5, GetRenderTargetTexture(RenderTarget.sky_sh)!);
            var pass = new PassBufferData();
            pass.Set(0, (float)skysphere.GetMipCount()); // pass_light_image_based::environment_mip_count
            cmd.PushConstants(pass);
            DispatchFor(cmd, tex_out);
            cmd.InsertBarrierMemory();
        }
        cmd.EndMarker();
    }
}
