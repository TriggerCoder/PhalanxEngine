using System.Drawing;
using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// instanced drawing (Renderer.Instancing.cs): store entities in one draw per batch per view, per view layers, OnPreDraw
// overrides as their own draws, the previous transform for the velocity, exact groups of world entities, the draw data growth
public partial class RendererSceneTests
{
    private static Material Colored(float r, float g, float b)
    {
        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, r);
        material.SetProperty(MaterialProperty.ColorG, g);
        material.SetProperty(MaterialProperty.ColorB, b);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);
        return material;
    }

    private static readonly EntityArchetype Drawn = EntityArchetype.Empty.Renderable();

    private static IEnumerable<Renderer.InstancedDraw> Packed(SceneView view) =>
        view.State.Draws.Concat(view.State.DrawsAlpha).Where(d => d.mode == 2u);

    [SkippableFact]
    public void Store_entities_are_drawn_in_one_instanced_draw_per_batch_and_view()
    {
        Run(() =>
        {
            Renderer.MinPixelSize = 0.0f; // the small cubes are about 2 pixels of the 64 x 64 view, at the contribution threshold
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            Entity sun = World.World.CreateEntity();
            sun.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
            sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
            RenderBatch blue  = RenderBatches.Get(MeshType.Cube, Colored(0.1f, 0.2f, 0.9f));
            RenderBatch green = RenderBatches.Get(MeshType.Cube, Colored(0.1f, 0.9f, 0.2f));
            EntityStore store = World.World.Store;
            store.Spawn(Drawn, new EntitySpawn(Vector3.Zero, blue));
            for (int i = 0; i < 40; i++)
            {
                var p = new Vector3(-2.0f + 0.1f * i, 1.0f, 1.0f);
                store.Spawn(Drawn, new EntitySpawn(p, i % 2 == 0 ? blue : green) { Scale = new Vector3(0.05f), Layers = i % 4 < 2 ? 1u : 2u });
                store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0, -20 - i), green)); // behind the camera
            }
            Frame(scene);

            SceneView main = Renderer.MainView;
            Renderer.InstancedDraw[] draws = Packed(main).ToArray();
            Assert.Equal(2, draws.Length);
            Assert.Equal(new uint[] { 21, 20 }, draws.Select(d => d.instance_count).ToArray()); // blue first (batch order)
            Assert.Empty(main.State.DrawCalls); // no class renderable left
            Vector4 albedo = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), Width / 2, Height / 2);
            Assert.True(albedo.Z > 0.6f && albedo.X < 0.3f, $"{albedo}");
            // the shadow slices cull the same store for their casters, whatever the layers
            Assert.True(Renderer.InstancingStats.ShadowInstances >= 41, $"{Renderer.InstancingStats.ShadowInstances}");

            // a second view on layer 2 only: its own lists, from the same culling pass
            SceneView layer_two = Renderer.AddView("layer2", scene.camera, new RectangleF(0, 0, 1, 1), 64, 64, 64, 64);
            layer_two.CullMask = 2u;
            Frame(scene);
            Renderer.PrepareViews(new[] { main, layer_two });
            Renderer.RebuildDrawCalls();
            Assert.Equal(new uint[] { 21, 20 }, Packed(main).Select(d => d.instance_count).ToArray());
            Assert.Equal(new uint[] { 10, 10 }, Packed(layer_two).Select(d => d.instance_count).ToArray());
            layer_two.Dispose();
        });
    }

    [SkippableFact]
    public void An_OnPreDraw_override_of_a_store_entity_is_its_own_draw_in_that_view_only()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            Material white = Colored(0.9f, 0.9f, 0.9f);
            Material blue = Colored(0.1f, 0.2f, 0.9f);
            RenderBatch batch = RenderBatches.Get(MeshType.Cube, white);
            EntityStore store = World.World.Store;
            EntityId center = store.Spawn(Drawn, new EntitySpawn(Vector3.Zero, batch));
            for (int i = 0; i < 9; i++)
                store.Spawn(Drawn, new EntitySpawn(new Vector3(-1.5f + 0.3f * i, 1.2f, 0), batch) { Scale = new Vector3(0.1f) });
            bool hide = false;
            var recorder = World.World.GetEntity(center)!.AddBehaviour(new PreDrawRecorder
            {
                Body = (self, view) =>
                {
                    if (view.View.Name == "tinted")
                        view.SetMaterial(blue);
                    if (hide && view.View.Name == "main")
                        view.SetVisible(false);
                },
            });
            SceneView main = Renderer.MainView;
            Frame(scene);
            Assert.Equal(new[] { ("main", 0) }, recorder.Calls);
            Assert.Equal(new uint[] { 10 }, Packed(main).Select(d => d.instance_count).ToArray()); // kept in the batch
            Vector4 albedo = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), Width / 2, Height / 2);
            Assert.True(albedo.X > 0.8f && albedo.Z > 0.8f, $"{albedo}");

            SceneView tinted = Renderer.AddView("tinted", scene.camera, new RectangleF(0, 0, 1, 1), 64, 64, 64, 64);
            void BothViews()
            {
                Frame(scene);
                Renderer.PrepareViews(new[] { main, tinted });
                Renderer.RebuildDrawCalls();
            }
            BothViews();
            Assert.Equal(new uint[] { 10 }, Packed(main).Select(d => d.instance_count).ToArray());
            Assert.Equal(new uint[] { 9 }, Packed(tinted).Select(d => d.instance_count).ToArray()); // out of the batch...
            Renderer.DrawCall own = Assert.Single(tinted.State.DrawCalls);                         // ...into its own draw
            Assert.Equal(Renderer.GetMaterialIndex(blue), own.material_index);
            Assert.Empty(main.State.DrawCalls);
            Assert.Single(tinted.State.PackedOverrides);

            // the main view's draw of it is gone when it hides there, the tinted view keeps its override
            hide = true;
            BothViews();
            Assert.Equal(new uint[] { 9 }, Packed(main).Select(d => d.instance_count).ToArray());
            Assert.Single(tinted.State.DrawCalls);
            tinted.Dispose();
            Frame(scene);
            Vector4 hidden = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), Width / 2, Height / 2);
            Assert.Equal(Vector4.Zero, hidden);
        });
    }

    private static void AssertStill(Vector2 velocity) => Assert.True(velocity.Length() < 1e-5f, $"{velocity}");

    [SkippableFact]
    public void A_moving_instance_has_velocity_and_a_new_or_still_one_has_none()
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
            AssertStill(Velocity()); // new: its previous transform is its current one
            Frame(scene);
            AssertStill(Velocity());
            store.SetLocalPosition(id, new Vector3(0.2f, 0, 0));
            Frame(scene);
            Vector2 moving = Velocity();
            Assert.True(MathF.Abs(moving.X) > 1e-3f && MathF.Abs(moving.Y) < MathF.Abs(moving.X) * 0.01f, $"{moving}");
            Frame(scene);
            AssertStill(Velocity()); // still again

            // a facade's override draw reads the previous transform back too
            World.World.GetEntity(id)!.AddBehaviour(new PreDrawRecorder { Body = (self, view) => view.SetMaterial(Colored(0.9f, 0.1f, 0.1f)) });
            Frame(scene);
            store.SetLocalPosition(id, new Vector3(0.4f, 0, 0));
            Frame(scene);
            Assert.Single(Renderer.MainView.State.DrawCalls);
            Vector2 overridden = Velocity();
            Assert.True(MathF.Abs(overridden.X - moving.X) < MathF.Abs(moving.X) * 0.05f, $"{overridden} vs {moving}");
        });
    }

    [SkippableFact]
    public void World_entities_of_one_mesh_and_material_share_an_instanced_draw()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.9f, 0.55f, 0.2f, 1.0f));
            Frame(scene);
            Vector4 alone = Renderer.ReadOutputPixel(Width / 2, Height / 2);
            Renderer.InstancedDraw single = Assert.Single(Renderer.MainView.State.Draws);
            Assert.Equal(0u, single.mode); // one entity: the plain draw of before
            for (int i = 0; i < 5; i++)
            {
                Entity other = AddCube(new Vector3(-2 + i, 1.5f, 2), 0.3f);
                other.GetComponent<Render>()!.SetMaterial(scene.material);
            }
            Frame(scene);
            Renderer.InstancedDraw group = Assert.Single(Renderer.MainView.State.Draws);
            Assert.Equal(1u, group.mode);
            Assert.Equal(6u, group.instance_count);
            Assert.Equal(alone, Renderer.ReadOutputPixel(Width / 2, Height / 2));
        });
    }

    [SkippableFact]
    public void The_draw_data_buffer_grows_instead_of_dropping_draws()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.9f, 0.55f, 0.2f, 1.0f));
            Frame(scene);
            Vector4 before = Renderer.ReadOutputPixel(Width / 2, Height / 2);
            uint capacity = Renderer.DrawDataCapacity;
            Renderer.EnsureDrawDataCapacity(capacity + 1);
            Assert.Equal(capacity * 2, Renderer.DrawDataCapacity);
            Frame(scene);
            Frame(scene);
            Assert.Equal(before, Renderer.ReadOutputPixel(Width / 2, Height / 2));
            Assert.Single(Renderer.MainView.State.Draws);
        });
    }
}
