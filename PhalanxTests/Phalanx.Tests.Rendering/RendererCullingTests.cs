using System.Drawing;
using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// per view culling (Renderer.Culling.cs): the frustum, the render layers against the cull masks, OnPreDraw once per
// view that draws the entity, per view overrides, deferred changes and the visibility callbacks
public partial class RendererSceneTests
{
    private static Entity AddCube(Vector3 position, float scale = 1.0f, uint layers = 1u)
    {
        Entity entity = World.World.CreateEntity();
        entity.SetPosition(position);
        entity.SetScale(new Vector3(scale));
        Render render = entity.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        render.RenderLayers = layers;
        return entity;
    }

    private static Camera AddCamera(Vector3 position, float yaw)
    {
        Entity entity = World.World.CreateEntity();
        Camera camera = entity.AddComponent<Camera>()!;
        entity.SetPosition(position);
        entity.SetRotation(Quaternion.CreateFromYawPitchRoll(yaw, 0.0f, 0.0f));
        return camera;
    }

    private static Render RenderOf(Entity entity) => entity.GetComponent<Render>()!;
    private static bool Culled(SceneView view, Entity entity) => !view.State.Visible.ContainsKey(RenderOf(entity));
    private static bool Draws(SceneView view, Entity entity) =>
        view.State.DrawCalls.Exists(d => d.render == RenderOf(entity)) || view.State.DrawCallsAlpha.Exists(d => d.render == RenderOf(entity));

    // records every OnPreDraw: the view's name and index
    private sealed class PreDrawRecorder : Behaviour, IPreDraw
    {
        public readonly List<(string view, int index)> Calls = new();
        public Action<PreDrawRecorder, ViewContext>? Body;
        public bool InRenderCallback;

        public void OnPreDraw(in ViewContext view)
        {
            Assert.Same(Entity, view.Entity);
            Assert.Same(Entity.GetComponent<Render>(), view.Render);
            Assert.Same(view.View.GetCamera(), view.Camera);
            InRenderCallback = World.World.IsInRenderCallback;
            Calls.Add((view.View.Name, view.ViewIndex));
            Body?.Invoke(this, view);
        }
    }

    private delegate void Action<T1, T2>(T1 a, T2 b) where T2 : allows ref struct;

    private sealed class VisibilityRecorder : Behaviour, IOnBecameVisible
    {
        public readonly List<string> Log = new();
        public void OnBecameVisible()   => Log.Add("visible");
        public void OnBecameInvisible() => Log.Add("invisible");
    }

    [SkippableFact]
    public void Each_view_culls_against_its_camera_frustum_and_the_shadow_draws_keep_everything()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One); // the camera at z = -3 looking down +z, 90 degrees across
            Entity behind   = AddCube(new Vector3(0, 0, -10));
            Entity far_left = AddCube(new Vector3(-50, 0, 0));
            Entity straddle = AddCube(new Vector3(-4, 0, 0), scale: 4.0f);  // x -6..-2: the left plane is at x = -3 at z = 0
            Entity outside  = AddCube(new Vector3(-5.5f, 0, 0));            // x -6..-5 at z <= 0.5, the plane is at -3.5
            Frame(scene);

            SceneView main = Renderer.MainView;
            Assert.False(Culled(main, scene.cube));
            Assert.False(Culled(main, straddle));
            Assert.True(Culled(main, behind));
            Assert.True(Culled(main, far_left));
            Assert.True(Culled(main, outside));
            Assert.True(Draws(main, scene.cube));
            Assert.True(Draws(main, straddle));
            Assert.False(Draws(main, behind));
            Assert.Equal(2, main.State.DrawCalls.Count + main.State.DrawCallsAlpha.Count);
            // the shadow maps draw every renderable
            Assert.Equal(5, Renderer.FrameDrawCalls.Count);

            // moving the camera round brings the one behind in and the cube out, the same frame
            scene.camera.GetEntity()!.SetRotation(Quaternion.CreateFromYawPitchRoll(MathF.PI, 0.0f, 0.0f));
            Frame(scene);
            Assert.False(Culled(main, behind));
            Assert.True(Culled(main, scene.cube));
            // a cube moved after the world tick is culled where it is now
            behind.SetPosition(new Vector3(0, 0, 50));
            Assert.True(Renderer.Tick());
            Assert.True(Culled(main, behind));
        });
    }

    [SkippableFact]
    public void A_view_draws_a_renderable_only_when_its_layers_meet_the_view_and_camera_masks()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Render render = RenderOf(scene.cube);
            var recorder = scene.cube.AddBehaviour(new PreDrawRecorder());
            SceneView main = Renderer.MainView;
            Assert.Equal(1u, render.RenderLayers);
            Assert.Equal(uint.MaxValue, scene.camera.CullMask);
            Assert.Equal(uint.MaxValue, main.CullMask);
            try
            {
                (uint layers, uint view_mask, uint camera_mask, bool drawn)[] cases =
                {
                    (1u,         uint.MaxValue, uint.MaxValue, true),  // the defaults
                    (1u << 1,    uint.MaxValue, uint.MaxValue, true),  // any layer under an all mask
                    (1u << 1,    ~(1u << 1),    uint.MaxValue, false), // the view leaves layer 2 out
                    (1u << 1,    uint.MaxValue, ~(1u << 1),    false), // the camera does
                    (0b101u,     0b100u,        uint.MaxValue, true),  // several layers, one shared bit is enough
                    (0b101u,     uint.MaxValue, 0b001u,        true),
                    (0b101u,     0b010u,        uint.MaxValue, false), // none shared
                    (0b101u,     0b100u,        0b001u,        false), // the view and the camera masks are and'ed
                    (1u,         0u,            uint.MaxValue, false), // a zero view mask draws nothing
                    (1u,         uint.MaxValue, 0u,            false), // a zero camera mask too
                    (0u,         uint.MaxValue, uint.MaxValue, false), // a renderable on no layer is never drawn
                    (1u << 31,   1u << 31,      1u << 31,      true),  // the top layer
                };
                foreach (var c in cases)
                {
                    render.RenderLayers     = c.layers;
                    main.CullMask           = c.view_mask;
                    scene.camera.CullMask   = c.camera_mask;
                    recorder.Calls.Clear();
                    Frame(scene);
                    string label = $"layers {c.layers:x} view {c.view_mask:x} camera {c.camera_mask:x}";
                    Assert.True(c.drawn == Draws(main, scene.cube), label);
                    Assert.True(c.drawn == !Culled(main, scene.cube), label);
                    Assert.True((c.drawn ? 1 : 0) == recorder.Calls.Count, label); // no OnPreDraw for a masked view
                    Assert.Single(Renderer.FrameDrawCalls); // the shadow casters stay
                }
            }
            finally
            {
                main.CullMask = uint.MaxValue;
            }
        });
    }

    [SkippableFact]
    public void OnPreDraw_runs_once_per_view_that_draws_the_entity_and_never_for_culled_or_masked_views()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            var first    = scene.cube.AddBehaviour(new PreDrawRecorder());
            var second   = scene.cube.AddBehaviour(new PreDrawRecorder());
            var disabled = scene.cube.AddBehaviour(new PreDrawRecorder { Enabled = false });
            Camera away = AddCamera(new Vector3(0, 0, -3), MathF.PI); // looking away from the cube
            Camera back = AddCamera(new Vector3(0, 0, -6), 0.0f);     // further back, seeing it
            Frame(scene);

            // a normal frame: only the main view renders
            Assert.Equal(new[] { ("main", 0) }, first.Calls);
            Assert.Equal(new[] { ("main", 0) }, second.Calls);
            Assert.Empty(disabled.Calls);
            Assert.True(first.InRenderCallback);
            Frame(scene);
            Frame(scene);
            Assert.Equal(3, first.Calls.Count);

            var rect = new RectangleF(0, 0, 1, 1);
            SceneView away_view   = Renderer.AddView("away",   away, rect, 64, 64, 64, 64);
            SceneView masked_view = Renderer.AddView("masked", back, rect, 64, 64, 64, 64);
            SceneView back_view   = Renderer.AddView("back",   back, rect, 64, 64, 64, 64);
            masked_view.CullMask = ~1u;
            first.Calls.Clear();
            second.Calls.Clear();
            Renderer.PrepareViews(new[] { Renderer.MainView, away_view, masked_view, back_view });
            Assert.Equal(new[] { ("main", 0), ("back", 3) }, first.Calls);
            Assert.Equal(new[] { ("main", 0), ("back", 3) }, second.Calls);
            Assert.Empty(disabled.Calls);
            Assert.True(Culled(away_view, scene.cube));
            Assert.True(Culled(masked_view, scene.cube));
            Assert.False(Culled(back_view, scene.cube));

            // an entity without the callback costs nothing: it is culled but not called back
            Entity plain = AddCube(new Vector3(0.5f, 0, 0));
            World.World.Tick();
            first.Calls.Clear();
            Renderer.PrepareViews(new[] { Renderer.MainView });
            Assert.False(Culled(Renderer.MainView, plain));
            Assert.Single(first.Calls);
        });
    }

    [SkippableFact]
    public void Per_view_overrides_apply_to_their_view_and_frame_only()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.9f, 0.55f, 0.2f, 1.0f));
            var blue = new Material();
            blue.SetProperty(MaterialProperty.ColorR, 0.1f);
            blue.SetProperty(MaterialProperty.ColorG, 0.2f);
            blue.SetProperty(MaterialProperty.ColorB, 0.9f);
            blue.SetProperty(MaterialProperty.ColorA, 1.0f);
            bool hide_in_main = true;
            var recorder = scene.cube.AddBehaviour(new PreDrawRecorder
            {
                Body = (self, view) =>
                {
                    if (view.View.Name == "main" && hide_in_main)
                    {
                        view.SetVisible(false);
                        Assert.False(view.Visible);
                    }
                    if (view.View.Name == "tinted")
                    {
                        view.SetMaterial(blue);
                        view.SetUvTransform(new Vector2(2, 2), new Vector2(0.5f, 0));
                    }
                    Assert.True(view.View.Name == "main" || view.Visible); // a fresh state in every view
                    Assert.True(view.View.Name == "tinted" || view.MaterialOverride == null);
                },
            });
            Frame(scene);
            SceneView main = Renderer.MainView;
            Assert.False(Draws(main, scene.cube)); // hidden in the main view...
            Assert.Single(Renderer.FrameDrawCalls); // ...still a shadow caster
            uint shared_draw = Renderer.FrameDrawCalls[0].draw_index;

            var rect = new RectangleF(0, 0, 1, 1);
            SceneView tinted = Renderer.AddView("tinted", scene.camera, rect, 64, 64, 64, 64);
            SceneView plain  = Renderer.AddView("plain",  scene.camera, rect, 64, 64, 64, 64);
            Renderer.PrepareViews(new[] { main, tinted, plain });
            Renderer.BuildViewDrawLists(main.State);
            Renderer.BuildViewDrawLists(tinted.State);
            Renderer.BuildViewDrawLists(plain.State);
            Assert.False(Draws(main, scene.cube));
            Renderer.DrawCall tinted_draw = Assert.Single(tinted.State.DrawCalls);
            Renderer.DrawCall plain_draw  = Assert.Single(plain.State.DrawCalls);
            Assert.NotEqual(shared_draw, tinted_draw.draw_index); // its own draw data
            Assert.Equal(shared_draw, plain_draw.draw_index);     // untouched by the others' overrides

            // the next frame starts over: shown again once the behaviour stops hiding it
            hide_in_main = false;
            Frame(scene);
            Renderer.DrawCall main_draw = Assert.Single(main.State.DrawCalls);
            Assert.Equal(Renderer.FrameDrawCalls[0].draw_index, main_draw.draw_index);
            Vector4 orange = Renderer.ReadOutputPixel(Width / 2, Height / 2);
            Assert.True(orange.X > orange.Z, $"{orange}");

            // a material only an override uses goes into the material table, the main view draws the cube blue
            tinted.Dispose();
            plain.Dispose();
            recorder.Body = (self, view) => view.SetMaterial(blue);
            Frame(scene);
            main_draw = Assert.Single(main.State.DrawCalls);
            Assert.Equal(Renderer.GetMaterialIndex(blue), main_draw.material_index);
            Assert.NotEqual(Renderer.GetMaterialIndex(scene.material), main_draw.material_index);
            Vector4 tinted_pixel = Renderer.ReadOutputPixel(Width / 2, Height / 2);
            Assert.True(tinted_pixel.Z > tinted_pixel.X, $"{tinted_pixel}");

            // hiding in OnPreDraw looks like not drawing at all
            recorder.Body = (self, view) => view.SetVisible(false);
            Frame(scene);
            Vector4 hidden = Renderer.ReadOutputPixel(Width / 2, Height / 2);
            recorder.Body = null;
            RenderOf(scene.cube).RenderLayers = 0;
            Frame(scene);
            Assert.Equal(Renderer.ReadOutputPixel(Width / 2, Height / 2), hidden);
        });
    }

    [SkippableFact]
    public void Changes_made_in_OnPreDraw_are_deferred_to_the_next_world_tick()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Entity other = AddCube(new Vector3(0.5f, 0, 0));
            bool deferred_ran = false;
            var recorder = scene.cube.AddBehaviour(new PreDrawRecorder
            {
                Body = (self, view) =>
                {
                    self.Entity.SetPosition(self.Entity.GetPosition() + new Vector3(1, 0, 0));
                    other.SetActive(false);
                    view.Defer(() => deferred_ran = true);
                    self.Body = null;
                },
            });
            Frame(scene);
            Assert.Single(recorder.Calls);
            Assert.True(recorder.InRenderCallback);
            Assert.Equal(Vector3.Zero, scene.cube.GetPosition()); // the frame drew it where it was
            Assert.True(other.IsActive());
            Assert.False(deferred_ran);
            Assert.True(Draws(Renderer.MainView, other));

            World.World.Tick();
            Assert.Equal(new Vector3(1, 0, 0), scene.cube.GetPosition());
            Assert.False(other.IsActive());
            Assert.True(deferred_ran);
            Assert.True(Renderer.Tick());
            Assert.False(Draws(Renderer.MainView, other));
        });
    }

    [SkippableFact]
    public void OnBecameVisible_and_OnBecameInvisible_follow_the_union_of_the_views()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            var visibility = scene.cube.AddBehaviour(new VisibilityRecorder());
            Frame(scene);
            Frame(scene);
            Assert.Equal(new[] { "visible" }, visibility.Log);

            scene.cube.SetPosition(new Vector3(0, 0, -10)); // behind the camera
            Frame(scene);
            Assert.Equal(new[] { "visible", "invisible" }, visibility.Log);

            // seen by a second view only: visible
            Camera back = AddCamera(new Vector3(0, 0, -6), MathF.PI);
            World.World.Tick();
            World.World.Tick(); // a camera computes its matrices in its first tick
            SceneView back_view = Renderer.AddView("back", back, new RectangleF(0, 0, 1, 1), 64, 64, 64, 64);
            Renderer.PrepareViews(new[] { Renderer.MainView, back_view });
            Assert.Equal(new[] { "visible", "invisible", "visible" }, visibility.Log);
            Renderer.PrepareViews(new[] { Renderer.MainView });
            Assert.Equal(new[] { "visible", "invisible", "visible", "invisible" }, visibility.Log);

            // hidden by every view that culled it in is invisible too
            scene.cube.SetPosition(Vector3.Zero);
            var hider = scene.cube.AddBehaviour(new PreDrawRecorder { Body = (self, view) => view.SetVisible(false) });
            Frame(scene);
            Assert.Equal(4, visibility.Log.Count);
            hider.Enabled = false;
            Frame(scene);
            Assert.Equal("visible", visibility.Log[^1]);
        });
    }
}
