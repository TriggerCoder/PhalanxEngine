using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;

namespace Phalanx.Bench;

// The cpu benchmark of the entity store (PhalanxEngine-entities-s1.md, measured numbers): no gpu, the world's store,
// the stress mode's three batched behaviours, the transform pass, the per view culling, structural changes and the
// class side (behaviour dispatch, the incremental world lists), and the per view culling again with the views' 2 pixel
// contribution cull (PhalanxEngine-entities-s3-subpixel.md). Run it in Release:
//   dotnet run -c Release --project PhalanxTests/Phalanx.Bench [-- counts...]   (default 10000 100000 1000000 2000000)
// The second table (PhalanxEngine-entities-s2.md) is the shadow culling of the stress mode's lights, two sun cascades
// and six point light faces, with the s1 settings (the cascades' side planes only, no range, no size threshold, the
// lists finished one after another) and the s2 ones, and the transforms and dirty instance groups when a tenth moves.
// Every timing is the median of several runs, in milliseconds.
public static class Program
{
    private struct Spin : IComponentData { public Vector3 Axis; public float Speed; }
    private struct Orbit : IComponentData { public Vector3 Center; public float Radius, Speed, Phase; }
    private struct Bob : IComponentData { public float Height, Speed, Phase; }

    // the same work per entity as EntitiesDemo's behaviours
    private sealed class SpinBehaviour : BatchedBehaviour<Spin>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Spin> spins = chunk.Get<Spin>();
            Span<Quaternion> rotations = chunk.Rotations;
            float dt = context.DeltaTime;
            for (int i = 0; i < chunk.Count; i++)
                rotations[i] = Quaternion.Normalize(rotations[i] * Quaternion.CreateFromAxisAngle(spins[i].Axis, spins[i].Speed * dt));
        }
    }

    private sealed class OrbitBehaviour : BatchedBehaviour<Orbit>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Orbit> orbits = chunk.Get<Orbit>();
            Span<Vector3> positions = chunk.Positions;
            float time = (float)context.Time;
            for (int i = 0; i < chunk.Count; i++)
            {
                Orbit o = orbits[i];
                float angle = o.Phase + o.Speed * time;
                positions[i] = new Vector3(o.Center.X + o.Radius * MathF.Cos(angle), positions[i].Y, o.Center.Z + o.Radius * MathF.Sin(angle));
            }
        }
    }

    private sealed class BobBehaviour : BatchedBehaviour<Bob>, IBatchedLateUpdate
    {
        public void LateUpdate(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Bob> bobs = chunk.Get<Bob>();
            Span<Vector3> positions = chunk.Positions;
            float time = (float)context.Time;
            for (int i = 0; i < chunk.Count; i++)
                positions[i].Y = bobs[i].Height * MathF.Sin(bobs[i].Phase + bobs[i].Speed * time);
        }
    }

    private sealed class Counter : Behaviour, IUpdate
    {
        public int Updates;
        public void Update() => Updates++;
    }

    private static double Median(int runs, Action body)
    {
        var samples = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            body();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[runs / 2];
    }

    private static string F(double value) => value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    public static int Main(string[] args)
    {
        int[] counts = args.Length > 0 ? args.Select(a => int.Parse(a, CultureInfo.InvariantCulture)).ToArray() : new[] { 10000, 100000, 1000000, 2000000 };
        World.World.SetMainThread();
        Console.WriteLine($"Phalanx.Bench: {Environment.ProcessorCount} logical cores, {RuntimeInformation()}, Vector256 {System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}");
        Console.WriteLine("entities | spawn ms | bytes/entity | update+late ms (3 behaviours) | rotate only ms | transforms ms | tick ms | allocated B/tick | hierarchy sync ms (25% children) | cull 1/2/4 views ms | with the 2 px contribution cull ms | destroy+spawn 10k ms");
        foreach (int count in counts)
            RunStore(count);
        Console.WriteLine("entities | shadow cull s1 ms (casters) | shadow cull s2 ms (casters) | transforms 10% moving ms | dirty groups 10% moving MB | all moving MB");
        foreach (int count in counts)
            RunShadowsAndPartial(count);
        RunClassSide();
        return 0;
    }

    // the stress mode's field and lights: the sun's two cascades (DirectionalCascades: +-64 m and +-2000 m, 10000 m
    // deep, 2000 m behind the camera) and the point light's six faces; slices of about 2720 texels (an 8192 atlas of 8)
    private static void RunShadowsAndPartial(int count)
    {
        World.World.ClearScene();
        EntityStore store = World.World.Store;
        var random = new Random(1);
        RenderBatch[] batches = Enumerable.Range(0, 4).Select(_ => RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)))).ToArray();
        EntityArchetype moving = EntityArchetype.Empty.Renderable().With<Spin>().With<Orbit>().With<Bob>();
        EntityArchetype still = EntityArchetype.Empty.Renderable();
        float field = MathF.Max(40.0f, MathF.Sqrt(count) * 1.1f);
        int moving_count = count / 10;
        int side = (int)MathF.Ceiling(MathF.Sqrt(count));
        float spacing = field / side;
        for (int i = 0; i < count; i++)
        {
            // EntitiesDemo's layout: row by row over the field, so a chunk covers a strip of it
            var p = new Vector3((i % side + random.NextSingle()) * spacing - field / 2, -0.75f, (i / side + random.NextSingle()) * spacing - 10);
            var spawn = new EntitySpawn(p, batches[i & 3]) { Scale = new Vector3(0.4f), Rotation = Quaternion.CreateFromYawPitchRoll(random.NextSingle() * 6.28f, random.NextSingle() * 6.28f, 0) };
            if (i >= moving_count)
            {
                store.Spawn(still, spawn);
                continue;
            }
            EntityId id = store.Spawn(moving, spawn);
            store.Set(id, new Spin { Axis = Vector3.UnitY, Speed = 1 + random.NextSingle() });
            store.Set(id, new Orbit { Center = p, Radius = random.NextSingle(), Speed = 0.3f + random.NextSingle(), Phase = random.NextSingle() * 6.28f });
            store.Set(id, new Bob { Height = 0.5f, Speed = 2, Phase = random.NextSingle() * 6.28f });
        }
        store.SyncTransforms();

        var camera = new Vector3(0, 6, -20);
        Vector3 sun = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(0.5f, 0.6f, 0.0f));
        const float resolution = 2720.0f, min_texels = 1.0f;
        var s1 = new List<InstanceCullRequest>();
        var s2 = new List<InstanceCullRequest>();
        foreach (float extent in new[] { 64.0f, 2000.0f })
        {
            Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(camera - sun * 2000.0f, camera, MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4Extensions.CreateOrthoOffCenterLH(-extent, extent, -extent, extent, 10000.0f, 0.0f);
            var frustum = new Frustum(view, projection);
            var old_request = new InstanceCullRequest { ShadowCasters = true };
            old_request.SetFrustum(frustum, ignore_depth: true);
            s1.Add(old_request);
            var request = new InstanceCullRequest { ShadowCasters = true };
            request.SetFrustum(frustum, ignore_depth: true); // before the limits: it resets them
            request.MinDiameter = min_texels * 2.0f * extent / resolution;
            for (int plane = 0; plane < 2; plane++)
                if (Vector3.Dot(request.Planes[plane].Normal, sun) < 0.0f)
                    request.PlaneMask |= 1 << plane;
            s2.Add(request);
        }
        Entity point_entity = World.World.CreateEntity();
        Light point = point_entity.AddComponent<Light>()!;
        point.SetLightType(LightType.Point);
        point.SetIntensity(2000000.0f);
        float range = point.GetRange();
        var light = new Vector3(0.8f, 0.6f, -1.5f);
        float fov = MathF.PI / 2.0f + 0.02f;
        foreach ((Vector3 forward, Vector3 up) in new[] { (Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitY), (Vector3.UnitY, -Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitZ), (Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY) })
        {
            var frustum = new Frustum(Matrix4x4.CreateLookAtLeftHanded(light, light + forward, up), Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(fov, 1.0f, range, 0.05f));
            var old_request = new InstanceCullRequest { ShadowCasters = true };
            old_request.SetFrustum(frustum);
            s1.Add(old_request);
            var request = new InstanceCullRequest { ShadowCasters = true };
            request.SetFrustum(frustum);
            request.HasSphere = true;
            request.SphereCenter = request.SizeEye = light;
            request.SphereRadius = range;
            request.MinDiameterPerDistance = min_texels * 2.0f * MathF.Tan(fov * 0.5f) / resolution;
            s2.Add(request);
        }
        store.CullInstances(s1);
        store.CullInstances(s2);
        double s1_ms = Median(9, () =>
        {
            store.CullInstances(s1);
            foreach (InstanceCullRequest r in s1)
                store.FinishCull(r);
        });
        double s2_ms = Median(9, () =>
        {
            store.CullInstances(s2);
            store.FinishCulls(s2);
        });
        long s1_casters = s1.Sum(r => (long)r.Total), s2_casters = s2.Sum(r => (long)r.Total);

        var behaviours = new BatchedBehaviour[] { new SpinBehaviour(), new OrbitBehaviour(), new BobBehaviour() };
        foreach (BatchedBehaviour b in behaviours)
            World.World.RegisterBatched(b);
        for (int i = 0; i < 5; i++)
            World.World.Tick(1.0 / 60.0);
        double transforms = 0;
        Median(15, () => { World.World.Tick(1.0 / 60.0); transforms += World.World.Stats.TransformsMs; });
        transforms /= 15;
        // the groups the last sync stamped, and those of the frame before (the upload of a frame is both)
        uint version = store.SyncVersion;
        long groups = store.SlotGroupStamp.Take(store.SlotGroupCount).Count(stamp => stamp >= version - 1 && stamp != 0);
        double group_mb = groups * EntityStore.SlotGroupSize * 32.0 / 1048576.0;
        double all_mb = store.SlotCount * 32.0 / 1048576.0;
        foreach (BatchedBehaviour b in behaviours)
            World.World.UnregisterBatched(b);
        Console.WriteLine($"{count,8} | {F(s1_ms) + " (" + s1_casters + ")",27} | {F(s2_ms) + " (" + s2_casters + ")",27} | {F(transforms),24} | {group_mb,26:0.0} | {all_mb,13:0.0}");
        World.World.ClearScene();
    }

    private static string RuntimeInformation() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    private static void RunStore(int count)
    {
        World.World.ClearScene();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memory_before = GC.GetTotalMemory(true);
        EntityStore store = World.World.Store;
        var random = new Random(1);
        RenderBatch[] batches = Enumerable.Range(0, 4).Select(_ => RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)))).ToArray();
        EntityArchetype archetype = EntityArchetype.Empty.Renderable().With<Spin>().With<Orbit>().With<Bob>();
        float field = MathF.Max(40.0f, MathF.Sqrt(count) * 1.1f);
        var ids = new EntityId[count];
        long spawn_start = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(random.NextSingle() * field - field / 2, 0, random.NextSingle() * field - 10);
            ids[i] = store.Spawn(archetype, new EntitySpawn(p, batches[i & 3]) { Scale = new Vector3(0.4f), Layers = (i & 1) == 0 ? 1u : 2u });
            store.Set(ids[i], new Spin { Axis = Vector3.UnitY, Speed = 1 + random.NextSingle() });
            store.Set(ids[i], new Orbit { Center = p, Radius = random.NextSingle(), Speed = 0.3f + random.NextSingle(), Phase = random.NextSingle() * 6.28f });
            store.Set(ids[i], new Bob { Height = 0.5f, Speed = 2, Phase = random.NextSingle() * 6.28f });
        }
        double spawn_ms = Stopwatch.GetElapsedTime(spawn_start).TotalMilliseconds;
        store.SyncTransforms();
        double bytes = (GC.GetTotalMemory(true) - memory_before - (long)count * 8) / (double)count; // less the ids array

        var behaviours = new BatchedBehaviour[] { new SpinBehaviour(), new OrbitBehaviour(), new BobBehaviour() };
        foreach (BatchedBehaviour b in behaviours)
            World.World.RegisterBatched(b);
        for (int i = 0; i < 5; i++)
            World.World.Tick(1.0 / 60.0); // warm up
        double update = 0, transforms = 0;
        double tick = Median(15, () =>
        {
            World.World.Tick(1.0 / 60.0);
            update += World.World.Stats.UpdateMs + World.World.Stats.LateUpdateMs;
            transforms += World.World.Stats.TransformsMs;
        });
        update /= 15;
        transforms /= 15;
        long allocated_before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < 10; i++)
            World.World.Tick(1.0 / 60.0);
        long allocated = (GC.GetTotalAllocatedBytes(true) - allocated_before) / 10;

        // the design's rotate alone (one quaternion multiply, axis angle and normalize per entity)
        World.World.UnregisterBatched(behaviours[1]);
        World.World.UnregisterBatched(behaviours[2]);
        double rotate = 0;
        Median(15, () => { World.World.Tick(1.0 / 60.0); rotate += World.World.Stats.UpdateMs; });
        rotate /= 15;

        // culling: the stress mode's camera, plus views turned away and to the side with other layer masks
        var requests = new List<InstanceCullRequest>();
        for (int v = 0; v < 4; v++)
        {
            var eye = new Vector3(0, 6, -20);
            Quaternion turn = Quaternion.CreateFromYawPitchRoll(v * 0.8f, 0.32f, 0);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, turn);
            Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(eye, eye + forward, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(1.0f, 16.0f / 9.0f, 0.1f, 2000.0f);
            var request = new InstanceCullRequest { Mask = v == 3 ? 2u : uint.MaxValue, Callbacks = true };
            request.SetFrustum(new Frustum(view, projection));
            requests.Add(request);
        }
        string Cull(float min_pixels) => string.Join("/", new[] { 1, 2, 4 }.Select(n =>
        {
            List<InstanceCullRequest> subset = requests.Take(n).ToList();
            foreach (InstanceCullRequest r in subset)
                r.MinPixels = min_pixels;
            store.CullInstances(subset);
            return F(Median(9, () =>
            {
                store.CullInstances(subset);
                foreach (InstanceCullRequest r in subset)
                    store.FinishCull(r);
            }));
        }));
        string cull = Cull(0.0f);
        // the views' contribution cull (Renderer.MinPixelSize, 2 pixels of a 1920 x 1080 render) and what it keeps
        for (int v = 0; v < requests.Count; v++)
        {
            var eye = new Vector3(0, 6, -20);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(v * 0.8f, 0.32f, 0));
            requests[v].SetContribution(Matrix4x4.CreateLookAtLeftHanded(eye, eye + forward, Vector3.UnitY) * Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(1.0f, 16.0f / 9.0f, 0.1f, 2000.0f),
                                        new Vector2(1920, 1080), 2.0f);
        }
        int frustum_kept = requests[0].Total;
        string contribution = Cull(2.0f);
        contribution += $" ({requests[0].Total} of {frustum_kept} kept)";

        // structural changes: 10k random destroys then 10k spawns
        int churn = System.Math.Min(10000, count / 2);
        double structural = Median(5, () =>
        {
            for (int i = 0; i < churn; i++)
            {
                int k = random.Next(count);
                if (store.IsAlive(ids[k]))
                    store.Destroy(ids[k]);
            }
            for (int i = 0; i < churn; i++)
            {
                int k = random.Next(count);
                if (!store.IsAlive(ids[k]))
                    ids[k] = store.Spawn(archetype, new EntitySpawn(Vector3.Zero, batches[k & 3]));
            }
        });

        foreach (BatchedBehaviour b in behaviours)
            World.World.UnregisterBatched(b);

        // hierarchy: a quarter of them children of the others, two levels
        World.World.ClearScene();
        EntityArchetype plain = EntityArchetype.Empty.Renderable();
        var roots = new EntityId[count - count / 4];
        for (int i = 0; i < roots.Length; i++)
            roots[i] = store.Spawn(plain, new EntitySpawn(new Vector3(i % 1000, 0, i / 1000), batches[i & 3]));
        for (int i = 0; i < count / 4; i++)
            store.Spawn(plain, new EntitySpawn(new Vector3(0, 1, 0), batches[i & 3]) { Parent = roots[i % roots.Length] });
        store.SyncTransforms();
        double full_sync = Median(9, () =>
        {
            store.MarkAllDirty();
            store.SyncTransforms();
        });

        Console.WriteLine($"{count,8} | {F(spawn_ms),8} | {bytes,12:0} | {F(update),29} | {F(rotate),14} | {F(transforms),13} | {F(tick),7} | {allocated,16} | {F(full_sync),32} | {cull,19} | {contribution,34} | {F(structural),20}");
        World.World.ClearScene();
    }

    // the class behaviours and the world's lists: what the S1 fixes changed (DispatchList.Remove, the full rebuild on add)
    private static void RunClassSide()
    {
        const int count = 200000;
        World.World.ClearScene();
        var counters = new List<Counter>();
        for (int i = 0; i < count; i++)
        {
            Entity entity = World.World.CreateEntity();
            entity.AddComponent<Render>();
            counters.Add(entity.AddBehaviour(new Counter()));
        }
        World.World.Tick(0.0);
        double class_update = Median(9, () => World.World.Tick(0.0));
        double disable = Median(5, () =>
        {
            for (int i = 0; i < 10000; i++)
                counters[i * 20].Enabled = false;
            World.World.Tick(0.0);
            for (int i = 0; i < 10000; i++)
                counters[i * 20].Enabled = true;
            World.World.Tick(0.0);
        });
        double add_one = Median(9, () =>
        {
            Entity entity = World.World.CreateEntity();
            entity.AddComponent<Render>();
            World.World.Tick(0.0);
        });
        Console.WriteLine($"class side, {count} entities with a Render and an IUpdate behaviour: tick {F(class_update)} ms, " +
                          $"disable + re-enable 10k behaviours (two ticks) {F(disable)} ms, add one renderable entity + tick {F(add_one)} ms");
        World.World.ClearScene();
    }
}
