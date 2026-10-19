using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// The device lost diagnostics of the stress mode: with the driver's checkpoints every draw and dispatch gets one,
// named after its pass, its index there and its instances, next to the begin and end of every pass. And the suspect
// switches (--serialize-frames, --descriptor-eviction, --instance-velocity) leave the frame as it was.
public partial class RendererSceneTests
{
    [SkippableFact]
    public void Checkpoints_name_every_draw_and_the_switches_keep_the_frame()
    {
        var checkpoints = new List<(string name, bool end)>();
        try
        {
            Run(() =>
            {
                Scene scene = CreateScene(Vector4.One);
                scene.cube.SetActive(false);
                Entity sun = World.World.CreateEntity();
                sun.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
                sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
                RenderBatch blue = RenderBatches.Get(MeshType.Cube, Colored(0.1f, 0.2f, 0.9f));
                EntityStore store = World.World.Store;
                for (int i = 0; i < 40; i++)
                    store.Spawn(Drawn, new EntitySpawn(new Vector3(-1.0f + 0.05f * i, -0.4f, 1.5f), blue) { Scale = new Vector3(0.1f) });
                Renderer.MaxInstancesPerDraw = 16;
                Frame(scene);
                Frame(scene);
                byte[] color = Read(Renderer_RenderTarget.gbuffer_color), depth = Read(Renderer_RenderTarget.gbuffer_depth);

                RHI_Device.CheckpointRecorder = (name, end) => { lock (checkpoints) checkpoints.Add((name, end)); };
                Frame(scene);
                RHI_Device.CheckpointRecorder = null;
                foreach (string pass in new[] { "depth_prepass", "g_buffer" })
                {
                    // 40 instances in pieces of 16 over consecutive slots: a+16, a+16+16, a+32+8
                    var draws = checkpoints.Where(c => c.name.StartsWith(pass + " draw ", StringComparison.Ordinal)).Select(c => c.name).ToList();
                    Assert.Equal(3, draws.Count);
                    var ranges = draws.Select(Instances).ToList();
                    Assert.Equal(new uint[] { 16, 16, 8 }, ranges.Select(r => r.count));
                    Assert.Equal(new[] { ranges[0].first, ranges[0].first + 16, ranges[0].first + 32 }, ranges.Select(r => r.first));
                    for (int i = 0; i < draws.Count; i++)
                        Assert.StartsWith($"{pass} draw {i} (instances ", draws[i]);
                    int begin = checkpoints.IndexOf((pass, false)), end = checkpoints.IndexOf((pass, true));
                    Assert.True(begin >= 0 && end > begin, $"{pass}: {begin} {end}");
                    Assert.All(draws, d => Assert.InRange(checkpoints.FindIndex(c => c.name == d), begin + 1, end - 1));
                }
                Assert.Contains(checkpoints, c => c.name.StartsWith("screen_space_shadows dispatch 0 (", StringComparison.Ordinal));
                Assert.Contains(checkpoints, c => c.name.StartsWith("light_image_based dispatch 0 (", StringComparison.Ordinal));

                // the switches: no overlap, no eviction, no previous instance buffer, the same g-buffer
                RenderDevice.SerializeFrames = true;
                RenderDevice.DescriptorSetEviction = false;
                Renderer.InstanceVelocity = false;
                Frame(scene);
                Frame(scene);
                Assert.True(RHI_Device.GetFrameWork(RHI_Device.FrameIndex - 1).IsComplete());
                Assert.Equal(color, Read(Renderer_RenderTarget.gbuffer_color));
                Assert.Equal(depth, Read(Renderer_RenderTarget.gbuffer_depth));
            });
        }
        finally
        {
            RHI_Device.CheckpointRecorder = null;
            RenderDevice.SerializeFrames = false;
            RenderDevice.DescriptorSetEviction = true;
            Renderer.InstanceVelocity = true;
            Renderer.MaxInstancesPerDraw = 0;
        }


        // "g_buffer draw 1 (instances 16+16)" -> (16, 16)
        static (uint first, uint count) Instances(string name)
        {
            string range = name[(name.IndexOf("(instances ", StringComparison.Ordinal) + 11)..].TrimEnd(')');
            string[] parts = range.Split('+');
            return (uint.Parse(parts[0]), uint.Parse(parts[1]));
        }
    }
}
