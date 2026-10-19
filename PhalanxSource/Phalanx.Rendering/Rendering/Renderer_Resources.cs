// Port of the resource half of SpartanEngine source/rendering/Renderer_Resources.cpp @ 4c07baa, the slice the first
// pass needs: CreateBuffers (:79, the frame constant buffer, the bindless material, light and geometry info tables,
// the dummy instance and the draw data), CreateSamplers (:406), CreateRenderTargets (:631, the g-buffer, frame_render
// and frame_output), CreateShaders (:1114, the shaders ported so far), CreateStandardMeshes (:1365, the cube so far),
// CreateStandardTextures (:1422, the noise, checkerboard, black and white textures),
// DestroyResources (:1616) and the getters (:1775).
// Deviations:
// - The standard textures are generated (StandardTextures.cs) instead of loaded from data/textures.
//   The gizmo icons and the night sky textures are not created yet (no editor, no sky pass); GetStandardTexture
//   returns null for them.
// - CreateSamplers keeps spartan's static "created once" guard as a field reset by DestroyResources, so a renderer
//   initialized again after Shutdown gets its samplers back (spartan's function-local statics never reset).
// - The g-buffer targets are created at the render resolution without RHI_Texture_ConcurrentSharing and without the
//   normal and velocity history copies and the hi-z pyramids, nothing reads them yet. gbuffer_depth_previous
//   (:770, taau and the upscaler reactivity read it), dlss_reactivity (:767) and taau_history (:866, mono) are created.
// - taau_history and frame_output_2 (the target it swaps with) are R32G32B32A32_Float, spartan creates both as
//   R16G16B16A16_Float (:849, :859, :872; upstream 67a6e8b :912, :922, :935), a flaw in spartan that bands on real
//   hardware (an rtx 3080), with rgba16f light buffers too. taau.hlsl blends each new jitter sample into the history
//   with a weight of about 1/(n+1) for up to 128 frames (taau.hlsl:454-475) and writes the result to the history every
//   frame (:514). A half float has 11 significant bits, so once a pixel's history weight is in the tens, the change a
//   sample makes, (sample - history)/(n+1), is under half a half-float step whenever the history is within a few
//   percent of the samples, and the stored value doesn't move. The clip box doesn't pull it back either: the luminance
//   padding of taau.hlsl:384-386 (0.05 * level * (1 - level) in taa tonemapped space) is several percent of the
//   radiance. Each pixel freezes at whatever value it had when its weight grew, so a smooth gradient (a floor lit by a
//   point light) turns into fine contour lines that follow the shading. A float history converges to the jittered mean.
// - light_diffuse and light_specular are R16G16B16A16_Float, spartan creates them as R11G11B10_Float (:809-810;
//   upstream 67a6e8b :870-871), a flaw in spartan as well. That format keeps 6 mantissa bits for red and green and 5
//   for blue, so the light comes out in steps of 1.6% to 3%. With anti-aliasing off they are single steps, under one 8
//   bit code after the tonemapper. Taau turns them into bands: the jitter moves each pixel's samples across the steps,
//   the clip box is wider than a step, so a jump to the next step isn't clipped and the history only creeps towards it
//   at 1/(n+1) per frame. When a soft shadow or a light moves over a static floor, every pixel lags by a different
//   amount depending on when it last crossed a step, and the lags show as stepped contour bands behind the shadow.
//   rgba16f has 11 significant bits, the steps fall to 0.05%.
// - frame_output has one mip (spartan: a full chain with per-mip views, for the bloom and auto exposure
//   downsamples); RHI_Texture_ConcurrentSharing is dropped, there is no async compute yet.
using Phalanx.Rendering.RHI;
using static Phalanx.Rendering.Renderer_Definitions;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using RenderView   = Phalanx.World.RenderView;
using Texture      = Phalanx.Rendering.RHI.RHI_Texture;
using Material     = Phalanx.World.Material;
using MaterialProperty    = Phalanx.World.MaterialProperty;
using MaterialTextureType = Phalanx.World.MaterialTextureType;

namespace Phalanx.Rendering;

// spartan's shader names; resolve_c is Phalanx's stand-in for light, composition and tonemapping
internal enum Renderer_Shader
{
    gbuffer_v, gbuffer_p, depth_prepass_alpha_p, light_c, light_composition_c, light_image_based_c, output_c, blit_c, depth_light_v, depth_light_alpha_p, taau_c, dlss_reactivity_c,
    light_integration_brdf_specular_lut_c, light_integration_environment_filter_c, ffx_spd_average_one_c,
    skysphere_c, skysphere_transmittance_lut_c, skysphere_multiscatter_lut_c, skysphere_sky_view_lut_c, skysphere_sh_project_c,
    max
}

public static partial class Renderer
{
    private static readonly Texture?[] s_render_targets     = new Texture?[(int)RenderTarget.max];
    private static readonly Texture?[] s_standard_textures  = new Texture?[(int)Renderer_StandardTexture.Max];
    private static readonly RHI_Sampler?[] s_samplers       = new RHI_Sampler?[(int)Renderer_Sampler.Max];
    private static readonly RHI_Buffer?[] s_buffers         = new RHI_Buffer?[(int)Renderer_Buffer.Max];
    private static readonly RHI_Shader?[] s_shaders         = new RHI_Shader?[(int)Renderer_Shader.max];
    private static bool s_samplers_created;
    private static float s_mip_bias = float.MaxValue;
    private static bool s_bindless_samplers_dirty;

    internal static Texture? GetRenderTargetTexture(RenderTarget type) => s_render_targets[(int)type];
    internal static Texture? GetStandardTexture(Renderer_StandardTexture type) => s_standard_textures[(int)type];
    internal static RHI_Sampler? GetSampler(Renderer_Sampler type) => s_samplers[(int)type];
    internal static RHI_Buffer? GetBuffer(Renderer_Buffer type) => s_buffers[(int)type];
    internal static RHI_Shader? GetShader(Renderer_Shader type) => s_shaders[(int)type];

    private static void CreateBuffers()
    {
        uint element_count = renderer_draw_data_buffer_count;
        static RHI_Buffer Storage<T>(uint count, string name) where T : unmanaged =>
            new(RHI_Buffer_Type.Storage, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), count, ReadOnlySpan<byte>.Empty, mappable: true, name);

        s_buffers[(int)Renderer_Buffer.ConstantFrame] = new RHI_Buffer(RHI_Buffer_Type.Constant, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<FrameBufferData>(),
                                                                      element_count, ReadOnlySpan<byte>.Empty, mappable: true, "frame");
        s_buffers[(int)Renderer_Buffer.MaterialParameters] = Storage<MaterialParameters>(RHI_Definitions.rhi_max_array_size, "materials");
        s_buffers[(int)Renderer_Buffer.LightParameters]    = Storage<LightParameters>(RHI_Definitions.rhi_max_array_size, "lights");
        // one record per tlas instance, scattered foliage enters per instance so this outgrows the entity count
        s_buffers[(int)Renderer_Buffer.GeometryInfo]       = Storage<GeometryInfo>(RHI_Definitions.rhi_max_array_size * 4, "geometry_info");

        PackedInstance[] identity = { PackedInstance.GetIdentity() };
        s_buffers[(int)Renderer_Buffer.DummyInstance] = new RHI_Buffer(RHI_Buffer_Type.Instance, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<PackedInstance>(), (uint)identity.Length,
                                                                      System.Runtime.InteropServices.MemoryMarshal.AsBytes(identity.AsSpan()), mappable: true, "dummy_instance_buffer");
        // one buffer for every frame, each writes its own offset region so the bindless descriptors never change under in-flight commands
        s_buffers[(int)Renderer_Buffer.DrawData] = Storage<DrawData>(renderer_max_draw_calls * renderer_draw_data_buffer_count, "draw_data");
    }

    private static void CreateSamplers()
    {
        void Create(Renderer_Sampler type, RHI_Filter filter_min, RHI_Filter filter_mag, RHI_Filter filter_mip, RHI_Sampler_Address_Mode address_mode,
                    RHI_Comparison_Function comparison_func, float anisotropy, bool comparison_enabled, float mip_bias)
        {
            s_samplers[(int)type]?.Dispose();
            s_samplers[(int)type] = new RHI_Sampler(filter_min, filter_mag, filter_mip, address_mode, comparison_func, anisotropy, comparison_enabled, mip_bias, type.ToString().ToLowerInvariant());
        }

        // non anisotropic
        if (!s_samplers_created)
        {
            Create(Renderer_Sampler.Compare_depth,         RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Greater, 0.0f, true,  0.0f); // reverse-z
            Create(Renderer_Sampler.Point_clamp_edge,      RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Point_clamp_border,    RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Point_wrap,            RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Wrap,        RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Bilinear_clamp_edge,   RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Bilinear_clamp_border, RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.ClampToZero, RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Bilinear_wrap,         RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Nearest, RHI_Sampler_Address_Mode.Wrap,        RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            Create(Renderer_Sampler.Trilinear_clamp,       RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Filter.Linear,  RHI_Sampler_Address_Mode.Clamp,       RHI_Comparison_Function.Never,   0.0f, false, 0.0f);
            s_samplers_created = true;
        }

        // anisotropic (negative mip bias when upscaling to keep textures sharp)
        float mip_bias_new = 0.0f;
        if (s_resolution_output_width > s_resolution_render_width)
            mip_bias_new = MathF.Log2((float)s_resolution_render_width / s_resolution_output_width) - 1.0f;
        if (mip_bias_new != s_mip_bias)
        {
            s_mip_bias = mip_bias_new;
            Create(Renderer_Sampler.Anisotropic_wrap, RHI_Filter.Linear, RHI_Filter.Linear, RHI_Filter.Linear, RHI_Sampler_Address_Mode.Wrap, RHI_Comparison_Function.Always, 16.0f, false, s_mip_bias);
        }

        s_bindless_samplers_dirty = true;
    }

    private static void CreateRenderTargets(bool create_render, bool create_output)
    {
        const RHI_Texture_Flags flags = RHI_Texture_Flags.RHI_Texture_Uav | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_ClearBlit;
        void Replace(RenderTarget type, uint width, uint height, RHI_Format format = RHI_Format.R16G16B16A16_Float)
        {
            s_render_targets[(int)type]?.Dispose(); // the deletion queue keeps it alive for frames in flight
            s_render_targets[(int)type] = new Texture(RHI_Texture_Type.Type2D, width, height, 1, 1, format, flags, type.ToString());
            RenderView.SetRenderTargetSize(type, width, height);
        }

        // g-buffer (Renderer_Resources.cpp:758-766)
        void GBuffer(RenderTarget type, RHI_Format format)
        {
            const RHI_Texture_Flags gbuffer_flags = RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit;
            s_render_targets[(int)type]?.Dispose();
            s_render_targets[(int)type] = new Texture(RHI_Texture_Type.Type2D, s_resolution_render_width, s_resolution_render_height, 1, 1, format, gbuffer_flags, type.ToString());
            RenderView.SetRenderTargetSize(type, s_resolution_render_width, s_resolution_render_height);
        }

        if (create_render)
        {
            GBuffer(RenderTarget.gbuffer_color,    RHI_Format.R8G8B8A8_Unorm);
            GBuffer(RenderTarget.gbuffer_normal,   RHI_Format.R16G16B16A16_Float);
            GBuffer(RenderTarget.gbuffer_material, RHI_Format.R8G8B8A8_Unorm);
            GBuffer(RenderTarget.gbuffer_emissive, RHI_Format.R11G11B10_Float);
            GBuffer(RenderTarget.gbuffer_velocity, RHI_Format.R16G16B16A16_Float);
            Replace(RenderTarget.dlss_reactivity, s_resolution_render_width, s_resolution_render_height, RHI_Format.R8G8B8A8_Unorm);
            GBuffer(RenderTarget.gbuffer_depth,    RHI_Format.D32_Float);
            // the temporal passes test disocclusion against the prior depth (:770); it starts cleared, see Pass_Depth_Prepass
            GBuffer(RenderTarget.gbuffer_depth_previous, RHI_Format.D32_Float);
            s_depth_history_cleared = false;
            Replace(RenderTarget.frame_render, s_resolution_render_width, s_resolution_render_height);
            // light (Renderer_Resources.cpp:809-810), rgba16f instead of r11g11b10, see the deviation in the header
            Replace(RenderTarget.light_diffuse,  s_resolution_render_width, s_resolution_render_height, LightBufferFormat);
            Replace(RenderTarget.light_specular, s_resolution_render_width, s_resolution_render_height, LightBufferFormat);

            // shadow atlas (Renderer_Resources.cpp:842), d32, independent of the resolution: created once
            Texture? atlas = s_render_targets[(int)RenderTarget.shadow_atlas];
            if (atlas == null || atlas.GetWidth() != ShadowAtlasResolution)
            {
                atlas?.Dispose();
                s_render_targets[(int)RenderTarget.shadow_atlas] = new Texture(RHI_Texture_Type.Type2D, ShadowAtlasResolution, ShadowAtlasResolution, 1, 1, RHI_Format.D32_Float,
                    RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_ClearBlit, "shadow_atlas");
                RenderView.SetRenderTargetSize(RenderTarget.shadow_atlas, ShadowAtlasResolution, ShadowAtlasResolution);
            }
        }
        if (create_output)
        {
            // Renderer_Resources.cpp:848-849, frame_output_2 is the post process ping-pong target
            Replace(RenderTarget.frame_output,   s_resolution_output_width, s_resolution_output_height);
            // :866, the mono history; Pass_AA_Upscale swaps it with frame_output_2 every taau frame, so both need the
            // same size and format. Both are rgba32f (spartan: rgba16f), see the deviation in the header.
            RHI_Format history_format = TaauHistoryFormat;
            Replace(RenderTarget.frame_output_2, s_resolution_output_width, s_resolution_output_height, history_format);
            Replace(RenderTarget.taau_history,   s_resolution_output_width, s_resolution_output_height, history_format);
        }

        CreateSkyRenderTargets();

        // new targets hold no history (Renderer.cpp:1720, SetResolution resets it with the targets)
        s_taau_reset_history = true;
    }

    private static void CreateShaders()
    {
        void Compile(Renderer_Shader type, RHI_Shader_Type stage, string file, RHI_Vertex_Type vertex_type = RHI_Vertex_Type.Max, string? define = null)
        {
            var shader = new RHI_Shader();
            if (define != null)
                shader.AddDefine(define);
            shader.Compile(stage, file, async: false, vertex_type);
            if (!shader.IsCompiled())
                throw new InvalidOperationException($"{file} failed to compile: {shader.LastError}");
            s_shaders[(int)type] = shader;
        }

        Compile(Renderer_Shader.gbuffer_v, RHI_Shader_Type.Vertex,  "g_buffer.vert", RHI_Vertex_Type.PosUvNorTan);
        Compile(Renderer_Shader.gbuffer_p, RHI_Shader_Type.Pixel,   "g_buffer.frag");
        Compile(Renderer_Shader.depth_prepass_alpha_p, RHI_Shader_Type.Pixel, "depth_prepass_alpha.frag");
        Compile(Renderer_Shader.light_c,             RHI_Shader_Type.Compute, "light.comp");
        Compile(Renderer_Shader.light_composition_c, RHI_Shader_Type.Compute, "light_composition.comp");
        Compile(Renderer_Shader.output_c,            RHI_Shader_Type.Compute, "output.comp");
        Compile(Renderer_Shader.blit_c,         RHI_Shader_Type.Compute, "blit.comp");
        Compile(Renderer_Shader.depth_light_v,       RHI_Shader_Type.Vertex,  "depth_light.vert", RHI_Vertex_Type.PosUvNorTan);
        Compile(Renderer_Shader.depth_light_alpha_p, RHI_Shader_Type.Pixel,   "depth_light_alpha.frag");
        Compile(Renderer_Shader.taau_c,              RHI_Shader_Type.Compute, "taau.comp");
        Compile(Renderer_Shader.dlss_reactivity_c,   RHI_Shader_Type.Compute, "dlss_reactivity.comp");

        // sky and image based lighting (Renderer_Resources.cpp:1168-1169, :1180, :1197, :1202-1206, spartan's defines select the kernel)
        Compile(Renderer_Shader.light_image_based_c,                    RHI_Shader_Type.Compute, "light_image_based.comp");
        Compile(Renderer_Shader.light_integration_brdf_specular_lut_c,  RHI_Shader_Type.Compute, "light_integration.comp", define: "BRDF_SPECULAR_LUT");
        Compile(Renderer_Shader.light_integration_environment_filter_c, RHI_Shader_Type.Compute, "light_integration.comp", define: "ENVIRONMENT_FILTER");
        Compile(Renderer_Shader.ffx_spd_average_one_c,                    RHI_Shader_Type.Compute, "downscale.comp");
        Compile(Renderer_Shader.skysphere_c,                            RHI_Shader_Type.Compute, "sky/skysphere.comp");
        Compile(Renderer_Shader.skysphere_transmittance_lut_c,          RHI_Shader_Type.Compute, "sky/skysphere.comp", define: "TRANSMITTANCE_LUT");
        Compile(Renderer_Shader.skysphere_multiscatter_lut_c,           RHI_Shader_Type.Compute, "sky/skysphere.comp", define: "MULTISCATTER_LUT");
        Compile(Renderer_Shader.skysphere_sky_view_lut_c,               RHI_Shader_Type.Compute, "sky/skysphere.comp", define: "SKY_VIEW_LUT");
        Compile(Renderer_Shader.skysphere_sh_project_c,                 RHI_Shader_Type.Compute, "sky/skysphere_sh_project.comp");
    }

    private static readonly Mesh?[] s_standard_meshes = new Mesh?[(int)Phalanx.Geometry.MeshType.Max];
    public static Mesh? GetStandardMesh(Phalanx.Geometry.MeshType type) => s_standard_meshes[(int)type];

    private static void CreateStandardMeshes()
    {
        void Create(Phalanx.Geometry.MeshType type, Action<List<Phalanx.Geometry.RHI_Vertex_PosTexNorTan>, List<uint>> generate, string name)
        {
            var vertices = new List<Phalanx.Geometry.RHI_Vertex_PosTexNorTan>();
            var indices  = new List<uint>();
            generate(vertices, indices);
            var mesh = new Mesh();
            mesh.SetResourceName(name); // spartan: SetResourceFilePath(project directory + name), no project directory yet
            mesh.SetFlag((uint)MeshFlags.PostProcessOptimize, false);
            mesh.AddGeometry(vertices, indices, false);
            mesh.SetMeshType(type);
            mesh.CreateGpuBuffers();
            s_standard_meshes[(int)type] = mesh;
            RenderView.SetStandardMesh(type, mesh); // Render::SetMesh(MeshType) reads it
        }
        Create(Phalanx.Geometry.MeshType.Cube, Phalanx.Geometry.GeometryGeneration.generate_cube, "standard_cube");
        Create(Phalanx.Geometry.MeshType.Quad, Phalanx.Geometry.GeometryGeneration.generate_quad, "standard_quad");
    }

    // Renderer_Resources.cpp:1598. Not ported: SetPersistent and the data directory (no material files yet).
    private static Material? s_standard_material;
    public static Material? GetStandardMaterial() => s_standard_material;
    private static void CreateStandardMaterials()
    {
        var material = new Material();
        material.SetResourceName("standard" + FileSystem.EXTENSION_MATERIAL);
        material.SetProperty(MaterialProperty.TextureTilingX, 1.0f);
        material.SetProperty(MaterialProperty.TextureTilingY, 1.0f);
        material.SetProperty(MaterialProperty.ColorR,         1.0f);
        material.SetProperty(MaterialProperty.ColorG,         1.0f);
        material.SetProperty(MaterialProperty.ColorB,         1.0f);
        material.SetProperty(MaterialProperty.ColorA,         1.0f);
        material.SetProperty(MaterialProperty.WorldSpaceUv,   1.0f);
        material.SetTexture(MaterialTextureType.Color,        GetStandardTexture(Renderer_StandardTexture.Checkerboard));
        s_standard_material = material;
        RenderView.SetStandardMaterial(material);
    }

    /// <summary>A cpu texture for <see cref="Phalanx.World.Material.SetTexture"/> from <paramref name="rgba8"/>
    /// (width * height * 4 bytes) with its full mip chain; the renderer uploads it the first time a material binds it.
    /// Colour and emission textures in this format are decoded as sRGB unless <paramref name="greyscale"/> (spartan:
    /// should_decode_as_srgb); alpha below 255 marks it transparent, which alpha tests a colour map (spartan: the
    /// importer's has_transparent_pixels). Files go through Phalanx.Assets' ImageImporter.</summary>
    public static Phalanx.World.RHI_Texture CreateTexture(string name, uint width, uint height, byte[] rgba8, bool greyscale = false)
    {
        if (rgba8.Length != (long)width * height * 4)
            throw new ArgumentException($"expected {width}x{height}x4 bytes, got {rgba8.Length}", nameof(rgba8));
        var flags = (greyscale ? Phalanx.World.RHI_Texture_Cpu_Flags.Greyscale : 0) |
                    (Phalanx.World.RHI_Texture.HasTransparentPixels(rgba8) ? Phalanx.World.RHI_Texture_Cpu_Flags.Transparent : 0);
        return new Phalanx.World.RHI_Texture(name, width, height, Phalanx.World.RHI_Texture.GenerateMips(rgba8, width, height), flags);
    }

    private static void CreateStandardTextures()
    {
        static Texture Create(string name, uint width, uint height, byte[] pixels)
        {
            var slice = new RHI_Texture_Slice();
            slice.mips.Add(new RHI_Texture_Mip { bytes = pixels });
            return new Texture(RHI_Texture_Type.Type2D, width, height, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Srv, name,
                               new List<RHI_Texture_Slice> { slice });
        }

        s_standard_textures[(int)Renderer_StandardTexture.Noise_perlin] = Create("noise_perlin", StandardTextures.PerlinSize, StandardTextures.PerlinSize, StandardTextures.Perlin());
        s_standard_textures[(int)Renderer_StandardTexture.Noise_blue]   = Create("noise_blue", StandardTextures.BlueNoiseSize, StandardTextures.BlueNoiseSize, StandardTextures.BlueNoise());
        s_standard_textures[(int)Renderer_StandardTexture.Checkerboard] = Create("no_texture", StandardTextures.CheckerboardSize, StandardTextures.CheckerboardSize, StandardTextures.Checkerboard());

        // solid 1x1 textures (spartan adds the uav flag, nothing writes them)
        s_standard_textures[(int)Renderer_StandardTexture.Black] = Create("black_texture", 1, 1, StandardTextures.Solid(0, 0, 0, 255));
        s_standard_textures[(int)Renderer_StandardTexture.White] = Create("white_texture", 1, 1, StandardTextures.Solid(255, 255, 255, 255));
    }

    private static void DestroyResources()
    {
        static void DisposeAll<T>(T?[] items) where T : class, IDisposable
        {
            foreach (T? item in items)
                item?.Dispose();
            Array.Clear(items);
        }
        DisposeAll(s_render_targets);
        DisposeAll(s_standard_textures);
        DisposeAll(s_samplers);
        DisposeAll(s_buffers);
        DisposeAll(s_shaders);
        DisposeAll(s_standard_meshes);
        for (int i = 0; i < (int)Phalanx.Geometry.MeshType.Max; i++)
            RenderView.SetStandardMesh((Phalanx.Geometry.MeshType)i, null);
        s_standard_material = null;
        RenderView.SetStandardMaterial(null);
        s_samplers_created = false;
        s_mip_bias         = float.MaxValue;
        ResetSkyState();
    }
}
