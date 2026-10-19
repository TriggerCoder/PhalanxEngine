using System.Numerics;
using Phalanx.Assets.Import;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Silk.NET.Windowing;

namespace Phalanx.Editor;

// --renderer-cube: spartan's standard cube through the renderer (geometry buffer, g-buffer, light, composition,
// tonemap, shadow maps), seen by a camera 3 m away and lit by a directional light; the cube turns so every face shows
// and casts its shadow on a ground plane (the standard quad, 20 m wide, 1 m under the cube).
// --texture <file> loads an image (ResourceCache / ImageImporter) as the cube's albedo; --point-light adds a bright
// point light at the camera; --time-of-day <0..1> puts the sun there (Light.SetTimeOfDay: 0.25 sunrise, 0.5 noon,
// 0.75 sunset), without it the sun keeps its fixed afternoon angle. The sky is the atmospheric skysphere.
// --layers <hide-cube|only-cube> exercises the render layers: the cube goes on layer 2 and the camera's cull mask
// leaves layer 2 out (the cube disappears, its shadow stays: the shadow atlas draws every caster) or keeps only layer 2
// (the ground disappears). --predraw <material|blink|invalid> puts a behaviour on the cube that logs its lifecycle and draws
// it blue through OnPreDraw (material), hides it in the view every other second and logs OnBecameVisible /
// OnBecameInvisible (blink), or overrides it with a destroyed material (invalid: the cube shows the illegal material).
// --material <none|destroyed|invalid|missing-texture|full> exercises the fallback materials: the cube has no material
// (the default one, white with spartan's grey checkerboard), its material is destroyed or has a NaN roughness (the
// illegal material, the sad face), its albedo texture has no pixels (the plain orange colour, one warning), or the
// material table holds only one material besides the reserved two (the cube keeps its own, the ground goes illegal).
internal static class RendererCubeDemo
{
    private sealed record Scene(Entity Cube);

    public enum Layers { Default, HideCube, OnlyCube }
    public enum PreDraw { None, Material, Blink, Invalid }
    public enum MaterialCase { Own, None, Destroyed, Invalid, MissingTexture, Full }

    /// <summary>The render layer, behaviour and fallback material switches of the demo (--layers, --predraw, --material).</summary>
    public sealed record ViewOptions(Layers Layers = Layers.Default, PreDraw PreDraw = PreDraw.None, MaterialCase Material = MaterialCase.Own);

    // the --predraw behaviour: a per view override in OnPreDraw and the lifecycle on the console
    private sealed class CubeBehaviour : Behaviour, IAwake, IOnEnable, IStart, IPreDraw, IOnBecameVisible, IOnDisable, IOnDestroy
    {
        private readonly PreDraw m_mode;
        private readonly Material m_blue = new();
        private readonly Material m_destroyed = new();
        private readonly System.Diagnostics.Stopwatch m_clock = System.Diagnostics.Stopwatch.StartNew();
        private bool m_logged_pre_draw;

        public CubeBehaviour(PreDraw mode)
        {
            m_mode = mode;
            m_blue.SetProperty(MaterialProperty.ColorR, 0.15f);
            m_blue.SetProperty(MaterialProperty.ColorG, 0.35f);
            m_blue.SetProperty(MaterialProperty.ColorB, 0.95f);
            m_blue.SetProperty(MaterialProperty.ColorA, 1.0f);
            m_destroyed.SetResourceName("predraw_destroyed");
            m_destroyed.Destroy();
        }

        public void Awake()     => Console.WriteLine("predraw: Awake");
        public void OnEnable()  => Console.WriteLine("predraw: OnEnable");
        public void Start()     => Console.WriteLine("predraw: Start");
        public void OnDisable() => Console.WriteLine("predraw: OnDisable");
        public void OnDestroy() => Console.WriteLine("predraw: OnDestroy");
        public void OnBecameVisible()   => Console.WriteLine("predraw: OnBecameVisible");
        public void OnBecameInvisible() => Console.WriteLine("predraw: OnBecameInvisible");

        public void OnPreDraw(in ViewContext view)
        {
            if (!m_logged_pre_draw)
            {
                Console.WriteLine($"predraw: OnPreDraw in view '{view.View.Name}'");
                m_logged_pre_draw = true;
            }
            if (m_mode == PreDraw.Material)
                view.SetMaterial(m_blue);
            else if (m_mode == PreDraw.Invalid)
                view.SetMaterial(m_destroyed);
            else if (m_mode == PreDraw.Blink && (int)m_clock.Elapsed.TotalSeconds % 2 == 1)
                view.SetVisible(false);
        }
    }

    // the scene is World entities: a camera, a directional light and the standard cube with a Render component
    private static Scene CreateScene(string? texture_path, bool point_light = false, float? time_of_day = null, ViewOptions? options = null)
    {
        World.World.SetMainThread();
        World.World.ClearScene();

        Entity camera_entity = World.World.CreateEntity();
        camera_entity.SetObjectName("camera");
        Camera camera = camera_entity.AddComponent<Camera>()!; // the camera constructor places its entity at (0, 3, -5)
        // sunny 16 (f/16, 1/125 s, iso 100) for the 120000 lux sun
        camera.SetAperture(16.0f);
        camera.SetShutterSpeed(1.0f / 125.0f);
        camera.SetIso(100.0f);
        camera_entity.SetPosition(new Vector3(0, 0.8f, -3));
        camera_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.0f, 0.3f, 0.0f)); // looking slightly down at the ground

        Entity light_entity = World.World.CreateEntity();
        light_entity.SetObjectName("light_directional");
        light_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
        Light light = light_entity.AddComponent<Light>()!;
        light.SetLightType(LightType.Directional);
        light.SetColor(new Phalanx.Math.Color(1.0f, 1.0f, 1.0f, 1.0f));
        if (time_of_day is float t)
            light.SetTimeOfDay(t); // the sun rises behind the camera and sets in front of it, in view

        if (point_light)
        {
            Entity point_entity = World.World.CreateEntity();
            point_entity.SetObjectName("light_point");
            point_entity.SetPosition(new Vector3(0.8f, 0.6f, -1.5f));
            Light point = point_entity.AddComponent<Light>()!;
            point.SetLightType(LightType.Point);
            point.SetColor(new Phalanx.Math.Color(1.0f, 0.85f, 0.6f, 1.0f));
            point.SetIntensity(2000000.0f); // lm, bright enough to show under sunny 16
        }

        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, 0.9f);
        material.SetProperty(MaterialProperty.ColorG, 0.55f);
        material.SetProperty(MaterialProperty.ColorB, 0.2f);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);
        if (texture_path != null)
        {
            if (ResourceCache.LoadTexture(texture_path) is RHI_Texture texture)
            {
                material.SetProperty(MaterialProperty.ColorR, 1.0f);
                material.SetProperty(MaterialProperty.ColorG, 1.0f);
                material.SetProperty(MaterialProperty.ColorB, 1.0f);
                material.SetTexture(MaterialTextureType.Color, texture);
            }
            else
                Console.Error.WriteLine($"renderer-cube: couldn't load '{texture_path}', using the plain colour");
        }

        options ??= new ViewOptions();
        material.SetResourceName("cube");
        if (options.Material == MaterialCase.MissingTexture)
            material.SetTexture(MaterialTextureType.Color, new RHI_Texture(16, 16)); // a size, no pixels
        Entity cube = World.World.CreateEntity();
        cube.SetObjectName("cube");
        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        if (options.Material != MaterialCase.None)
            render.SetMaterial(material);
        if (options.Material == MaterialCase.Destroyed)
            material.Destroy();
        else if (options.Material == MaterialCase.Invalid)
            material.SetProperty(MaterialProperty.Roughness, float.NaN);
        else if (options.Material == MaterialCase.Full)
            Renderer.s_material_capacity = 3; // the default, the illegal and the cube's, the ground's doesn't fit
        if (options.Layers != Layers.Default)
        {
            render.RenderLayers = 1u << 1; // layer 2
            camera.CullMask     = options.Layers == Layers.HideCube ? ~(1u << 1) : 1u << 1;
        }
        if (options.PreDraw != PreDraw.None)
            cube.AddBehaviour(new CubeBehaviour(options.PreDraw));

        // the ground: spartan's standard quad, a light grey material, under the turning cube's reach (half diagonal 0.87 m)
        var ground_material = new Material();
        ground_material.SetProperty(MaterialProperty.ColorR, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorG, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorB, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorA, 1.0f);
        ground_material.SetProperty(MaterialProperty.Roughness, 0.9f);
        ground_material.SetResourceName("ground");
        Entity ground = World.World.CreateEntity();
        ground.SetObjectName("ground");
        ground.SetPosition(new Vector3(0, -1.0f, 0));
        ground.SetScale(new Vector3(20.0f, 1.0f, 20.0f));
        Render ground_render = ground.AddComponent<Render>()!;
        ground_render.SetMesh(MeshType.Quad);
        ground_render.SetMaterial(ground_material);

        World.World.Tick(); // publish the entities (spartan: the world load commits them before the first frame)
        return new Scene(cube);
    }

    // Engine::Tick order: World::Tick (entities, camera) then Renderer::Tick
    private static void Animate(Scene scene, float time)
    {
        scene.Cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.6f + time * 0.8f, 0.45f + time * 0.3f, 0.0f));
        World.World.Tick();
    }

    public static int Run(IWindow window, int frames, bool validation, string? texture_path = null, bool point_light = false, float? time_of_day = null, ViewOptions? options = null)
    {
        Renderer.Initialize(window, validation);
        try
        {
            Scene scene = CreateScene(texture_path, point_light, time_of_day, options);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int presented = 0;
            while (!window.IsClosing && (frames <= 0 || presented < frames))
            {
                window.DoEvents();
                Animate(scene, (float)clock.Elapsed.TotalSeconds);
                if (Renderer.Tick())
                    presented++;
            }
            return presented;
        }
        finally
        {
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }

    // no window: one frame at size x size, returns frame_output's centre pixel (rgba16f)
    public static Vector4 RunOffscreen(uint size, bool validation, string? texture_path = null, bool point_light = false, float? time_of_day = null, ViewOptions? options = null)
    {
        Renderer.Initialize(size, size, validation);
        try
        {
            Scene scene = CreateScene(texture_path, point_light, time_of_day, options);
            Animate(scene, 0.0f);
            Renderer.Tick();
            return Renderer.ReadOutputPixel(size / 2, size / 2);
        }
        finally
        {
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }

    // no window: settles a few frames at w x h and writes frame_output (display encoded rgba16f) as an 8 bit png
    public static void RunOffscreenPng(string path, uint width, uint height, float time, bool validation, string? texture_path = null, bool point_light = false, float? time_of_day = null, ViewOptions? options = null)
    {
        Renderer.Initialize(width, height, validation);
        try
        {
            Scene scene = CreateScene(texture_path, point_light, time_of_day, options);
            for (int i = 0; i < 4; i++) // temporal history settles
            {
                Animate(scene, time);
                Renderer.Tick();
            }
            byte[] rgba = Renderer.ReadOutputRgba8();
            PngWriter.Write(path, width, height, rgba);
        }
        finally
        {
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }
}
