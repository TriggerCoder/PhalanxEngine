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
//   history copies (normal, velocity and depth previous) and the hi-z pyramids, nothing reads them yet.
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
internal enum Renderer_Shader { gbuffer_v, gbuffer_p, depth_prepass_alpha_p, light_c, light_composition_c, output_c, blit_c, max }

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
            GBuffer(RenderTarget.gbuffer_depth,    RHI_Format.D32_Float);
            Replace(RenderTarget.frame_render, s_resolution_render_width, s_resolution_render_height);
            // light (Renderer_Resources.cpp:809-810)
            Replace(RenderTarget.light_diffuse,  s_resolution_render_width, s_resolution_render_height, RHI_Format.R11G11B10_Float);
            Replace(RenderTarget.light_specular, s_resolution_render_width, s_resolution_render_height, RHI_Format.R11G11B10_Float);
        }
        if (create_output)
        {
            // Renderer_Resources.cpp:848-849, frame_output_2 is the post process ping-pong target
            Replace(RenderTarget.frame_output,   s_resolution_output_width, s_resolution_output_height);
            Replace(RenderTarget.frame_output_2, s_resolution_output_width, s_resolution_output_height);
        }
    }

    private static void CreateShaders()
    {
        void Compile(Renderer_Shader type, RHI_Shader_Type stage, string file, RHI_Vertex_Type vertex_type = RHI_Vertex_Type.Max)
        {
            var shader = new RHI_Shader();
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
    }

    private static readonly Mesh?[] s_standard_meshes = new Mesh?[(int)Phalanx.Geometry.MeshType.Max];
    public static Mesh? GetStandardMesh(Phalanx.Geometry.MeshType type) => s_standard_meshes[(int)type];

    private static void CreateStandardMeshes()
    {
        var vertices = new List<Phalanx.Geometry.RHI_Vertex_PosTexNorTan>();
        var indices  = new List<uint>();
        Phalanx.Geometry.GeometryGeneration.generate_cube(vertices, indices);
        var mesh = new Mesh();
        mesh.SetResourceName("standard_cube"); // spartan: SetResourceFilePath(project directory + name), no project directory yet
        mesh.SetFlag((uint)MeshFlags.PostProcessOptimize, false);
        mesh.AddGeometry(vertices, indices, false);
        mesh.SetMeshType(Phalanx.Geometry.MeshType.Cube);
        mesh.CreateGpuBuffers();
        s_standard_meshes[(int)Phalanx.Geometry.MeshType.Cube] = mesh;
        RenderView.SetStandardMesh(Phalanx.Geometry.MeshType.Cube, mesh); // Render::SetMesh(MeshType) reads it
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
    }
}
