using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Math;
using Phalanx.Rendering;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the views' contribution cull of store entities (Renderer.MinPixelSize, EntityStore.Culling.cs): sub-pixel entities
// aren't drawn nor called back, 0 draws them again, and an entity that comes back has the right previous transform
public partial class RendererSceneTests
{
    // a near cube and a 20 x 20 field of cubes far away, each well under a pixel of the 128 x 96 render
    private static (EntityId near, List<EntityId> far) SpawnNearAndFar(Scene scene)
    {
        scene.cube.SetActive(false);
        EntityStore store = World.World.Store;
        RenderBatch white = RenderBatches.Get(MeshType.Cube, Colored(0.9f, 0.9f, 0.9f));
        EntityId near = store.Spawn(Drawn, new EntitySpawn(Vector3.Zero, white));
        var far = new List<EntityId>();
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
                far.Add(store.Spawn(Drawn, new EntitySpawn(new Vector3(-30 + 3 * x, -22 + 2.2f * y, 80), white) { Scale = new Vector3(0.36f) }));
        return (near, far);
    }

    [SkippableFact]
    public void A_sub_pixel_store_entity_is_not_drawn_nor_called_back_and_0_draws_it_again()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            (EntityId near, List<EntityId> far) = SpawnNearAndFar(scene);
            EntityStore store = World.World.Store;
            var recorder = World.World.GetEntity(far[210])!.AddBehaviour(new PreDrawRecorder());
            var near_recorder = World.World.GetEntity(near)!.AddBehaviour(new PreDrawRecorder());
            Frame(scene);

            // every far one is in the frustum and under a pixel (spartan's measure, unjittered, render resolution)
            SceneView main = Renderer.MainView;
            Matrix4x4 vp = scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix();
            foreach (EntityId id in far)
            {
                Sphere sphere = store.GetCullSphere(id);
                float pixels = ScreenContribution.SpherePixels(vp, sphere.Center, sphere.Radius, new Vector2(Width, Height));
                Assert.InRange(pixels, 0.3f, 0.99f);
            }
            Assert.Equal(new[] { store.GetSlot(near) }, main.State.Instances.Slots.Take(main.State.Instances.Total).Concat(main.State.Instances.Extras.Select(e => e.slot)).Distinct());
            Assert.Equal(new Renderer.ViewInstanceStats(1, 400), Assert.Single(Renderer.InstancingStats.Views));
            Assert.Empty(recorder.Calls);             // not drawn: no OnPreDraw
            Assert.Equal(new[] { ("main", 0) }, near_recorder.Calls);
            byte[] culled = Read(Renderer_RenderTarget.gbuffer_color);

            Renderer.MinPixelSize = 0.0f;
            Frame(scene);
            Assert.Equal(new Renderer.ViewInstanceStats(401, 0), Assert.Single(Renderer.InstancingStats.Views));
            Assert.Equal(new[] { ("main", 0) }, recorder.Calls);
            byte[] drawn = Read(Renderer_RenderTarget.gbuffer_color);
            int covered = 0;
            for (int i = 0; i < culled.Length; i += 4)
                if (culled[i] != drawn[i] || culled[i + 1] != drawn[i + 1] || culled[i + 2] != drawn[i + 2])
                    covered++;
            Assert.InRange(covered, 10, 400); // some of the far ones cover a pixel center when drawn

            // the entities keep updating while they aren't drawn (enabled is not rendered)
            Renderer.MinPixelSize = 2.0f;
            store.SetLocalPosition(far[0], new Vector3(-30, -22, 81));
            Frame(scene);
            Assert.Equal(new Vector3(-30, -22, 81), store.GetSyncedWorldMatrix(far[0]).Translation);
            Assert.Equal(new Renderer.ViewInstanceStats(1, 400), Assert.Single(Renderer.InstancingStats.Views));
        });
    }

    [SkippableFact]
    public void A_store_entity_that_comes_back_into_a_view_has_the_velocity_of_its_last_frame_only()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            EntityStore store = World.World.Store;
            EntityId id = store.Spawn(Drawn, new EntitySpawn(Vector3.Zero, RenderBatches.Get(MeshType.Cube, Colored(0.5f, 0.5f, 0.5f))) { Scale = new Vector3(2) });
            Vector2 Velocity()
            {
                byte[] velocity = Read(Renderer_RenderTarget.gbuffer_velocity);
                int v = Index(Width / 2, Height / 2) * 8;
                return new Vector2(Half(velocity, v), Half(velocity, v + 2));
            }
            Frame(scene);
            Frame(scene);
            AssertStill(Velocity());

            // moved while out of the view (a threshold larger than the screen), then back while still: no velocity
            Renderer.MinPixelSize = 100000.0f;
            store.SetLocalPosition(id, new Vector3(0.2f, 0, 0));
            Frame(scene);
            Assert.Equal(new Renderer.ViewInstanceStats(0, 1), Assert.Single(Renderer.InstancingStats.Views));
            Frame(scene);
            Renderer.MinPixelSize = 2.0f;
            Frame(scene);
            Assert.Equal(new Renderer.ViewInstanceStats(1, 0), Assert.Single(Renderer.InstancingStats.Views));
            AssertStill(Velocity());

            // back while moving: the motion of that frame only, like a drawn one's
            store.SetLocalPosition(id, new Vector3(0.4f, 0, 0));
            Frame(scene);
            Vector2 drawn = Velocity();
            Frame(scene);
            Renderer.MinPixelSize = 100000.0f;
            Frame(scene);
            store.SetLocalPosition(id, new Vector3(0.6f, 0, 0));
            Renderer.MinPixelSize = 2.0f;
            Frame(scene);
            Vector2 back = Velocity();
            Assert.True(MathF.Abs(drawn.X) > 1e-3f && MathF.Abs(back.X - drawn.X) < MathF.Abs(drawn.X) * 0.05f, $"{back} vs {drawn}");
        });
    }

    [SkippableFact]
    public void A_sub_pixel_world_entity_is_not_drawn_nor_called_back_either()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One); // its cube, 3 m away, is drawn
            Entity tiny = AddCube(new Vector3(0.5f, 0.3f, 80.0f), 0.2f);
            var recorder = tiny.AddBehaviour(new PreDrawRecorder());
            World.World.Tick();
            Frame(scene);
            ViewState s = Renderer.MainView.State;
            Assert.Equal(1, s.WorldSkippedSubpixel);
            Assert.True(s.Visible.ContainsKey(scene.cube.GetComponent<Render>()!));
            Assert.False(s.Visible.ContainsKey(tiny.GetComponent<Render>()!));
            Assert.Empty(recorder.Calls);
            Assert.True(tiny.GetActive()); // still active and updating, only not drawn

            Renderer.MinPixelSize = 0.0f;
            Frame(scene);
            Assert.Equal(0, Renderer.MainView.State.WorldSkippedSubpixel);
            Assert.True(Renderer.MainView.State.Visible.ContainsKey(tiny.GetComponent<Render>()!));
            Assert.Equal(new[] { ("main", 0) }, recorder.Calls);
        });
    }
}
