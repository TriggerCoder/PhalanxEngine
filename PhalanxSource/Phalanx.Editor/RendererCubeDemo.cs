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
// point light at the camera.
internal static class RendererCubeDemo
{
    private sealed record Scene(Entity Cube);

    // the scene is World entities: a camera, a directional light and the standard cube with a Render component
    private static Scene CreateScene(string? texture_path, bool point_light = false)
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

        Entity cube = World.World.CreateEntity();
        cube.SetObjectName("cube");
        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        render.SetMaterial(material);

        // the ground: spartan's standard quad, a light grey material, under the turning cube's reach (half diagonal 0.87 m)
        var ground_material = new Material();
        ground_material.SetProperty(MaterialProperty.ColorR, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorG, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorB, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorA, 1.0f);
        ground_material.SetProperty(MaterialProperty.Roughness, 0.9f);
        Entity ground = World.World.CreateEntity();
        ground.SetObjectName("ground");
        ground.SetPosition(new Vector3(0, -1.0f, 0));
        ground.SetScale(new Vector3(20.0f, 1.0f, 20.0f));
        Render ground_render = ground.AddComponent<Render>()!;
        ground_render.SetMesh(MeshType.Quad);
        ground_render.SetMaterial(ground_material);

        Renderer.SkyFallbackNits     = new Vector3(4000.0f, 6000.0f, 10000.0f);
        Renderer.AmbientFallbackNits = new Vector3(2000.0f, 3000.0f, 5000.0f);
        World.World.Tick(); // publish the entities (spartan: the world load commits them before the first frame)
        return new Scene(cube);
    }

    // Engine::Tick order: World::Tick (entities, camera) then Renderer::Tick
    private static void Animate(Scene scene, float time)
    {
        scene.Cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.6f + time * 0.8f, 0.45f + time * 0.3f, 0.0f));
        World.World.Tick();
    }

    public static int Run(IWindow window, int frames, bool validation, string? texture_path = null, bool point_light = false)
    {
        Renderer.Initialize(window, validation);
        try
        {
            Scene scene = CreateScene(texture_path, point_light);
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
    public static Vector4 RunOffscreen(uint size, bool validation, string? texture_path = null, bool point_light = false)
    {
        Renderer.Initialize(size, size, validation);
        try
        {
            Scene scene = CreateScene(texture_path, point_light);
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
}
