using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;

namespace Phalanx.Bench;

// The cpu benchmark of the entity store (PhalanxEngine-entities-s1.md, measured numbers): no gpu, the world's store,
// the stress mode's three batched behaviours, the transform pass, the per view culling, structural changes and the
// class side (behaviour dispatch, the incremental world lists). Run it in Release:
//   dotnet run -c Release --project PhalanxTests/Phalanx.Bench [-- counts...]   (default 10000 100000 1000000 2000000)
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
        Console.WriteLine("entities | spawn ms | bytes/entity | update+late ms (3 behaviours) | rotate only ms | transforms ms | tick ms | allocated B/tick | hierarchy sync ms (25% children) | cull 1/2/4 views ms | destroy+spawn 10k ms");
        foreach (int count in counts)
            RunStore(count);
        RunClassSide();
        return 0;
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
        string cull = string.Join("/", new[] { 1, 2, 4 }.Select(n =>
        {
            List<InstanceCullRequest> subset = requests.Take(n).ToList();
            store.CullInstances(subset);
            return F(Median(9, () =>
            {
                store.CullInstances(subset);
                foreach (InstanceCullRequest r in subset)
                    store.FinishCull(r);
            }));
        }));

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

        Console.WriteLine($"{count,8} | {F(spawn_ms),8} | {bytes,12:0} | {F(update),29} | {F(rotate),14} | {F(transforms),13} | {F(tick),7} | {allocated,16} | {F(full_sync),32} | {cull,19} | {F(structural),20}");
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
