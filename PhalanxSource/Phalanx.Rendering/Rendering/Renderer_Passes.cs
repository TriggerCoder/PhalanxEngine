// The renderer's passes so far. ProduceFrame follows the shape of SpartanEngine Renderer::ProduceFrame
// (Renderer.cpp:5504 @ 4c07baa): the g-buffer fills from the scene, the result lands in frame_render and then
// frame_output, which Tick blits to the back buffer. Pass_GBuffer ports the cpu-driven draw loop of
// Renderer_Passes_Geometry.cpp (Pass_GBuffer), Pass_Blit is a port of Renderer_Passes_Post.cpp:590-619.
// Pass_Resolve is not in spartan: it shades the g-buffer into frame_render, standing in for the light, composition
// and post process passes until they are ported.
// Pass_Depth_Prepass ports the opaque half of Renderer_Passes_Geometry.cpp:1463, then the g-buffer shades only the
// prepass survivors with an equal depth test (ReadEqual, the state spartan's alpha half uses at :1764).
// Deviations: spartan's opaque indirect g-buffer keeps GreaterEqual (:1695) because its prepass and g-buffer run
// different mesh shaders that are not bit identical; Phalanx's prepass runs the g-buffer's own vertex shader module
// (spartan: a separate depth_prepass_v) with an invariant gl_Position, so the depths match by construction and the
// equal test is safe. The prepass culls back faces (spartan: cull None, its triangles are culled on the gpu before).
// No alpha-tested half, previous depth copy or hi-z yet; no gpu-driven indirect path, culling or lods; frame_render goes to
// frame_output through Pass_Blit; the passes have no gpu timing (no profiler yet), they are markers.
using Phalanx.Geometry;
using System.Numerics;
using Phalanx.Rendering.RHI;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    /// <summary>What Pass_Resolve writes where no geometry is (the clear colour of frame_render).</summary>
    public static Vector4 ResolveBackgroundColor { get; set; } = new(0.0f, 0.0f, 0.0f, 1.0f);

    /// <summary>Waits for the gpu and reads one pixel of frame_output (rgba16f), for headless runs and tools.</summary>
    public static Vector4 ReadOutputPixel(uint x, uint y)
    {
        RHI_Device.QueueWaitAll();
        RHI_Texture output = GetRenderTargetTexture(RenderTarget.frame_output)!;
        byte[] bytes = RHI_Device.ReadbackTexture(output, 0, 0);
        int i = (int)(y * output.GetWidth() + x) * 8;
        float H(int c) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i + c * 2));
        return new Vector4(H(0), H(1), H(2), H(3));
    }

    private static void ProduceFrame(RHI_CommandList cmd)
    {
        RHI_Texture rt_render = GetRenderTargetTexture(RenderTarget.frame_render)!;
        RHI_Texture rt_output = GetRenderTargetTexture(RenderTarget.frame_output)!;

        Pass_Depth_Prepass(cmd);
        Pass_GBuffer(cmd);
        Pass_Resolve(cmd, rt_render);
        Pass_Blit(cmd, rt_render, rt_output);
    }

    // Renderer_Passes_Geometry.cpp:1463, the opaque half: no pixel shader, ReadWrite depth, clears the depth (reverse-z 0)
    private static void Pass_Depth_Prepass(RHI_CommandList cmd)
    {
        cmd.BeginMarker("depth_prepass");
        {
            var pso = new RHI_PipelineState { name = "depth_prepass" };
            pso.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.gbuffer_v)!);
            pso.rasterizer_state            = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            pso.depth_stencil_state         = new RHI_DepthStencilState(true, true, RHI_Comparison_Function.GreaterEqual);
            pso.blend_state                 = new RHI_BlendState(false);
            pso.render_target_depth_texture = GetRenderTargetTexture(RenderTarget.gbuffer_depth)!;
            pso.clear_depth                 = 0.0f; // reverse-z, unconditionally so an empty scene still has a fresh depth
            cmd.SetPipelineState(pso);
            cmd.SetCullMode(RHI_CullMode.Back);
            SetStandardResources(cmd);
            DrawOpaque(cmd);
        }
        cmd.EndMarker();
    }

    // the cpu draw loop both passes share, over the frame's draw calls
    private static void DrawOpaque(RHI_CommandList cmd)
    {
        RHI_Buffer? vertices = GeometryBuffer.GetVertexBuffer();
        RHI_Buffer? indices  = GeometryBuffer.GetIndexBuffer();
        if (vertices == null || indices == null || s_draw_calls.Count == 0)
            return;
        cmd.SetBufferVertex(vertices);
        cmd.SetBufferIndex(indices);
        foreach (DrawCall draw_call in s_draw_calls)
        {
            var pass = new PassBufferData { draw_index = draw_call.draw_index, material_index = draw_call.material_index };
            cmd.PushConstants(pass);
            cmd.DrawIndexed(draw_call.index_count, draw_call.index_offset, (int)draw_call.vertex_offset);
        }
    }

    private static void Pass_GBuffer(RHI_CommandList cmd)
    {
        RHI_Texture tex_color    = GetRenderTargetTexture(RenderTarget.gbuffer_color)!;
        RHI_Texture tex_normal   = GetRenderTargetTexture(RenderTarget.gbuffer_normal)!;
        RHI_Texture tex_material = GetRenderTargetTexture(RenderTarget.gbuffer_material)!;
        RHI_Texture tex_velocity = GetRenderTargetTexture(RenderTarget.gbuffer_velocity)!;
        RHI_Texture tex_emissive = GetRenderTargetTexture(RenderTarget.gbuffer_emissive)!;
        RHI_Texture tex_depth    = GetRenderTargetTexture(RenderTarget.gbuffer_depth)!;

        cmd.BeginMarker("g_buffer");
        {
            var pso = new RHI_PipelineState { name = "g_buffer" };
            pso.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.gbuffer_v)!);
            pso.SetShader(RHI_Shader_Type.Pixel, GetShader(Renderer_Shader.gbuffer_p)!);
            // spartan's standard Solid rasterizer, Off blend and ReadEqual depth states: only the prepass survivors
            // shade (Renderer_Passes_Geometry.cpp:1764), the depth is loaded
            pso.rasterizer_state    = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            pso.depth_stencil_state = new RHI_DepthStencilState(true, false, RHI_Comparison_Function.Equal);
            pso.blend_state         = new RHI_BlendState(false);
            pso.render_target_color_textures[0] = tex_color;
            pso.render_target_color_textures[1] = tex_normal;
            pso.render_target_color_textures[2] = tex_material;
            pso.render_target_color_textures[3] = tex_velocity;
            pso.render_target_color_textures[4] = tex_emissive;
            pso.render_target_depth_texture     = tex_depth;
            for (int i = 0; i < 5; i++)
                pso.clear_color[i] = new Phalanx.Math.Color(0.0f, 0.0f, 0.0f, 0.0f);
            pso.clear_depth = RHI_Definitions.rhi_depth_load; // the prepass cleared and wrote it
            cmd.SetPipelineState(pso);
            cmd.SetCullMode(RHI_CullMode.Back);
            SetStandardResources(cmd);

            DrawOpaque(cmd);
        }
        cmd.EndMarker();
    }

    private static void Pass_Resolve(RHI_CommandList cmd, RHI_Texture tex_out)
    {
        cmd.BeginMarker("resolve");
        {
            var pso = new RHI_PipelineState { name = "resolve" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.resolve_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, tex_out, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_color, GetRenderTargetTexture(RenderTarget.gbuffer_color)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_normal, GetRenderTargetTexture(RenderTarget.gbuffer_normal)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_depth, GetRenderTargetTexture(RenderTarget.gbuffer_depth)!);
            var pass = new PassBufferData();
            pass.Set(0, ResolveBackgroundColor);
            pass.Set(4, (float)GetLightCount()); // values[1].x
            cmd.PushConstants(pass);
            cmd.Dispatch((tex_out.GetWidth() + 7) / 8, (tex_out.GetHeight() + 7) / 8);
        }
        cmd.EndMarker();
    }

    // compute blit: vulkan can't blit depth to float, amd uav requires float
    private static void Pass_Blit(RHI_CommandList cmd, RHI_Texture tex_in, RHI_Texture tex_out)
    {
        cmd.BeginMarker("blit");
        {
            var pso = new RHI_PipelineState { name = "blit" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.blit_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, tex_out, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, tex_in);
            cmd.Dispatch((tex_out.GetWidth() + 7) / 8, (tex_out.GetHeight() + 7) / 8); // blit.comp is 8x8
        }
        cmd.EndMarker();
    }
}
