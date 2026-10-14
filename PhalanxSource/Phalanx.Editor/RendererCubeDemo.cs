using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Silk.NET.Windowing;

namespace Phalanx.Editor;

// --renderer-cube: spartan's standard cube through the renderer (geometry buffer, g-buffer, resolve), seen by a
// camera 3 m away and lit by a directional light; the cube turns so every face shows.
internal static class RendererCubeDemo
{
    private sealed record Scene(Camera Camera, Entity Cube);

    private static Scene CreateScene()
    {
        var camera_entity = new Entity();
        var camera = new Camera(camera_entity);
        camera_entity.SetPosition(new Vector3(0, 0, -3));
        camera.Initialize();
        Renderer.SetViewCamera(camera);

        var light_entity = new Entity();
        light_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
        var light = new Light(light_entity);
        light.SetLightType(LightType.Directional);
        light.SetColor(new Phalanx.Math.Color(1.0f, 1.0f, 1.0f, 1.0f));
        Renderer.AddLight(light);

        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, 0.9f);
        material.SetProperty(MaterialProperty.ColorG, 0.55f);
        material.SetProperty(MaterialProperty.ColorB, 0.2f);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);

        var cube = new Entity();
        Renderer.AddRenderable(Renderer.GetStandardMesh(MeshType.Cube)!, cube, material);
        Renderer.ResolveBackgroundColor = new Vector4(0.08f, 0.09f, 0.12f, 1.0f);
        return new Scene(camera, cube);
    }

    private static void Animate(Scene scene, float time)
    {
        scene.Cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.6f + time * 0.8f, 0.45f + time * 0.3f, 0.0f));
        scene.Camera.Tick();
    }

    public static int Run(IWindow window, int frames, bool validation)
    {
        Renderer.Initialize(window, validation);
        try
        {
            Scene scene = CreateScene();
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
        }
    }

    // no window: one frame at size x size, returns frame_output's centre pixel (rgba16f)
    public static Vector4 RunOffscreen(uint size, bool validation)
    {
        Renderer.Initialize(size, size, validation);
        try
        {
            Scene scene = CreateScene();
            Animate(scene, 0.0f);
            Renderer.Tick();
            return Renderer.ReadOutputPixel(size / 2, size / 2);
        }
        finally
        {
            Renderer.Shutdown();
        }
    }
}
