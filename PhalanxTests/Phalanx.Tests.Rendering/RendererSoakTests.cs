using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// lavapipe, validation with synchronization and gpu assisted checks: many frames of store entities spawned, destroyed,
// moved and rebatched while the instance buffers, the slot list and the draw data grow, every frame's draws checked on
// the cpu (Renderer.ValidateFrameDraws); and the device lost path (a simulated vkQueueSubmit2 failure)
public partial class RendererSceneTests
{
    // gpu assisted validation reports its own setup as internal warnings (the features it forces on, the 8 set
    // pipeline layouts it can't instrument): every other warning and every error fails
    private static void AssertCleanIgnoringGpuAvCoverage() =>
        Assert.True(!RHI_Device.ValidationMessages.Any(m => (m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal))
                                                             && !m.Contains("Internal Warning", StringComparison.Ordinal)),
                    string.Join("\n", RHI_Device.ValidationMessages));

    private static void RunValidated(Action body)
    {
        RenderDevice.Validation = RenderValidation.Synchronization | RenderValidation.GpuAssisted;
        try
        {
            // Run asserts no error or warning at its end: the soak checks with the gpu assisted coverage note filtered
            // out and clears the list before Run's own check
            Run(() =>
            {
                body();
                RHI_Device.QueueWaitAll();
                AssertCleanIgnoringGpuAvCoverage();
                RHI_Device.ClearValidationMessages();
            });
        }
        finally
        {
            RenderDevice.Validation = RenderValidation.None;
        }
    }

    [SkippableFact]
    public void Soak_spawn_destroy_churn_and_buffer_growth_stay_clean_under_sync_and_gpu_assisted_validation()
    {
        RunValidated(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            Entity sun = World.World.CreateEntity();
            sun.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
            sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
            RenderBatch[] batches = Enumerable.Range(0, 6).Select(i => RenderBatches.Get(MeshType.Cube, Colored(0.1f + 0.15f * i, 0.5f, 0.9f - 0.15f * i))).ToArray();
            EntityStore store = World.World.Store;
            var random = new Random(7);
            var live = new List<EntityId>();
            Vector3 Somewhere() => new((float)random.NextDouble() * 3.0f - 1.5f, (float)random.NextDouble() * 2.0f - 1.0f, (float)random.NextDouble() * 8.0f);
            uint draw_data_capacity = Renderer.DrawDataCapacity;
            uint most_slots = 0;
            int most_visible = 0;

            const int frames = 120;
            for (int frame = 0; frame < frames; frame++)
            {
                int spawn = frame < 60 ? 800 : 150;
                for (int i = 0; i < spawn; i++)
                    live.Add(store.Spawn(Drawn, new EntitySpawn(Somewhere(), batches[random.Next(batches.Length)]) { Scale = new Vector3(0.02f) }));
                int destroy = frame < 20 ? 0 : 300;
                for (int i = 0; i < destroy && live.Count > 0; i++)
                {
                    int at = random.Next(live.Count);
                    store.Destroy(live[at]);
                    live[at] = live[^1];
                    live.RemoveAt(live.Count - 1);
                }
                for (int i = 0; i < 200; i++)
                    store.SetLocalPosition(live[random.Next(live.Count)], Somewhere());
                if (frame % 25 == 10)
                    for (int i = 0; i < 100; i++)
                    {
                        EntityId id = live[random.Next(live.Count)];
                        store.SetRenderBatch(id, batches[random.Next(batches.Length)]);
                        store.SetEnabled(id, i % 3 != 0);
                    }
                if (frame == 90) // the draw data grows with frames in flight
                    Renderer.EnsureDrawDataCapacity(Renderer.DrawDataCapacity + 1);

                Frame(scene);
                Assert.Null(Renderer.ValidateFrameDraws());
                most_slots   = System.Math.Max(most_slots, store.SlotCount);
                most_visible = System.Math.Max(most_visible, Renderer.InstancingStats.VisibleInstances);
                if (frame % 20 == 19)
                {
                    RHI_Device.QueueWaitAll();
                    AssertCleanIgnoringGpuAvCoverage();
                }
            }

            // the instance buffers grew past two slot blocks, the slot list past its first 4096, the draw data doubled
            Assert.True(most_slots > 2 * EntityStore.SlotBlockSize, $"{most_slots}");
            Assert.True(most_visible > 4096, $"{most_visible}");
            Assert.Equal(draw_data_capacity * 2, Renderer.DrawDataCapacity);
            Vector4 albedo = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), Width / 2, Height / 2);
            Assert.True(albedo != Vector4.Zero, $"{albedo}");
        });
    }

    [SkippableFact]
    public void Soak_partial_movement_with_sun_and_point_shadows_stays_clean_and_the_gpu_instances_match()
    {
        RunValidated(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            scene.cube.SetActive(false);
            Entity sun = World.World.CreateEntity();
            sun.SetRotation(Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
            sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
            Entity point = World.World.CreateEntity();
            point.SetPosition(new Vector3(0.5f, 0.8f, 1.5f));
            Light point_light = point.AddComponent<Light>()!;
            point_light.SetLightType(LightType.Point);
            point_light.SetIntensity(20000.0f);
            RenderBatch[] batches = Enumerable.Range(0, 3).Select(i => RenderBatches.Get(MeshType.Cube, Colored(0.2f + 0.3f * i, 0.5f, 0.8f - 0.3f * i))).ToArray();
            EntityStore store = World.World.Store;
            var random = new Random(9);
            var live = new List<EntityId>();
            Vector3 Somewhere() => new((float)random.NextDouble() * 30.0f - 15.0f, (float)random.NextDouble() * 2.0f - 1.0f, (float)random.NextDouble() * 60.0f);
            for (int i = 0; i < 30000; i++)
                live.Add(store.Spawn(Drawn, new EntitySpawn(Somewhere(), batches[i % 3]) { Scale = new Vector3(0.005f + 0.1f * (float)random.NextDouble()) }));
            long uploaded_all = 0, uploaded_partial = 0;

            const int frames = 80;
            for (int frame = 0; frame < frames; frame++)
            {
                // a tenth moves, a contiguous run of slots that wanders over the first half: the rest stays put (and
                // stops moving); the churn replaces entities of the second half (their new slots are anywhere)
                int start = (frame * 1700) % 12000;
                for (int i = start; i < start + 3000; i++)
                    store.SetLocalPosition(live[i], store.GetLocalPosition(live[i]) + new Vector3(0.01f, 0, 0));
                if (frame % 10 == 5)
                    for (int i = 0; i < 200; i++)
                    {
                        int at = 15000 + random.Next(live.Count - 15000);
                        store.Destroy(live[at]);
                        live[at] = store.Spawn(Drawn, new EntitySpawn(Somewhere(), batches[random.Next(3)]) { Scale = new Vector3(0.05f) });
                    }
                if (frame == 60) // every one moves for a frame
                    foreach (EntityId id in live)
                        store.SetLocalRotation(id, Quaternion.CreateFromYawPitchRoll(0.1f * frame, 0.0f, 0.0f));
                Frame(scene);
                Assert.Null(Renderer.ValidateFrameDraws());
                if (frame == 60) uploaded_all = Renderer.InstancingStats.UploadedBytes;
                if (frame == 40) uploaded_partial = Renderer.InstancingStats.UploadedBytes;
                if (frame % 20 == 19)
                {
                    RHI_Device.QueueWaitAll();
                    AssertCleanIgnoringGpuAvCoverage();
                }
            }

            // the partial frames upload a fraction of the all moving one
            Assert.True(uploaded_partial > 0 && uploaded_partial * 3 < uploaded_all, $"{uploaded_partial} vs {uploaded_all}");
            Assert.True(Renderer.InstancingStats.ShadowSkippedSmall > 0, "the far cascade leaves out sub-texel casters");
            int count = (int)store.SlotCount;
            PackedTransform[] current = Renderer.ReadGpuInstances(previous: false, count);
            foreach (EntityId id in live)
            {
                uint slot = store.GetSlot(id);
                Assert.Equal(store.Instances[slot].pos_x, current[slot].pos_x);
                Assert.Equal(store.Instances[slot].rotation_xy, current[slot].rotation_xy);
            }
        });
    }

    [SkippableFact]
    public void A_lost_device_ends_the_frame_with_a_report_and_the_renderer_still_shuts_down()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Frame(scene);
            RHI_Device.QueueWaitAll(); // nothing in flight: the simulated loss leaves no work the shutdown would race
            RHI_Device.SimulateDeviceLostOnSubmit = true;
            try
            {
                World.World.Tick();
                RendererDeviceLostException lost = Assert.Throws<RendererDeviceLostException>(() => Renderer.Tick());
                Assert.True(RHI_Device.IsDeviceLost);
                Assert.Contains("GPU device lost", lost.Report, StringComparison.Ordinal);
                Assert.Contains("vkQueueSubmit2", lost.Report, StringComparison.Ordinal);
                Assert.Contains("frame", lost.Message, StringComparison.Ordinal);
                // the same report, written through to the disk next to log.txt
                string written = File.ReadAllText(RHI_Device.DeviceLostReportPath);
                Assert.Contains("GPU device lost", written, StringComparison.Ordinal);
                Assert.Contains("vkQueueSubmit2", written, StringComparison.Ordinal);
                // the renderer stays lost, the waits of the shutdown return
                Assert.Throws<RendererDeviceLostException>(() => Renderer.Tick());
                RHI_Device.QueueWaitAll();
            }
            finally
            {
                RHI_Device.SimulateDeviceLostOnSubmit = false;
            }
        });
        Assert.False(RHI_Device.IsDeviceLost); // the lost device went with the shutdown
    }

    // the shutdown after a loss never waits on the queues: a lost device fails vkQueueWaitIdle, which the 3080 runs
    // threw from RHI_Device.Destroy (the lost flag was cleared before the deletion queue flush waited)
    [SkippableFact]
    public void The_shutdown_after_a_lost_device_never_waits_on_the_queues()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Frame(scene);
            RHI_Device.QueueWaitAll();
            RHI_Device.SimulateDeviceLostOnSubmit = true;
            World.World.Tick();
            Assert.Throws<RendererDeviceLostException>(() => Renderer.Tick());
            RHI_Device.SimulateDeviceLostOnWait = true; // from here a queue wait fails like the lost device's
            // something to flush: the deletion queue waits for the queues before it destroys
            new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 4, ReadOnlySpan<byte>.Empty, mappable: true, "lost_device_flush").Dispose();
        });
        RHI_Device.SimulateDeviceLostOnWait = false;
        RHI_Device.SimulateDeviceLostOnSubmit = false;
        Assert.False(RHI_Device.IsDeviceLost);
    }

    [Fact]
    public void Device_checkpoints_encode_the_frame_marker_and_end()
    {
        ulong begin = RHI_Device.EncodeCheckpoint(0x1234_5678u, 42u, end: false);
        ulong end   = RHI_Device.EncodeCheckpoint(0x1_1234_5678u, 42u, end: true); // the frame keeps its low 32 bits
        Assert.Equal((0x1234_5678ul, 42u, false), RHI_Device.DecodeCheckpoint(begin));
        Assert.Equal((0x1234_5678ul, 42u, true), RHI_Device.DecodeCheckpoint(end));
        Assert.NotEqual(0ul, begin); // a zero marker is the driver's "none"
    }
}
