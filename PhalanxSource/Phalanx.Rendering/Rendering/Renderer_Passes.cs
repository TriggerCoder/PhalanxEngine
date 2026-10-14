// The renderer's passes so far. ProduceFrame follows the shape of SpartanEngine Renderer::ProduceFrame
// (Renderer.cpp:5504 @ 4c07baa): the scene renders into frame_render, the result lands in frame_output, which Tick
// blits to the back buffer. Pass_Blit is a port of Renderer_Passes_Post.cpp:590-619.
// Pass_TestPattern is not in spartan: it clears frame_render and draws a quad textured with a bindless material
// texture (every unset slot is the checkerboard), standing in for the geometry passes until the scene is wired up.
// Deviations: frame_render goes to frame_output through Pass_Blit, spartan's post process chain (tonemapping,
// upscaling, ...) does that job; the blit has no gpu timing (no profiler yet), it is a marker.
using System.Numerics;
using Phalanx.Rendering.RHI;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    // what Pass_TestPattern clears to and tints with (tests)
    internal static readonly Phalanx.Math.Color TestPatternClearColor = new(0.0f, 0.0f, 0.0f, 1.0f);
    internal static Vector4 TestPatternTint { get; set; } = Vector4.One;
    internal static uint TestPatternMaterialIndex { get; set; }

    private static void ProduceFrame(RHI_CommandList cmd)
    {
        RHI_Texture rt_render = GetRenderTargetTexture(RenderTarget.frame_render)!;
        RHI_Texture rt_output = GetRenderTargetTexture(RenderTarget.frame_output)!;

        Pass_TestPattern(cmd, rt_render);
        Pass_Blit(cmd, rt_render, rt_output);
    }

    private static void Pass_TestPattern(RHI_CommandList cmd, RHI_Texture tex_out)
    {
        cmd.BeginMarker("test_pattern");
        {
            var pso = new RHI_PipelineState { name = "test_pattern" };
            pso.SetShader(RHI_Shader_Type.Vertex, GetShader(Renderer_Shader.test_pattern_v)!);
            pso.SetShader(RHI_Shader_Type.Pixel, GetShader(Renderer_Shader.test_pattern_p)!);
            // spartan's standard Solid rasterizer, Off depth stencil and Off blend states (Renderer_Resources.cpp:373-404)
            pso.rasterizer_state    = new RHI_RasterizerState(RHI_PolygonMode.Solid, true);
            pso.depth_stencil_state = new RHI_DepthStencilState(false, false, RHI_Comparison_Function.Never);
            pso.blend_state         = new RHI_BlendState(false);
            pso.render_target_color_textures[0] = tex_out;
            pso.clear_color[0] = TestPatternClearColor;
            cmd.SetPipelineState(pso);
            cmd.SetCullMode(RHI_CullMode.None);
            SetStandardResources(cmd);

            var pass = new PassBufferData { material_index = TestPatternMaterialIndex };
            pass.Set(0, TestPatternTint);
            cmd.PushConstants(pass);
            cmd.Draw(6);
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
            cmd.SetTexture(0, tex_out, storage: true); // Renderer_BindingsUav::tex
            cmd.SetTexture(7, tex_in);                 // Renderer_BindingsSrv::tex
            cmd.Dispatch((tex_out.GetWidth() + 7) / 8, (tex_out.GetHeight() + 7) / 8); // blit.comp is 8x8
        }
        cmd.EndMarker();
    }
}
