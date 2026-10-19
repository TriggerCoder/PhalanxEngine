// The renderer's passes so far. ProduceFrame follows the shape of SpartanEngine Renderer::ProduceFrame
// (Renderer.cpp:5504 @ 4c07baa): the g-buffer fills from the scene, the result lands in frame_render and then
// frame_output, which Tick blits to the back buffer. Pass_GBuffer ports the cpu-driven draw loop of
// Renderer_Passes_Geometry.cpp (Pass_GBuffer), Pass_Blit is a port of Renderer_Passes_Post.cpp:590-619.
// Pass_Light and Pass_Light_Composition port Renderer_Passes_Lighting.cpp:1580-1652 (light.hlsl,
// light_composition.hlsl), Pass_ScreenSpaceShadows lives in Renderer_Passes_ScreenSpaceShadows.cs, the sky and
// Pass_Light_ImageBased live in Renderer_Passes_Sky.cs, Pass_Tonemap ports Renderer_Passes_Post.cpp:385-398
// (output.hlsl).
// After composition the upscaler takes frame_render to frame_output (Pass_AA_Upscale, taau or a linear blit, see
// Renderer_Passes_Post.cs) and the post process chain ping-pongs frame_output/frame_output_2, ending display ready in
// frame_output.
// Pass_Depth_Prepass ports both halves of Renderer_Passes_Geometry.cpp:1463: opaque draws without a pixel shader,
// then alpha tested draws with depth_prepass_alpha (discard below the alpha threshold), depth loaded, cull None
// (:1553-1570). The g-buffer shades only the prepass survivors with an equal depth test (ReadEqual, the state
// spartan's alpha half uses at :1764), its alpha half loads the colour targets and culls None like spartan's.
// Deviations: spartan's opaque indirect g-buffer keeps GreaterEqual (:1695) because its prepass and g-buffer run
// different mesh shaders that are not bit identical; Phalanx's prepass runs the g-buffer's own vertex shader module
// (spartan: a separate depth_prepass_v) with an invariant gl_Position, so the depths match by construction and the
// equal test is safe. The prepass culls back faces (spartan: cull None, its triangles are culled on the gpu before).
// No hi-z yet; no gpu-driven indirect path, culling or lods; the passes have no gpu timing (no profiler yet), they
// are markers.
using Phalanx.Geometry;
using System.Numerics;
using Phalanx.Rendering.RHI;
using Entity = Phalanx.World.Entity;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using Light = Phalanx.World.Light;
using LightType = Phalanx.World.LightType;
using Render = Phalanx.World.Render;
using RenderFlags = Phalanx.World.RenderFlags;
using Material = Phalanx.World.Material;
using MaterialTextureType = Phalanx.World.MaterialTextureType;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    /// <summary>spartan's r.tonemapping (default gran turismo 7, ConsoleVariables.cpp:188); gran turismo 7 and reinhard
    /// are ported, the others clamp like Max.</summary>
    public static Renderer_Tonemapping Tonemapping { get; set; } = Renderer_Tonemapping.GranTurismo7;

    /// <summary>Waits for the gpu and reads one pixel of frame_output (rgba16f, tonemapped and srgb encoded), for headless runs and tools.</summary>
    public static Vector4 ReadOutputPixel(uint x, uint y)
    {
        RHI_Device.QueueWaitAll();
        RHI_Texture output = GetRenderTargetTexture(RenderTarget.frame_output)!;
        byte[] bytes = RHI_Device.ReadbackTexture(output, 0, 0);
        int i = (int)(y * output.GetWidth() + x) * 8;
        float H(int c) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i + c * 2));
        return new Vector4(H(0), H(1), H(2), H(3));
    }

    /// <summary>frame_output (display encoded) as rgba8, clamped, row major from the top.</summary>
    public static byte[] ReadOutputRgba8()
    {
        RHI_Device.QueueWaitAll();
        RHI_Texture output = GetRenderTargetTexture(RenderTarget.frame_output)!;
        byte[] bytes = RHI_Device.ReadbackTexture(output, 0, 0);
        int count = (int)(output.GetWidth() * output.GetHeight());
        var rgba = new byte[count * 4];
        for (int i = 0; i < count * 4; i++)
        {
            float v = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, i * 2));
            rgba[i] = (byte)System.Math.Clamp((int)(v * 255.0f + 0.5f), 0, 255);
        }
        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = 255;
        return rgba;
    }

    private static void ProduceFrame(RHI_CommandList cmd)
    {
        RHI_Texture rt_render = GetRenderTargetTexture(RenderTarget.frame_render)!;

        Pass_ComputeSky(cmd);            // Pass_ComputeBatchA (Renderer.cpp:5364-5388), see Renderer_Passes_Sky.cs
        SwapDepthHistory();
        ClearDepthHistoryIfNeeded(cmd);
        Pass_Depth_Prepass(cmd);
        Pass_GBuffer(cmd);
        Entity.CommitTransformHistory(); // Renderer_Passes_Geometry.cpp:2022, after the opaque g-buffer
        Pass_ScreenSpaceShadows(cmd);    // Pass_ComputeBatchB (Renderer.cpp:5393-5402), see Renderer_Passes_ScreenSpaceShadows.cs
        Pass_ShadowMaps(cmd);            // Pass_GraphicsPhase2_ShadowsAndRT (Renderer.cpp:5431)
        Pass_Light(cmd);
        Pass_Light_Composition(cmd, rt_render);
        Pass_Light_ImageBased(cmd, rt_render);
        Pass_AA_Upscale(cmd);
        Pass_PostProcess(cmd);
    }

    // Renderer_Passes_Geometry.cpp:1463: the opaque half has no pixel shader, ReadWrite depth and clears the depth
    // (reverse-z 0); the alpha half (:1553-1570) discards with depth_prepass_alpha, loads the depth and culls None
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
            Draw(cmd, s_draw_calls);

            if (s_draw_calls_alpha.Count > 0)
            {
                var pso_alpha = new RHI_PipelineState { name = "depth_prepass_alpha" };
                pso_alpha.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.gbuffer_v)!);
                pso_alpha.SetShader(RHI_Shader_Type.Pixel, GetShader(Renderer_Shader.depth_prepass_alpha_p)!);
                pso_alpha.rasterizer_state            = pso.rasterizer_state;
                pso_alpha.depth_stencil_state         = pso.depth_stencil_state;
                pso_alpha.blend_state                 = pso.blend_state;
                pso_alpha.render_target_depth_texture = pso.render_target_depth_texture;
                pso_alpha.clear_depth                 = RHI_Definitions.rhi_depth_load;
                cmd.SetPipelineState(pso_alpha);
                cmd.SetCullMode(RHI_CullMode.None);
                SetStandardResources(cmd);
                Draw(cmd, s_draw_calls_alpha);
            }
        }
        cmd.EndMarker();
    }

    // the cpu draw loop both passes share
    private static void Draw(RHI_CommandList cmd, List<DrawCall> draw_calls)
    {
        RHI_Buffer? vertices = GeometryBuffer.GetVertexBuffer();
        RHI_Buffer? indices  = GeometryBuffer.GetIndexBuffer();
        if (vertices == null || indices == null || draw_calls.Count == 0)
            return;
        cmd.SetBufferVertex(vertices);
        cmd.SetBufferIndex(indices);
        foreach (DrawCall draw_call in draw_calls)
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
            Draw(cmd, s_draw_calls);

            // the alpha half loads what the opaque half wrote and shades the cutout survivors
            if (s_draw_calls_alpha.Count > 0)
            {
                for (int i = 0; i < 5; i++)
                    pso.clear_color[i] = RHI_Definitions.rhi_color_load;
                pso.name = "g_buffer_alpha";
                cmd.SetPipelineState(pso);
                cmd.SetCullMode(RHI_CullMode.None);
                SetStandardResources(cmd);
                Draw(cmd, s_draw_calls_alpha);
            }
        }
        cmd.EndMarker();
    }

    // Renderer_Passes_Geometry.cpp:528-838, the cpu path without batching (spartan's multi draw indirect batches are
    // an optimisation of the same draws): every slice of every light with shadows, its casters (CastsShadows, opaque,
    // in the slice frustum) depth only into the slice's atlas rectangle, the alpha tested ones with depth_light_alpha.
    // The atlas is cleared to 0 (reverse-z far) once.
    private static void Pass_ShadowMaps(RHI_CommandList cmd)
    {
        RHI_Texture atlas = GetRenderTargetTexture(RenderTarget.shadow_atlas)!;
        RHI_Buffer? vertices = GeometryBuffer.GetVertexBuffer();
        RHI_Buffer? indices  = GeometryBuffer.GetIndexBuffer();

        cmd.BeginMarker("shadow_maps");
        {
            // the bias is done in the shader (Renderer_Resources.cpp:385), the directional cascades clamp depth
            var rasterizer_directional = new RHI_RasterizerState(RHI_PolygonMode.Solid, false);
            var rasterizer_point_spot  = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            var depth_read_write       = new RHI_DepthStencilState(true, true, RHI_Comparison_Function.GreaterEqual);

            bool cleared = false;
            RHI_PipelineState Pso(RHI_RasterizerState rasterizer, bool alpha)
            {
                var pso = new RHI_PipelineState { name = alpha ? "shadow_maps_alpha" : "shadow_maps" };
                pso.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.depth_light_v)!);
                if (alpha)
                    pso.SetShader(RHI_Shader_Type.Pixel, GetShader(Renderer_Shader.depth_light_alpha_p)!);
                pso.rasterizer_state            = rasterizer;
                pso.depth_stencil_state         = depth_read_write;
                pso.blend_state                 = new RHI_BlendState(false);
                pso.render_target_depth_texture = atlas;
                pso.clear_depth                 = cleared ? RHI_Definitions.rhi_depth_load : 0.0f;
                cleared = true;
                return pso;
            }

            // an empty frame still clears, the light pass reads stale depth otherwise
            if (s_shadow_slices.Count == 0 || vertices == null || indices == null)
            {
                cmd.SetPipelineState(Pso(rasterizer_point_spot, false));
                SetStandardResources(cmd);
                cmd.EndMarker();
                return;
            }

            foreach (ShadowSlice slice in s_shadow_slices)
            {
                Light light = slice.light;
                if (!slice.rect.IsDefined() || light.GetIntensityRadiometric() == 0.0f)
                    continue;
                RHI_RasterizerState rasterizer = light.GetLightType() == LightType.Directional ? rasterizer_directional : rasterizer_point_spot;

                foreach (bool alpha in new[] { false, true })
                {
                    List<DrawCall> draws = alpha ? s_draw_calls_alpha : s_draw_calls;
                    bool pso_set = false;
                    foreach (DrawCall draw_call in draws)
                    {
                        Render render = draw_call.render!;
                        if (!render.HasFlag(RenderFlags.CastsShadows))
                            continue;
                        Material? material = GetRenderMaterial(render);
                        if (material == null || material.IsTransparent())
                            continue;
                        if (!light.IsInViewFrustum(render, slice.slice_index))
                            continue;

                        if (!pso_set)
                        {
                            cmd.SetPipelineState(Pso(rasterizer, alpha));
                            cmd.SetCullMode(alpha ? RHI_CullMode.None : RHI_CullMode.Back);
                            SetStandardResources(cmd);
                            cmd.SetViewport(new RHI_Viewport(slice.rect.X, slice.rect.Y, slice.rect.Width, slice.rect.Height));
                            cmd.SetScissorRectangle(slice.rect);
                            cmd.SetBufferVertex(vertices);
                            cmd.SetBufferIndex(indices);
                            pso_set = true;
                        }

                        var pass = new PassBufferData { draw_index = draw_call.draw_index, material_index = draw_call.material_index };
                        pass.Set(0, material.HasTextureOfType(MaterialTextureType.Color)); // pass_depth_light::has_albedo
                        pass.Set(1, light.GetIndex());                                    // pass_depth_light::light_index
                        pass.Set(2, slice.slice_index);                                   // pass_depth_light::array_index
                        cmd.PushConstants(pass);
                        cmd.DrawIndexed(draw_call.index_count, draw_call.index_offset, (int)draw_call.vertex_offset);
                    }
                }
            }

            if (!cleared)
            {
                cmd.SetPipelineState(Pso(rasterizer_point_spot, false));
                SetStandardResources(cmd);
            }
        }
        cmd.EndMarker();
    }

    // the g-buffer textures the lighting passes read (spartan: SetCommonTextures, Renderer.cpp:2870-2885)
    private static void SetCommonTextures(RHI_CommandList cmd)
    {
        cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_color,    GetRenderTargetTexture(RenderTarget.gbuffer_color)!);
        cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_normal,   GetRenderTargetTexture(RenderTarget.gbuffer_normal)!);
        cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_material, GetRenderTargetTexture(RenderTarget.gbuffer_material)!);
        cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_depth,    GetRenderTargetTexture(RenderTarget.gbuffer_depth)!);
        cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_emissive, GetRenderTargetTexture(RenderTarget.gbuffer_emissive)!);
    }

    // Renderer_Passes_Lighting.cpp:1580, the opaque pass: direct diffuse and specular of every light
    private static void Pass_Light(RHI_CommandList cmd)
    {
        RHI_Texture light_diffuse  = GetRenderTargetTexture(RenderTarget.light_diffuse)!;
        RHI_Texture light_specular = GetRenderTargetTexture(RenderTarget.light_specular)!;
        cmd.BeginMarker("light");
        {
            var pso = new RHI_PipelineState { name = "light" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.light_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            SetCommonTextures(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex,  light_diffuse,  storage: true);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex2, light_specular, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, GetRenderTargetTexture(RenderTarget.shadow_atlas)!); // Renderer_Passes_Lighting.cpp:1592
            cmd.SetTexture((uint)Renderer_BindingsUav.tex_sss, GetRenderTargetTexture(RenderTarget.sss)!, storage: true); // :1590
            var pass = new PassBufferData();
            pass.Set(0, (float)GetLightCount()); // values[0].x (spartan: buffer_frame.cluster_light_count)
            cmd.PushConstants(pass);
            cmd.Dispatch((light_diffuse.GetWidth() + 7) / 8, (light_diffuse.GetHeight() + 7) / 8);
            cmd.InsertBarrierMemory(); // the composition reads both, the rhi has no hazard tracking between dispatches yet
        }
        cmd.EndMarker();
    }

    // Renderer_Passes_Lighting.cpp:1622, the opaque pass
    private static void Pass_Light_Composition(RHI_CommandList cmd, RHI_Texture tex_out)
    {
        cmd.BeginMarker("light_composition");
        {
            var pso = new RHI_PipelineState { name = "light_composition" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.light_composition_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            SetCommonTextures(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, tex_out, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, GetRenderTargetTexture(RenderTarget.skysphere)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex3, GetRenderTargetTexture(RenderTarget.light_diffuse)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex4, GetRenderTargetTexture(RenderTarget.light_specular)!);
            cmd.Dispatch((tex_out.GetWidth() + 7) / 8, (tex_out.GetHeight() + 7) / 8);
            cmd.InsertBarrierMemory(); // light_image_based reads and adds onto tex_out
        }
        cmd.EndMarker();
    }

    // Renderer_Passes_Post.cpp:385, exposure, tonemapping and the display encode
    private static void Pass_Tonemap(RHI_CommandList cmd, RHI_Texture tex_in, RHI_Texture tex_out)
    {
        cmd.BeginMarker("tonemap");
        {
            var pso = new RHI_PipelineState { name = "tonemap" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.output_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, tex_out, storage: true);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, tex_in);
            var pass = new PassBufferData();
            pass.Set(0, (float)(uint)Tonemapping); // values[0].x
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
