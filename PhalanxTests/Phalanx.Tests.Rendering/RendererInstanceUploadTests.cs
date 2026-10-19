using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the instance upload (Renderer.Instancing.cs UploadInstances): only the slot groups stamped since the device local
// buffer of the frame was last written go up, and both buffers hold what the shaders need, the frame's transforms and
// the previous frame's, also once an entity stopped (previous = current, no velocity left behind)
public partial class RendererSceneTests
{
    private static void AssertSame(PackedTransform expected, PackedTransform actual, string what) =>
        Assert.True(expected.pos_x == actual.pos_x && expected.pos_y == actual.pos_y && expected.pos_z == actual.pos_z &&
                    expected.rotation_xy == actual.rotation_xy && expected.rotation_zw == actual.rotation_zw &&
                    expected.scale_x == actual.scale_x && expected.scale_y == actual.scale_y && expected.scale_z == actual.scale_z,
                    $"{what}: ({expected.pos_x}, {expected.pos_y}, {expected.pos_z}) vs ({actual.pos_x}, {actual.pos_y}, {actual.pos_z})");

    [SkippableFact]
    public void Only_dirty_groups_upload_and_a_stopped_entity_gets_previous_equal_to_current()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            RenderBatch batch = RenderBatches.Get(MeshType.Cube, Colored(0.2f, 0.6f, 0.9f));
            EntityStore store = World.World.Store;
            var ids = new List<EntityId>();
            for (int i = 0; i < 5000; i++)
                ids.Add(store.Spawn(Drawn, new EntitySpawn(new Vector3(-1.5f + 0.0006f * i, 0.0f, 2.0f + (i % 7) * 0.3f), batch) { Scale = new Vector3(0.02f) }));
            int count = (int)store.SlotCount;
            Frame(scene);
            Frame(scene);
            Frame(scene);
            Assert.Equal(0, Renderer.InstancingStats.UploadRegions); // nothing moved for two frames: nothing to upload

            // one entity moves: one region of one slot group, the current buffer has it, the previous the old place
            EntityId moving = ids[2500];
            uint slot = store.GetSlot(moving);
            PackedTransform before = store.Instances[slot];
            store.SetLocalPosition(moving, new Vector3(0.5f, 0.25f, 3.0f));
            Frame(scene);
            Assert.Equal(1, Renderer.InstancingStats.UploadRegions);
            Assert.Equal((long)EntityStore.SlotGroupSize * System.Runtime.CompilerServices.Unsafe.SizeOf<PackedTransform>(), Renderer.InstancingStats.UploadedBytes);
            PackedTransform[] current = Renderer.ReadGpuInstances(previous: false, count);
            PackedTransform[] previous = Renderer.ReadGpuInstances(previous: true, count);
            for (int i = 0; i < count; i++)
                AssertSame(store.Instances[i], current[i], $"current slot {i}");
            AssertSame(before, previous[slot], "previous of the moved one");

            // it stops: the next frame's buffer catches up (its last write was two frames ago) and previous = current
            Frame(scene);
            Assert.Equal(1, Renderer.InstancingStats.UploadRegions);
            current  = Renderer.ReadGpuInstances(previous: false, count);
            previous = Renderer.ReadGpuInstances(previous: true, count);
            for (int i = 0; i < count; i++)
            {
                AssertSame(store.Instances[i], current[i], $"current slot {i}");
                AssertSame(store.Instances[i], previous[i], $"previous slot {i}");
            }
            Frame(scene);
            Assert.Equal(0, Renderer.InstancingStats.UploadRegions);
            Assert.Equal(0L, Renderer.InstancingStats.UploadedBytes);

            // a destroyed one's slot is refilled by the swap back: the moved row's group uploads, nothing goes stale
            store.Destroy(ids[10]);
            EntityId fresh = store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0.5f, 2.5f), batch) { Scale = new Vector3(0.03f) });
            Frame(scene);
            Frame(scene);
            count = (int)store.SlotCount;
            current  = Renderer.ReadGpuInstances(previous: false, count);
            previous = Renderer.ReadGpuInstances(previous: true, count);
            foreach (EntityId id in ids.Where(store.IsAlive).Append(fresh))
            {
                uint s = store.GetSlot(id);
                AssertSame(store.Instances[s], current[s], $"current of slot {s}");
                AssertSame(store.Instances[s], previous[s], $"previous of slot {s}");
            }
            Assert.Null(Renderer.ValidateFrameDraws());
        });
    }

    [SkippableFact]
    public void Gpu_pass_timing_reports_the_frame_and_its_top_level_passes()
    {
        RenderDevice.GpuPassTiming = true;
        try
        {
            Run(() =>
            {
                Scene scene = CreateScene(Vector4.One);
                var timings = RenderDevice.TakeGpuTimings(); // drops whatever an earlier test left
                for (int frame = 0; frame < 12; frame++)
                {
                    Frame(scene);
                    timings = RenderDevice.TakeGpuTimings();
                    if (timings.Frames > 0)
                        break;
                }
                Assert.True(timings.Frames > 0);
                Assert.True(timings.FrameMs > 0.0);
                string[] names = timings.Passes.Select(p => p.Pass).ToArray();
                Assert.Contains("g_buffer", names);
                Assert.Contains("light", names);
                Assert.Equal(names.Length, names.Distinct().Count());
                Assert.True(timings.Passes.Sum(p => p.Ms) <= timings.FrameMs * 1.05 + 0.01, $"{timings.Passes.Sum(p => p.Ms)} vs {timings.FrameMs}");
            });
        }
        finally
        {
            RenderDevice.GpuPassTiming = false;
            RenderDevice.TakeGpuTimings();
        }
    }
}
