// Port of SpartanEngine source/rhi/RHI_PipelineState.h/.cpp @ 4c07baa
// Deviations:
// - A class with Clone() instead of a value type copied with operator= (the command list keeps its own copy).
// - validate() throws instead of asserting, ray tracing and mesh shaders are not ported (no stages for them yet).
// - No resolution scale (RHI_Device::ScaleDimension arrives with the renderer), no vrs input texture and no cull_mode
//   field (d3d12 only in spartan, vulkan keeps cull mode dynamic).
// - The swapchain contributes its format to the hash like spartan, so do the render targets (format and type, not
//   object ids, so recreating a render target doesn't grow the pipeline cache).
using Phalanx.Math;

namespace Phalanx.Rendering.RHI;

internal sealed class RHI_PipelineState
{
    // state
    public RHI_RasterizerState? rasterizer_state;
    public RHI_BlendState? blend_state;
    public RHI_DepthStencilState? depth_stencil_state;
    public RHI_SwapChain? render_target_swapchain;
    public RHI_PrimitiveTopology primitive_topology = RHI_PrimitiveTopology.TriangleList;
    public RHI_Texture? render_target_depth_texture;
    public uint render_target_array_index;
    public bool is_multiview;
    public readonly RHI_Shader?[] shaders = new RHI_Shader?[(int)RHI_Shader_Type.Max];
    public readonly RHI_Texture?[] render_target_color_textures = new RHI_Texture?[RHI_Definitions.rhi_max_render_target_count];

    // dynamic properties, changing these will not create a new pso
    public bool use_standard_resources = true;
    public float clear_depth  = RHI_Definitions.rhi_depth_load;
    public uint clear_stencil = RHI_Definitions.rhi_stencil_load;
    public readonly Color[] clear_color = new Color[RHI_Definitions.rhi_max_render_target_count];
    public string? name; // used by the validation layer

    private uint m_width;
    private uint m_height;
    private ulong m_hash;

    public RHI_PipelineState()
    {
        Array.Fill(clear_color, RHI_Definitions.rhi_color_load);
    }

    public RHI_PipelineState Clone()
    {
        var clone = new RHI_PipelineState
        {
            rasterizer_state            = rasterizer_state,
            blend_state                 = blend_state,
            depth_stencil_state         = depth_stencil_state,
            render_target_swapchain     = render_target_swapchain,
            primitive_topology          = primitive_topology,
            render_target_depth_texture = render_target_depth_texture,
            render_target_array_index   = render_target_array_index,
            is_multiview                = is_multiview,
            use_standard_resources      = use_standard_resources,
            clear_depth                 = clear_depth,
            clear_stencil               = clear_stencil,
            name                        = name,
            m_width                     = m_width,
            m_height                    = m_height,
            m_hash                      = m_hash
        };
        shaders.CopyTo(clone.shaders, 0);
        render_target_color_textures.CopyTo(clone.render_target_color_textures, 0);
        clear_color.CopyTo(clone.clear_color, 0);
        return clone;
    }

    public ulong GetHash() => m_hash;
    public uint GetWidth() => m_width;
    public uint GetHeight() => m_height;

    public RHI_Shader? GetShader(RHI_Shader_Type type) => shaders[(int)type];
    public void SetShader(RHI_Shader_Type type, RHI_Shader? shader) => shaders[(int)type] = shader;

    public void Prepare()
    {
        ApplyGraphicsDefaults();
        m_hash = ComputeHash();
        GetDimensions(out m_width, out m_height);
        Validate();
    }

    public void SetColorTargets(RHI_Texture? t0, RHI_Texture? t1 = null, RHI_Texture? t2 = null, RHI_Texture? t3 = null,
                                RHI_Texture? t4 = null, RHI_Texture? t5 = null, RHI_Texture? t6 = null, RHI_Texture? t7 = null)
    {
        render_target_color_textures[0] = t0; render_target_color_textures[1] = t1;
        render_target_color_textures[2] = t2; render_target_color_textures[3] = t3;
        render_target_color_textures[4] = t4; render_target_color_textures[5] = t5;
        render_target_color_textures[6] = t6; render_target_color_textures[7] = t7;
    }

    public void SetDepthTarget(RHI_Texture? texture) => render_target_depth_texture = texture;

    public bool HasClearValues()
    {
        if (clear_depth != RHI_Definitions.rhi_depth_load && clear_depth != RHI_Definitions.rhi_depth_dont_care)
            return true;

        if (clear_stencil != RHI_Definitions.rhi_stencil_load && clear_stencil != RHI_Definitions.rhi_stencil_dont_care)
            return true;

        foreach (Color color in clear_color)
            if (color != RHI_Definitions.rhi_color_load && color != RHI_Definitions.rhi_color_dont_care)
                return true;

        return false;
    }

    public bool IsGraphics() =>
        (HasShader(RHI_Shader_Type.Vertex) || HasShader(RHI_Shader_Type.Hull) || HasShader(RHI_Shader_Type.Domain) || HasShader(RHI_Shader_Type.Pixel) || HasShader(RHI_Shader_Type.MeshShader)) &&
        !HasShader(RHI_Shader_Type.Compute) && !HasShader(RHI_Shader_Type.RayGeneration) && !HasShader(RHI_Shader_Type.RayMiss) && !HasShader(RHI_Shader_Type.RayHit);

    public bool IsCompute() =>
        HasShader(RHI_Shader_Type.Compute) &&
        !HasShader(RHI_Shader_Type.Vertex) && !HasShader(RHI_Shader_Type.Hull) && !HasShader(RHI_Shader_Type.Domain) && !HasShader(RHI_Shader_Type.Pixel) &&
        !HasShader(RHI_Shader_Type.MeshShader) && !HasShader(RHI_Shader_Type.RayGeneration) && !HasShader(RHI_Shader_Type.RayMiss) && !HasShader(RHI_Shader_Type.RayHit);

    public bool HasTessellation() => HasShader(RHI_Shader_Type.Hull) && HasShader(RHI_Shader_Type.Domain);

    private bool HasShader(RHI_Shader_Type type) => shaders[(int)type] != null;

    private void Validate()
    {
        bool compiled(RHI_Shader_Type type) => shaders[(int)type]?.IsCompiled() ?? false;
        bool has_shader_compute = compiled(RHI_Shader_Type.Compute);
        bool has_shader_vertex  = compiled(RHI_Shader_Type.Vertex);
        bool has_shader_pixel   = compiled(RHI_Shader_Type.Pixel);
        for (var type = RHI_Shader_Type.Vertex; type < RHI_Shader_Type.Max; type++)
            if (type is not (RHI_Shader_Type.Vertex or RHI_Shader_Type.Pixel or RHI_Shader_Type.Compute) && HasShader(type))
                throw new NotSupportedException($"{name}: {type} shaders are not supported");

        if (!has_shader_compute && !has_shader_vertex && !has_shader_pixel)
            throw new InvalidOperationException($"{name ?? "pipeline state"}: there is no shader set, ensure that it compiled successfully and that it has been set");

        bool is_graphics = (has_shader_vertex || has_shader_pixel) && !has_shader_compute;
        bool is_compute  = has_shader_compute && !has_shader_vertex && !has_shader_pixel;
        if (!is_graphics && !is_compute)
            throw new InvalidOperationException($"{name}: invalid pipeline state type, must be graphics or compute");

        if (is_graphics)
        {
            bool has_render_target = render_target_color_textures[0] != null || render_target_depth_texture != null;
            bool has_backbuffer    = render_target_swapchain != null;
            if (rasterizer_state == null || blend_state == null || depth_stencil_state == null)
                throw new InvalidOperationException($"{name}: graphics states are missing");
            if (!has_render_target && !has_backbuffer)
                throw new InvalidOperationException($"{name}: a render target is missing");
            if (!has_shader_vertex)
                throw new InvalidOperationException($"{name}: a graphics pipeline needs a vertex shader");
            if (m_width == 0 || m_height == 0)
                throw new InvalidOperationException($"{name}: the render target has no size");
        }

        if (name == null)
            throw new InvalidOperationException("Name your pipeline state");
    }

    private ulong ComputeHash()
    {
        ulong hash = 0;

        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)primitive_topology);

        if (render_target_swapchain != null)
            hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)render_target_swapchain.GetFormat());

        if (rasterizer_state != null)
            hash = RHI_Definitions.rhi_hash_combine(hash, rasterizer_state.GetHash());

        if (blend_state != null)
            hash = RHI_Definitions.rhi_hash_combine(hash, blend_state.GetHash());

        if (depth_stencil_state != null)
            hash = RHI_Definitions.rhi_hash_combine(hash, depth_stencil_state.GetHash());

        // shaders
        foreach (RHI_Shader? shader in shaders)
            if (shader != null)
                hash = RHI_Definitions.rhi_hash_combine(hash, shader.GetHash());

        // rt, formats not object ids, recreating an rt must not explode the pipeline cache
        void hash_rt(RHI_Texture? texture)
        {
            if (texture == null)
                return;
            hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)texture.GetFormat());
            hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)texture.Type);
        }
        foreach (RHI_Texture? texture in render_target_color_textures)
            hash_rt(texture);
        hash_rt(render_target_depth_texture);
        hash = RHI_Definitions.rhi_hash_combine(hash, render_target_array_index);
        hash = RHI_Definitions.rhi_hash_combine(hash, is_multiview ? 1UL : 0UL);

        return hash;
    }

    private void GetDimensions(out uint width, out uint height)
    {
        width  = 0;
        height = 0;

        if (render_target_swapchain != null)
        {
            width  = render_target_swapchain.Width;
            height = render_target_swapchain.Height;
        }
        else if (render_target_color_textures[0] is { } color)
        {
            width  = color.GetWidth();
            height = color.GetHeight();
        }
        else if (render_target_depth_texture is { } depth)
        {
            width  = depth.GetWidth();
            height = depth.GetHeight();
        }
    }

    // spartan keeps these as function statics
    private static readonly RHI_RasterizerState s_rasterizer        = new(RHI_PolygonMode.Solid, true, 0.0f, 0.0f, 0.0f, 3.0f);
    private static readonly RHI_BlendState s_blend                  = new(false);
    private static readonly RHI_DepthStencilState s_depth_off        = new(false, false, RHI_Comparison_Function.Never);
    private static readonly RHI_DepthStencilState s_depth_read_write = new(true, true, RHI_Comparison_Function.GreaterEqual);

    private void ApplyGraphicsDefaults()
    {
        if (!IsGraphics())
            return;

        rasterizer_state    ??= s_rasterizer;
        blend_state         ??= s_blend;
        depth_stencil_state ??= render_target_depth_texture != null ? s_depth_read_write : s_depth_off;
    }
}
