using System.Numerics;
using System.Security.Cryptography;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the view refactor (views-m0) must not change a pixel: the cube demo scene (Phalanx.Editor's --renderer-cube at
// --time-of-day 0.45, with a point light) rendered offscreen for several frames, so the taau history, the depth
// history and the screen space shadows all carry over, hashed every frame and compared with the hashes the code before
// the refactor (renderer-sss, bc30f62) produced on lavapipe. Each case also resizes, cuts the camera or switches the
// anti-aliasing mid run, which resets the histories. Other gpus round differently, so the golden only applies to the
// device it was recorded on; elsewhere the test checks that two runs agree.
public partial class RendererSceneTests
{
    // recorded on bc30f62 with llvmpipe (see GoldenDevice); set PHALANX_RECORD_GOLDEN=1 to print new values
    private const string GoldenDevice = "llvmpipe (LLVM 19.1.7, 256 bits)";
    private static readonly Dictionary<string, string> s_view_golden = new()
    {
        ["AaOff"] = "9AC4F36DCEC8B468,0E2934A170CD283C,9043EE1698781890,A9A26B4DCBAB7AD9,59EE517AB0F5D07D,7356874300E02116,E3D507FF2810210E,FAEF71FCA9101843",
        ["TaauCameraCut"] = "907E398652EBA87A,50269451510F9EEF,D812CF7360B9DC39,32F9626D5A3928EA,4D32302B7FBAAB62,265A63F9CE7BC7D4,FC57D96BED229346,7305D57AF00D3FE5",
        ["TaauModeSwitch"] = "907E398652EBA87A,50269451510F9EEF,D812CF7360B9DC39,32F9626D5A3928EA,59EE517AB0F5D07D,7356874300E02116,962453776F9E4E7F,023E5962E62ADCD6",
        ["TaauNoSss"] = "232D284EBED31A4C,3D4FF076A38594B9,450AB61E8948B8E9,6B2D9BD364E6BC64,657372CBBF04D6BB,5E23CE5E426EEE19,2EACB03155F4DC81,BAAC87227787FD6B",
        ["TaauResize"] = "907E398652EBA87A,50269451510F9EEF,D812CF7360B9DC39,32F9626D5A3928EA,18BE27237A437730,D28719636E1093C7,42767BD9876DD01B,728AD5BE2388E64D",
        ["TaauSss"] = "907E398652EBA87A,50269451510F9EEF,D812CF7360B9DC39,32F9626D5A3928EA,29BECD5A91BD7C1C,B2CE772BC49A6A46,2750C7D67625FD60,629CBD0F5CA937A2",
    };

    private const uint GoldenWidth = 160, GoldenHeight = 96, GoldenFrames = 8;

    public enum GoldenCase { TaauSss, TaauNoSss, AaOff, TaauResize, TaauCameraCut, TaauModeSwitch }

    // the demo scene of Phalanx.Editor/RendererCubeDemo.cs
    private static Entity CreateDemoScene()
    {
        Entity camera_entity = World.World.CreateEntity();
        Camera camera = camera_entity.AddComponent<Camera>()!;
        camera.SetAperture(16.0f);
        camera.SetShutterSpeed(1.0f / 125.0f);
        camera.SetIso(100.0f);
        camera_entity.SetPosition(new Vector3(0, 0.8f, -3));
        camera_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.0f, 0.3f, 0.0f));

        Entity light_entity = World.World.CreateEntity();
        light_entity.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
        Light light = light_entity.AddComponent<Light>()!;
        light.SetLightType(LightType.Directional);
        light.SetColor(new Phalanx.Math.Color(1.0f, 1.0f, 1.0f, 1.0f));
        light.SetTimeOfDay(0.45f);

        Entity point_entity = World.World.CreateEntity();
        point_entity.SetPosition(new Vector3(0.8f, 0.6f, -1.5f));
        Light point = point_entity.AddComponent<Light>()!;
        point.SetLightType(LightType.Point);
        point.SetColor(new Phalanx.Math.Color(1.0f, 0.85f, 0.6f, 1.0f));
        point.SetIntensity(2000000.0f);

        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, 0.9f);
        material.SetProperty(MaterialProperty.ColorG, 0.55f);
        material.SetProperty(MaterialProperty.ColorB, 0.2f);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);
        Entity cube = World.World.CreateEntity();
        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        render.SetMaterial(material);

        var ground_material = new Material();
        ground_material.SetProperty(MaterialProperty.ColorR, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorG, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorB, 0.6f);
        ground_material.SetProperty(MaterialProperty.ColorA, 1.0f);
        ground_material.SetProperty(MaterialProperty.Roughness, 0.9f);
        Entity ground = World.World.CreateEntity();
        ground.SetPosition(new Vector3(0, -1.0f, 0));
        ground.SetScale(new Vector3(20.0f, 1.0f, 20.0f));
        Render ground_render = ground.AddComponent<Render>()!;
        ground_render.SetMesh(MeshType.Quad);
        ground_render.SetMaterial(ground_material);

        World.World.Tick();
        return cube;
    }

    private static string RenderGoldenCase(GoldenCase c, out string device)
    {
        var hashes = new List<string>();
        string device_name = "";
        Renderer_AntiAliasing_Upsampling aa = c == GoldenCase.AaOff ? Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear : Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;
        Run(() =>
        {
            Renderer.ScreenSpaceShadows = c != GoldenCase.TaauNoSss;
            Entity cube = CreateDemoScene();
            device_name = RHI_Device.GetPrimaryPhysicalDevice().Name;
            for (uint frame = 0; frame < GoldenFrames; frame++)
            {
                if (frame == 4)
                {
                    switch (c)
                    {
                        case GoldenCase.TaauResize:
                            Renderer.SetResolutionRender(128, 80);
                            break;
                        case GoldenCase.TaauCameraCut:
                            World.World.GetCamera()!.GetEntity()!.SetPosition(new Vector3(0.5f, 1.0f, -3.5f));
                            Renderer.NotifyCameraCut();
                            break;
                        case GoldenCase.TaauModeSwitch:
                            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
                            break;
                    }
                }
                if (frame == 6 && c == GoldenCase.TaauModeSwitch)
                    Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau;

                float time = frame * 0.05f;
                cube.SetRotation(Quaternion.CreateFromYawPitchRoll(0.6f + time * 0.8f, 0.45f + time * 0.3f, 0.0f));
                World.World.Tick();
                Assert.True(Renderer.Tick());

                var bytes = new List<byte>(Read(Renderer_RenderTarget.frame_output));
                if (Renderer.AntiAliasingUpsampling == Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau)
                    bytes.AddRange(Read(Renderer_RenderTarget.taau_history));
                bytes.AddRange(Read(Renderer_RenderTarget.light_diffuse));
                hashes.Add(Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16]);
            }
        }, aa, shadow_atlas: 2048, width: GoldenWidth, height: GoldenHeight);
        device = device_name;
        return string.Join(",", hashes);
    }

    [SkippableTheory]
    [InlineData(GoldenCase.TaauSss)]
    [InlineData(GoldenCase.TaauNoSss)]
    [InlineData(GoldenCase.AaOff)]
    [InlineData(GoldenCase.TaauResize)]
    [InlineData(GoldenCase.TaauCameraCut)]
    [InlineData(GoldenCase.TaauModeSwitch)]
    public void The_main_view_renders_the_demo_scene_bit_identically_to_before_the_view_refactor(GoldenCase c)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        string first = RenderGoldenCase(c, out string device);
        if (Environment.GetEnvironmentVariable("PHALANX_RECORD_GOLDEN") == "1")
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "phalanx_golden.txt"), $"        [\"{c}\"] = \"{first}\", // {device}\n");

        if (device.Contains(GoldenDevice, StringComparison.OrdinalIgnoreCase) && s_view_golden.TryGetValue(c.ToString(), out string? golden))
        {
            Assert.Equal(golden, first);
        }
        else
        {
            // another gpu: the golden doesn't apply, the renderer must still be deterministic
            Assert.Equal(first, RenderGoldenCase(c, out _));
        }
    }
}
