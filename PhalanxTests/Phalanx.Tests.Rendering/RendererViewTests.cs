using System.Drawing;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;

namespace Phalanx.Tests;

// the views (Renderer.Views.cs, Views/): the main view's lifetime, the render target pool and per view history resets
public partial class RendererSceneTests
{
    private static RHI_Texture Target(SceneView view, Renderer_RenderTarget type) => view.State.GetTarget(type)!;

    [SkippableFact]
    public void The_main_view_lives_from_initialize_to_shutdown_and_secondary_views_go_with_shutdown()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        SceneView main = Renderer.MainView;
        Assert.False(main.IsAlive);
        Assert.Same(main, Assert.Single(Renderer.Views));
        SceneView? second = null;
        Run(() =>
        {
            Assert.Same(main, Renderer.MainView);
            Assert.True(main.IsAlive);
            Assert.True(main.IsMain);
            Assert.Equal((Width, Height), main.RenderResolution);
            Assert.Equal((Width, Height), main.OutputResolution);
            Assert.Throws<InvalidOperationException>(() => Renderer.RemoveView(main));

            second = Renderer.AddView("second", null, new RectangleF(0.5f, 0.0f, 0.5f, 1.0f), 64, 64, 64, 64);
            Assert.Equal(new[] { main, second }, Renderer.Views);
            Assert.True(second.IsAlive);
            Assert.Equal(64u, Target(second, Renderer_RenderTarget.frame_output).GetWidth());
            Assert.Equal(64u, Target(second, Renderer_RenderTarget.gbuffer_depth).GetWidth());

            // a view isn't the current one outside a frame, the per view targets resolve to the main view
            CreateScene(new System.Numerics.Vector4(1, 1, 1, 1));
            World.World.Tick();
            Assert.True(Renderer.Tick());
            Assert.Null(Renderer.CurrentView);
            Assert.Same(Target(main, Renderer_RenderTarget.frame_output), Renderer.GetRenderTargetTexture(Renderer_RenderTarget.frame_output));
            // the shared targets don't belong to a view
            Assert.Null(main.State.GetTarget(Renderer_RenderTarget.shadow_atlas));
            Assert.NotNull(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.shadow_atlas));

            second.Dispose();
            Assert.False(second.IsAlive);
            Assert.Same(main, Assert.Single(Renderer.Views));
            second.Dispose(); // twice is fine

            second = Renderer.AddView("third", null, new RectangleF(0, 0, 1, 1), 64, 64, 64, 64);
        });
        Assert.False(main.IsAlive);
        Assert.False(second!.IsAlive);
        Assert.Same(main, Assert.Single(Renderer.Views));
        Assert.Equal(0, Renderer.GetRenderTargetPool().Count);
    }

    [SkippableFact]
    public void The_render_target_pool_shares_equal_transient_targets_and_never_shares_history()
    {
        Run(() =>
        {
            RenderTargetPool pool = Renderer.GetRenderTargetPool();
            var desc = new RenderTargetDesc(RHI_Texture_Type.Type2D, 32, 16, 1, 1, RHI_Format.R16G16B16A16_Float,
                                            RHI_Texture_Flags.RHI_Texture_Srv | RHI_Texture_Flags.RHI_Texture_Uav, "pool_test");
            int before = pool.Count;
            RHI_Texture a = pool.Acquire(desc, shareable: true);
            RHI_Texture b = pool.Acquire(desc, shareable: true);
            RHI_Texture c = pool.Acquire(desc, shareable: false);
            RHI_Texture d = pool.Acquire(desc with { Width = 64 }, shareable: true);
            Assert.Same(a, b);
            Assert.NotSame(a, c);
            Assert.NotSame(a, d);
            Assert.Equal(2, pool.GetReferenceCount(a));
            Assert.Equal(1, pool.GetReferenceCount(c));
            Assert.Equal(before + 3, pool.Count);
            Assert.Equal((32u, 16u, RHI_Format.R16G16B16A16_Float), (a.GetWidth(), a.GetHeight(), a.GetFormat()));

            pool.Release(a);
            Assert.Equal(1, pool.GetReferenceCount(b));
            pool.Release(b);
            Assert.Equal(0, pool.GetReferenceCount(a));
            // a new request after the last release gets a new texture
            RHI_Texture e = pool.Acquire(desc, shareable: true);
            Assert.NotSame(a, e);
            pool.Release(e);
            pool.Release(c);
            pool.Release(d);
            pool.Release(null);
            Assert.Equal(before, pool.Count);

            // a second view of the main view's size shares its g-buffer, light buffers and frame_render, but owns its
            // history, depth, contact shadows and output
            SceneView main   = Renderer.MainView;
            SceneView second = Renderer.AddView("second", null, new RectangleF(0, 0, 1, 1), Width, Height, Width, Height);
            foreach (Renderer_RenderTarget t in new[] { Renderer_RenderTarget.gbuffer_color, Renderer_RenderTarget.gbuffer_normal, Renderer_RenderTarget.gbuffer_material,
                                                         Renderer_RenderTarget.gbuffer_emissive, Renderer_RenderTarget.gbuffer_velocity, Renderer_RenderTarget.dlss_reactivity,
                                                         Renderer_RenderTarget.frame_render, Renderer_RenderTarget.light_diffuse, Renderer_RenderTarget.light_specular })
            {
                Assert.Same(Target(main, t), Target(second, t));
                Assert.Equal(2, pool.GetReferenceCount(Target(main, t)));
            }
            foreach (Renderer_RenderTarget t in new[] { Renderer_RenderTarget.gbuffer_depth, Renderer_RenderTarget.gbuffer_depth_previous, Renderer_RenderTarget.sss,
                                                         Renderer_RenderTarget.frame_output, Renderer_RenderTarget.frame_output_2, Renderer_RenderTarget.taau_history })
            {
                Assert.NotSame(Target(main, t), Target(second, t));
                Assert.Equal(1, pool.GetReferenceCount(Target(second, t)));
            }
            // the history formats (ringsfix2): rgba32f taau history and its ping-pong target, rgba16f light buffers
            Assert.Equal(RHI_Format.R32G32B32A32_Float, Target(second, Renderer_RenderTarget.taau_history).GetFormat());
            Assert.Equal(RHI_Format.R32G32B32A32_Float, Target(second, Renderer_RenderTarget.frame_output_2).GetFormat());
            Assert.Equal(RHI_Format.R16G16B16A16_Float, Target(second, Renderer_RenderTarget.light_diffuse).GetFormat());

            // a different size gets its own set; going away gives the shared set back to the main view alone
            Renderer.ResizeView(second, 64, 48, 64, 48);
            Assert.NotSame(Target(main, Renderer_RenderTarget.gbuffer_color), Target(second, Renderer_RenderTarget.gbuffer_color));
            Assert.Equal(1, pool.GetReferenceCount(Target(main, Renderer_RenderTarget.gbuffer_color)));
            Assert.Equal(64u, Target(second, Renderer_RenderTarget.light_diffuse).GetWidth());
            Assert.Equal(48u, Target(second, Renderer_RenderTarget.taau_history).GetHeight());
            int with_second = pool.Count;
            second.Dispose();
            Assert.Equal(with_second - 15, pool.Count); // 9 transient + 6 history targets
        });
    }

    [SkippableFact]
    public void A_camera_cut_reset_or_resize_of_one_view_leaves_the_other_views_history_alone()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new System.Numerics.Vector4(1, 1, 1, 1));
            SceneView main   = Renderer.MainView;
            SceneView second = Renderer.AddView("second", scene.camera, new RectangleF(0, 0, 1, 1), 64, 64, 64, 64);
            Frame(scene);
            Frame(scene);
            ViewHistory main_history = main.State.History;
            Assert.False(main_history.TaauResetPending);
            Assert.True(main_history.DepthHistoryCleared);
            // the second view isn't rendered yet: its history still waits for its first frame
            Assert.True(second.State.History.TaauResetPending);
            second.State.History.TaauResetPending = false;

            // a cut of the second view
            second.NotifyCameraCut();
            Frame(scene);
            Assert.True(second.State.History.CameraCutPending);
            Assert.False(main_history.CameraCutPending);
            Assert.False(Renderer.IsTaauHistoryResetPending());

            // a history reset of the second view
            second.ResetHistory();
            Assert.True(second.State.History.TaauResetPending);
            Assert.False(Renderer.IsTaauHistoryResetPending());
            second.State.History.TaauResetPending = false;

            // resizing the second view recreates its targets only
            RHI_Texture main_taau  = Target(main, Renderer_RenderTarget.taau_history);
            RHI_Texture main_depth = Target(main, Renderer_RenderTarget.gbuffer_depth_previous);
            RHI_Texture second_taau = Target(second, Renderer_RenderTarget.taau_history);
            Renderer.ResizeView(second, 96, 64, 96, 64);
            Assert.NotSame(second_taau, Target(second, Renderer_RenderTarget.taau_history));
            Assert.True(second.State.History.TaauResetPending);
            Assert.False(second.State.History.DepthHistoryCleared);
            Assert.True(second.State.History.SssNeedsClear);
            Assert.Same(main_taau, Target(main, Renderer_RenderTarget.taau_history));
            Assert.Same(main_depth, Target(main, Renderer_RenderTarget.gbuffer_depth_previous));
            Assert.False(Renderer.IsTaauHistoryResetPending());
            Assert.True(main_history.DepthHistoryCleared);

            // resizing the main view resets the main view only
            second.State.History.TaauResetPending = false;
            Renderer.SetResolutionRender(96, 72);
            Assert.True(Renderer.IsTaauHistoryResetPending());
            Assert.False(main_history.DepthHistoryCleared);
            Assert.False(second.State.History.TaauResetPending);
            Frame(scene);
            Assert.False(Renderer.IsTaauHistoryResetPending());

            // a cut of the main view is the main view's (Renderer.NotifyCameraCut)
            Renderer.NotifyCameraCut();
            Assert.True(main_history.CameraCutPending);
            Assert.True(second.State.History.CameraCutPending); // still waits for the second view's next frame
            Frame(scene);
            Assert.False(main_history.CameraCutPending);

            // the global reset and the switch to taau reset every view
            Renderer.ResetTaauHistory();
            Assert.True(main_history.TaauResetPending);
            Assert.True(second.State.History.TaauResetPending);
            Frame(scene);
            second.State.History.TaauResetPending = false;
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
            Assert.True(main_history.TaauResetPending);
            Assert.True(second.State.History.TaauResetPending);
            Frame(scene);
            Assert.False(main_history.TaauResetPending);
            second.Dispose();
        }, Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau);
    }

    [SkippableFact]
    public void The_directional_cascades_follow_the_main_view_camera_and_the_light_keeps_none()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new System.Numerics.Vector4(1, 1, 1, 1));
            Entity sun_entity = World.World.CreateEntity();
            sun_entity.SetRotation(System.Numerics.Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
            Light sun = sun_entity.AddComponent<Light>()!;
            sun.SetLightType(LightType.Directional);
            Frame(scene);
            System.Numerics.Matrix4x4 near = Renderer.GetDirectionalCascadeViewProjection(Renderer.MainView, sun, 0);
            Assert.NotEqual(System.Numerics.Matrix4x4.Identity, near);
            Assert.Equal(System.Numerics.Matrix4x4.Identity, sun.GetViewProjectionMatrix(0));
            // two cascades of the one rendered view in the atlas, owned by that view
            var slices = Renderer.GetShadowSlices().Where(s => s.light == sun).ToList();
            Assert.Equal(2, slices.Count);
            Assert.All(slices, s => Assert.Same(Renderer.MainView.State, s.view));
            Assert.Equal(sun.GetAtlasRectangle(0), Phalanx.Math.Rectangle.Zero);

            // the camera moves 30 m: the near cascade follows it
            scene.camera.GetEntity()!.SetPosition(new System.Numerics.Vector3(30, 0, -3));
            Frame(scene);
            Assert.NotEqual(near, Renderer.GetDirectionalCascadeViewProjection(Renderer.MainView, sun, 0));

            // a removed light's cascades go too
            World.World.RemoveEntity(sun_entity);
            Frame(scene);
            Assert.DoesNotContain(sun, Renderer.MainView.State.Cascades.Keys);
        });
    }
}
