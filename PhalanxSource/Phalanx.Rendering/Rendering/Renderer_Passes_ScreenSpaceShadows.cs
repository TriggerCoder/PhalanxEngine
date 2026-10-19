// Port of SpartanEngine's screen space shadows (Bend Studio's), source/rendering/Renderer_Passes_Lighting.cpp @ 4c07baa
// Pass_ScreenSpaceShadows (:1173-1275, the same at upstream 67a6e8b but for the stereo and view light helpers), the
// sss render target (Renderer_Resources.cpp:812, upstream :873) and the light table fields of UpdateLights
// (Renderer.cpp:3285-3304, upstream :3413-3432). The shader is screen_space_shadows/bend_sss.comp, the dispatch list
// is BendSss.BuildDispatchList (bend_sss_cpu.h), light.comp samples the result.
// Every directional light with shadows and ShadowsScreenSpace gets a slice of the 4 slice r16f target and a contact
// shadow from the depth buffer; point and spot lights never do, like spartan (the pass and the table both require
// LightType::Directional, Light::SetLightType clears the flag for the others).
// Deviations:
// - The pass runs on the graphics queue after the g-buffer (spartan: Pass_ComputeBatchB, Renderer.cpp:5393-5402,
//   upstream :5549-5558, on async compute), there is no async compute yet.
// - The slices are assigned in UpdateLights, before the light table is uploaded, and the pass dispatches that same
//   list. Spartan assigns them in the pass (Renderer_Passes_Lighting.cpp:1258) after UpdateLights has already written
//   the table (Renderer.cpp:3289, TickUploadBindlessDependencies :905 runs before ProduceFrame :924; upstream :3417,
//   :961, :976), so its table carries last frame's slice. One rule decides both the pass and table bit 4: directional,
//   Shadows, ShadowsScreenSpace, effective shadows (bit 3), a non zero intensity, a free slice and
//   Renderer.ScreenSpaceShadows. Spartan's pass checks the intensity and the slice count but not shadow
//   effectiveness, its table checks effectiveness but not the intensity or the slice count.
// - Renderer.ScreenSpaceShadows switches the pass for every light (spartan has no global switch, only the per light
//   flag); with it off no light gets bit 4 and the target is cleared to lit.
// - The target is cleared to lit (1) when it is first used and when no light qualifies any more, once, instead of
//   every frame (Renderer_Passes_Lighting.cpp:1210-1216): with the shared rule nothing reads a slice no light owns.
// - No stereo (xr) path. Every view has its own target and slices (spartan: one target, no secondary views), and
//   ViewFeatures.ScreenSpaceShadows turns them off for one view.
// - Spartan's "todo: why do we need to flip sign?" (Renderer_Passes_Lighting.cpp:1244): bend wants the direction
//   towards the light (bend_sss_cpu.h:59-60), an entity's forward is the direction the light travels, hence -forward.
// Spartan's tuning is kept (bend_sss.hlsl: 256 samples, 0.005 surface thickness, contrast 4). Below about 720p a ray
// can step over a thin occluder such as a wall facing the camera, so whether it hits flips from one row to the next
// and the contact shadow turns into lit and shadowed stripes; spartan does the same at that resolution.
using System.Numerics;
using Phalanx.Rendering.RHI;
using RenderTarget = Phalanx.World.Renderer_RenderTarget;
using Light = Phalanx.World.Light;
using LightType = Phalanx.World.LightType;
using LightFlags = Phalanx.World.LightFlags;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    /// <summary>Contact shadows from the depth buffer (Bend Studio's screen space shadows) for every directional light
    /// with shadows and <see cref="LightFlags.ShadowsScreenSpace"/> (on by default, like spartan's light flag). Off
    /// turns them off for every light.</summary>
    public static bool ScreenSpaceShadows { get; set; } = true;

    // Renderer_Resources.cpp:812, a slice per light
    internal const uint renderer_sss_slice_count = 4;

    // the directional lights with a contact shadow this frame in the main view, in slice order (UpdateLights fills
    // them per view: ViewState.SssLights, with the clear state in ViewHistory)
    internal static IReadOnlyList<Light> GetScreenSpaceShadowLights() => s_main_view.State.SssLights;

    // the shared rule of the pass (Renderer_Passes_Lighting.cpp:1230) and the light table (Renderer.cpp:3285-3287)
    private static bool WantsScreenSpaceShadows(ViewState view, Light light, bool shadows_effective) =>
        ScreenSpaceShadows                                &&
        (view.View.Features & ViewFeatures.ScreenSpaceShadows) != 0 &&
        light.GetLightType() == LightType.Directional     &&
        light.GetFlag(LightFlags.Shadows)                 &&
        light.GetFlag(LightFlags.ShadowsScreenSpace)      &&
        shadows_effective                                 &&
        light.GetIntensityRadiometric() != 0.0f;

    // UpdateLights: gives the light a slice of the view's target when it qualifies, the slice or -1
    private static int AssignScreenSpaceShadowSlice(ViewState view, Light light, bool shadows_effective)
    {
        if (!WantsScreenSpaceShadows(view, light, shadows_effective))
            return -1;
        if (view.SssLights.Count == renderer_sss_slice_count)
        {
            if (!view.SssSliceWarned)
                Log.LogWarning("Render target has reached the maximum number of lights it can hold"); // Renderer_Passes_Lighting.cpp:1237
            view.SssSliceWarned = true;
            return -1;
        }
        int slice = view.SssLights.Count;
        if (view.View.IsMain)
            light.SetScreenSpaceShadowsSliceIndex((uint)slice); // the light's own field (Light.h:247) holds the main view's
        view.SssLights.Add(light);
        return slice;
    }

    private static void Pass_ScreenSpaceShadows(RHI_CommandList cmd, ViewState view)
    {
        RHI_Texture tex_sss = GetRenderTargetTexture(RenderTarget.sss)!;

        cmd.BeginMarker("screen_space_shadows");
        {
            // lit until a light writes its slice, and lit again once the last light stops
            bool any_light = view.SssLights.Count > 0 && view.View.GetCamera() != null;
            if (view.History.SssNeedsClear || (!any_light && view.History.SssWritten))
            {
                cmd.ClearTexture(tex_sss, new Phalanx.Math.Color(1.0f, 1.0f, 1.0f, 1.0f));
                view.History.SssNeedsClear = false;
                view.History.SssWritten    = false;
            }
            if (!any_light)
            {
                cmd.EndMarker();
                return;
            }

            var pso = new RHI_PipelineState { name = "screen_space_shadows" };
            pso.SetShader(RHI_Shader_Type.Compute, GetShader(Renderer_Shader.sss_c_bend)!);
            cmd.SetPipelineState(pso);
            SetStandardResources(cmd);
            cmd.SetTexture((uint)Renderer_BindingsSrv.tex, GetRenderTargetTexture(RenderTarget.gbuffer_depth)!);
            cmd.SetTexture((uint)Renderer_BindingsUav.tex_sss, tex_sss, storage: true);

            // the camera's own view projection, without the taau jitter (spartan: GetViewCamera()->GetViewProjectionMatrix())
            Matrix4x4 view_projection = view.Cb.view_projection_unjittered;
            for (int slice = 0; slice < view.SssLights.Count; slice++)
            {
                Light light = view.SssLights[slice];
                // bend wants the direction towards the light, the entity's forward is the direction the light travels
                Vector4 p = Vector4.Transform(new Vector4(-light.GetEntity()!.GetForward(), 0.0f), view_projection);

                // the depth only covers the scaled subrect, the light projects into that viewport, the texel size stays
                // the full texture's (resolution_scale is 1, no dynamic resolution yet)
                int active_width  = (int)tex_sss.GetWidth();
                int active_height = (int)tex_sss.GetHeight();
                float[] in_light_projection  = { p.X, p.Y, p.Z, p.W };
                int[]   in_viewport_size     = { active_width, active_height };
                int[]   in_min_render_bounds = { 0, 0 };
                int[]   in_max_render_bounds = { active_width, active_height };
                BendSss.DispatchList dispatch_list = BendSss.BuildDispatchList(in_light_projection, in_viewport_size, in_min_render_bounds, in_max_render_bounds, false);

                var pass = new PassBufferData();
                pass.Set(0, new Vector4(dispatch_list.LightCoordinate_Shader[0], dispatch_list.LightCoordinate_Shader[1],
                                        dispatch_list.LightCoordinate_Shader[2], dispatch_list.LightCoordinate_Shader[3])); // pass_bend_sss::light_coordinate
                pass.Set(8, 1.0f);                                                                 // pass_bend_sss::near_depth, reverse-z
                pass.Set(9, 0.0f);                                                                 // pass_bend_sss::far_depth
                pass.Set(10, (uint)slice);                                                         // pass_bend_sss::array_slice
                pass.Set(6, new Vector2(1.0f / tex_sss.GetWidth(), 1.0f / tex_sss.GetHeight()));   // pass_bend_sss::inv_depth_texture_size

                for (int dispatch_index = 0; dispatch_index < dispatch_list.DispatchCount; dispatch_index++)
                {
                    BendSss.DispatchData dispatch = dispatch_list.Dispatch[dispatch_index];
                    pass.Set(4, new Vector2(dispatch.WaveOffset_Shader[0], dispatch.WaveOffset_Shader[1])); // pass_bend_sss::wave_offset
                    cmd.PushConstants(pass);
                    cmd.Dispatch((uint)dispatch.WaveCount[0], (uint)dispatch.WaveCount[1], (uint)dispatch.WaveCount[2]);
                }
            }
            cmd.InsertBarrierMemory(); // the light pass reads the slices
            view.History.SssWritten = true;
        }
        cmd.EndMarker();
    }
}
