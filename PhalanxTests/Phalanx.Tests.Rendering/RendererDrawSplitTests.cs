using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// Renderer.MaxInstancesPerDraw (--max-instances-per-draw) splits an instanced draw into several draw calls over the
// same slot range: the prepass, the g-buffer and the shadow maps come out byte for byte the same. And the per slice
// shadow numbers (InstancingStats.ShadowSlices) add up to the totals the stress line prints
public partial class RendererSceneTests
{
    [SkippableFact]
    public void Split_instanced_draws_render_the_same_frame_and_shadows()
    {
        try
        {
            Run(() =>
            {
                Scene scene = CreateScene(Vector4.One);
                scene.cube.SetActive(false);
                Entity sun = World.World.CreateEntity();
                sun.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
                sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
                RenderBatch blue  = RenderBatches.Get(MeshType.Cube, Colored(0.1f, 0.2f, 0.9f));
                RenderBatch green = RenderBatches.Get(MeshType.Cube, Colored(0.1f, 0.9f, 0.2f));
                EntityStore store = World.World.Store;
                for (int i = 0; i < 300; i++)
                {
                    var p = new Vector3(-1.5f + 0.01f * i, -0.6f + 0.004f * i, 1.0f + (i % 9) * 0.25f);
                    store.Spawn(Drawn, new EntitySpawn(p, i % 3 == 0 ? green : blue) { Scale = new Vector3(0.12f) });
                }
                Renderer.MaxInstancesPerDraw = 0;
                Frame(scene);
                Frame(scene);
                byte[] color = Read(Renderer_RenderTarget.gbuffer_color), depth = Read(Renderer_RenderTarget.gbuffer_depth);
                byte[] atlas = Read(Renderer_RenderTarget.shadow_atlas);
                uint largest = Renderer.MainView.State.Draws.Max(d => d.instance_count);
                Assert.True(largest > 100, $"{largest}");

                Renderer.MaxInstancesPerDraw = 7; // every batch in many pieces, the last one short
                Frame(scene);
                Frame(scene);
                Assert.Equal(color, Read(Renderer_RenderTarget.gbuffer_color));
                Assert.Equal(depth, Read(Renderer_RenderTarget.gbuffer_depth));
                Assert.Equal(atlas, Read(Renderer_RenderTarget.shadow_atlas));
                Assert.Null(Renderer.ValidateFrameDraws());

                // the shadow numbers: one entry per slice, the sums are the totals
                var slices = Renderer.InstancingStats.ShadowSlices;
                Assert.Equal(Renderer.GetShadowSlices().Count, slices.Count);
                Assert.Equal(Renderer.InstancingStats.ShadowInstances, slices.Sum(s => s.Drawn));
                Assert.Equal(Renderer.InstancingStats.ShadowSkippedSmall, slices.Sum(s => s.SkippedSubTexel));
                Assert.All(slices, s => Assert.Equal(LightType.Directional, s.Light));
                // the near cascade holds them all, drawn or (smaller than its texel at this atlas size) left out
                Assert.True(slices[0].Drawn + slices[0].SkippedSubTexel >= 300, $"{slices[0]}");
            });
        }
        finally
        {
            Renderer.MaxInstancesPerDraw = 0;
        }
    }
}
