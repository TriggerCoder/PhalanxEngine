// The renderer's passes so far. ProduceFrame follows the shape of SpartanEngine Renderer::ProduceFrame
// (Renderer.cpp:5504 @ 4c07baa): the g-buffer fills from the scene, the result lands in frame_render and then
// frame_output, which Tick blits to the back buffer. Pass_GBuffer ports the cpu-driven draw loop of
// Renderer_Passes_Geometry.cpp (Pass_GBuffer), Pass_Blit is a port of Renderer_Passes_Post.cpp:590-619.
// Pass_Resolve is not in spartan: it shades the g-buffer into frame_render, standing in for the light, composition
// and post process passes until they are ported.
// Deviations: no depth prepass, so the g-buffer draws with read/write reverse-z depth (spartan: an equal test against
// the prepass) and clears the depth itself; no gpu-driven indirect path, culling or lods; frame_render goes to
// frame_output through Pass_Blit; the passes have no gpu timing (no profiler yet), they are markers.
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

        Pass_GBuffer(cmd);
        Pass_Resolve(cmd, rt_render);
        Pass_Blit(cmd, rt_render, rt_output);
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
            // spartan's standard Solid rasterizer, ReadWrite (reverse-z, greater equal) depth and Off blend states
            pso.rasterizer_state    = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            pso.depth_stencil_state = new RHI_DepthStencilState(true, true, RHI_Comparison_Function.GreaterEqual);
            pso.blend_state         = new RHI_BlendState(false);
            pso.render_target_color_textures[0] = tex_color;
            pso.render_target_color_textures[1] = tex_normal;
            pso.render_target_color_textures[2] = tex_material;
            pso.render_target_color_textures[3] = tex_velocity;
            pso.render_target_color_textures[4] = tex_emissive;
            pso.render_target_depth_texture     = tex_depth;
            for (int i = 0; i < 5; i++)
                pso.clear_color[i] = new Phalanx.Math.Color(0.0f, 0.0f, 0.0f, 0.0f);
            pso.clear_depth = 0.0f; // reverse-z
            cmd.SetPipelineState(pso);
            cmd.SetCullMode(RHI_CullMode.Back);
            SetStandardResources(cmd);

            RHI_Buffer? vertices = GeometryBuffer.GetVertexBuffer();
            RHI_Buffer? indices  = GeometryBuffer.GetIndexBuffer();
            if (vertices != null && indices != null && s_renderables.Count > 0)
            {
                cmd.SetBufferVertex(vertices);
                cmd.SetBufferIndex(indices);
                foreach (Renderable r in s_renderables)
                {
                    if (!r.mesh.HasGpuBuffers())
                        continue;
                    Matrix4x4 transform = r.entity.GetMatrix();
                    Matrix4x4 transform_previous = r.has_previous ? r.transform_previous : transform;
                    r.transform_previous = transform;
                    r.has_previous       = true;

                    uint material_index = GetMaterialIndex(r.material);
                    uint draw_index = WriteDrawData(transform, transform_previous, material_index, 0);
                    if (draw_index == uint.MaxValue)
                        break;

                    for (int sub_mesh = 0; sub_mesh < r.mesh.GetSubMeshCount(); sub_mesh++)
                    {
                        MeshLod lod = r.mesh.GetLod(sub_mesh, 0);
                        var pass = new PassBufferData { draw_index = draw_index, material_index = material_index };
                        cmd.PushConstants(pass);
                        cmd.DrawIndexed(lod.index_count, r.mesh.GetGlobalIndexOffset() + lod.index_offset,
                                        (int)(r.mesh.GetGlobalVertexOffset() + lod.vertex_offset));
                    }
                }
            }
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
