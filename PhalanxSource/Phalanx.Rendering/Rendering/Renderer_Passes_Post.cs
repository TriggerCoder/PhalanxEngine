// Port of the anti-aliasing and upscaling passes of SpartanEngine source/rendering/Renderer_Passes_Post.cpp @ 4c07baa
// (upstream 67a6e8b differs only by the frame generation hook, :565-569 there): Pass_Upscaler_Reactivity (:402-439,
// dlss_reactivity.hlsl), Pass_AA_Upscale (:441-570, taau.hlsl and the linear blit) and the start of Pass_PostProcess
// (:298-307 and :216-235: the tonemapper ping-pongs frame_output into frame_output_2, which is copied back), plus the
// depth history of Renderer_Passes_Lighting.cpp:848-858 (Pass_ReSTIR_SwapGBufferHistory) and the one shot clear of
// Renderer_Passes_Geometry.cpp:1471-1479.
// Deviations:
// - The depth history swaps at the start of the frame, before the prepass (spartan: at the end of ProduceFrame,
//   Renderer.cpp:5713). The textures each pass sees are the same; after a frame gbuffer_depth holds that frame's
//   depth, so readbacks and tools see the current depth. Only the depth is swapped, the normal and velocity history
//   targets don't exist yet.
// - The reactivity mask runs only when taau resolves (spartan runs it every frame for taau, XeSS and DLSS); there are
//   no particles to add their coverage to it.
// - AA_Fxaa_Upscale_Linear isn't ported (fxaa/fxaa3_11.h), it blits like AA_Off_Upscale_Linear and warns once.
// - The linear upscale is the compute blit (blit.comp, a box filter when downscaling) instead of a hardware linear
//   blit; frame_output has one mip, so there is no Pass_Downscale for the refraction mips.
// - No stereo, secondary views or frame generation.
// - The history and frame_output_2 are rgba32f instead of spartan's rgba16f, a half float history freezes into contour
//   lines on smooth gradients (Renderer_Resources.cs explains it).
// - The light buffers that feed frame_render are rgba16f instead of r11g11b10, their steps turn into bands behind
//   moving shadows under taau (Renderer_Resources.cs).
using Phalanx.Rendering.RHI;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    // shared_taau.h, taau.comp uses the same numbers
    internal const uint TAAU_GROUP_X = 8;
    internal const uint TAAU_GROUP_Y = 8;

    private static bool s_fxaa_warned;

    // the dispatch of the taau pass: one 8x8 group per 8x8 output pixels (Renderer_Passes_Post.cpp:527-531)
    internal static (uint x, uint y) GetTaauDispatch(uint output_width, uint output_height) =>
        ((output_width + TAAU_GROUP_X - 1) / TAAU_GROUP_X, (output_height + TAAU_GROUP_Y - 1) / TAAU_GROUP_Y);

    // the depth written last frame becomes the previous depth, the prepass writes into the old previous
    private static void SwapDepthHistory(ViewState view) => view.History.Swap(RenderTarget.gbuffer_depth, RenderTarget.gbuffer_depth_previous);

    // new depth history must start at the near plane (reverse-z 1) so disocclusion fails closed
    // (Renderer_Passes_Geometry.cpp:1471-1479); a depth only render pass that clears and draws nothing
    private static void ClearDepthHistoryIfNeeded(RHI_CommandList cmd, ViewState view)
    {
        if (view.History.DepthHistoryCleared)
            return;
        RHI_Texture? depth_previous = GetRenderTargetTexture(RenderTarget.gbuffer_depth_previous);
        if (depth_previous != null)
        {
            var pso = new RHI_PipelineState { name = "depth_history_clear" };
            pso.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.gbuffer_v)!);
            pso.rasterizer_state            = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            pso.depth_stencil_state         = new RHI_DepthStencilState(true, true, RHI_Comparison_Function.Always);
            pso.blend_state                 = new RHI_BlendState(false);
            pso.render_target_depth_texture = depth_previous;
            pso.clear_depth                 = 1.0f;
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
        }
        view.History.DepthHistoryCleared = true;
    }

    // Renderer_Passes_Post.cpp:402, the shared temporal upscaler reactivity (disocclusion and motion edges)
    private static void Pass_Upscaler_Reactivity(RHI_CommandList cmd)
    {
        RHI_Texture tex_reactivity = GetRenderTargetTexture(RenderTarget.dlss_reactivity)!;
        cmd.BeginMarker("upscaler_reactivity");
        {
            var pso = new RHI_PipelineState { name = "upscaler_reactivity" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.dlss_reactivity_c)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            SetCommonTextures(cmd);
            cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_velocity, GetRenderTargetTexture(RenderTarget.gbuffer_velocity)!);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex3, GetRenderTargetTexture(RenderTarget.gbuffer_depth_previous)!);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex, tex_reactivity, storage: true);
            var pass = new PassBufferData();
            pass.Set(0, System.Math.Clamp(UpscalerReactivity, 0.0f, 1.0f)); // pass_dlss_reactivity::scale
            cmd.PushConstants(pass);
            cmd.Dispatch((tex_reactivity.GetWidth() + 7) / 8, (tex_reactivity.GetHeight() + 7) / 8);
        }
        cmd.EndMarker();
    }

    // Renderer_Passes_Post.cpp:441, frame_render (render resolution) to frame_output (output resolution)
    private static void Pass_AA_Upscale(RHI_CommandList cmd, ViewState view)
    {
        RHI_Texture tex_in  = GetRenderTargetTexture(RenderTarget.frame_render)!;
        RHI_Texture tex_out = GetRenderTargetTexture(RenderTarget.frame_output)!;

        Renderer_AntiAliasing_Upsampling method = AntiAliasingUpsampling;
        if (IsTaauActive(view.View))
        {
            Pass_Upscaler_Reactivity(cmd);

            cmd.BeginMarker("aa_upscale");
            {
                RHI_Texture tex_history = GetRenderTargetTexture(RenderTarget.taau_history)!;
                RHI_Texture tex_scratch = GetRenderTargetTexture(RenderTarget.frame_output_2)!;

                var pso = new RHI_PipelineState { name = "taau" };
                pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.taau_c)!);
                cmd.SetPipelineState(pso);
                SetStandardResources(cmd);
                SetCommonTextures(cmd);
                cmd.SetTexture((uint)Renderer_BindingsSrv.gbuffer_velocity, GetRenderTargetTexture(RenderTarget.gbuffer_velocity)!);
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex,  tex_history);
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex2, tex_in);
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex3, GetRenderTargetTexture(RenderTarget.gbuffer_depth_previous)!);
                cmd.SetTexture((uint)Renderer_BindingsSrv.tex4, GetRenderTargetTexture(RenderTarget.dlss_reactivity)!);
                cmd.SetTexture((uint)Renderer_BindingsUav.tex,  tex_out, storage: true);
                cmd.SetTexture((uint)Renderer_BindingsUav.tex2, tex_scratch, storage: true);
                var pass = new PassBufferData();
                pass.Set(0, view.History.TaauResetPending); // pass_taau::reset_history
                pass.Set(1, true);                 // pass_taau::write_history, mono
                cmd.PushConstants(pass);
                (uint x, uint y) = GetTaauDispatch(tex_out.GetWidth(), tex_out.GetHeight());
                cmd.Dispatch(x, y);

                // protect the new history from post-processing; the old history becomes scratch.
                view.History.Swap(RenderTarget.taau_history, RenderTarget.frame_output_2);
                view.History.TaauResetPending = false;
            }
            cmd.EndMarker();
            return;
        }

        if (method == Renderer_AntiAliasing_Upsampling.AA_Fxaa_Upscale_Linear && !s_fxaa_warned)
        {
            Log.LogWarning("FXAA isn't ported yet, the frame is upscaled linearly without anti-aliasing");
            s_fxaa_warned = true;
        }
        Pass_Blit(cmd, tex_in, tex_out);
    }

    // Renderer_Passes_Post.cpp:298, the passes ported so far: the tonemapper reads frame_output and writes
    // frame_output_2, the result is copied back into frame_output (:232-235)
    private static void Pass_PostProcess(RHI_CommandList cmd)
    {
        RHI_Texture rt_output   = GetRenderTargetTexture(RenderTarget.frame_output)!;
        RHI_Texture rt_output_2 = GetRenderTargetTexture(RenderTarget.frame_output_2)!;
        Pass_Tonemap(cmd, rt_output, rt_output_2);
        Pass_Blit(cmd, rt_output_2, rt_output);
    }
}
