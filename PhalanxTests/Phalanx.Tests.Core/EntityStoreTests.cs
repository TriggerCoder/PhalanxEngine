using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the chunked entity store (EntityStore.cs): handles, swap back removal, components, command buffers, facades
[Collection("World")]
public class EntityStoreTests
{
    internal struct Value : IComponentData { public int V; }
    internal struct Velocity : IComponentData { public Vector3 V; }

    internal static void Run(Action body)
    {
        World.World.SetMainThread();
        World.World.ClearScene();
        try
        {
            body();
        }
        finally
        {
            World.World.ClearScene();
        }
    }

    private static readonly EntityArchetype Valued = EntityArchetype.Empty.With<Value>();

    [Fact]
    public void Destroyed_handles_go_stale_and_their_index_comes_back_with_a_new_generation()
    {
        Run(() =>
        {
            var store = new EntityStore(); // a fresh one: the world's reuses the indices of earlier tests
            EntityId a = store.Spawn(Valued);
            Assert.True(store.IsAlive(a));
            store.Destroy(a);
            Assert.False(store.IsAlive(a));
            EntityId b = store.Spawn(Valued);
            Assert.Equal(a.Index, b.Index);
            Assert.NotEqual(a.Generation, b.Generation);
            Assert.True(store.IsAlive(b));
            Assert.False(store.IsAlive(a));
            Assert.ThrowsAny<Exception>(() => store.GetLocalPosition(a));
            Assert.False(store.IsAlive(EntityId.Null));
            // a cleared world store keeps the generations going: an id from before is stale after
            EntityId c = World.World.Store.Spawn(Valued);
            World.World.ClearScene();
            Assert.False(World.World.Store.IsAlive(c));
            EntityId d = World.World.Store.Spawn(Valued);
            Assert.NotEqual(c, d);
        });
    }

    [Fact]
    public void Swap_back_removal_keeps_every_other_entity_intact_across_chunks()
    {
        Run(() =>
        {
            EntityStore store = World.World.Store;
            const int count = 40000; // three chunks (16384 rows each)
            var ids = new EntityId[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = store.Spawn(Valued, new EntitySpawn(new Vector3(i, 0, 0)));
                store.Set(ids[i], new Value { V = i });
            }
            for (int i = 0; i < count; i += 3)
                store.Destroy(ids[i]);
            Assert.Equal(count - (count + 2) / 3, store.Count);
            for (int i = 0; i < count; i++)
            {
                if (i % 3 == 0)
                {
                    Assert.False(store.IsAlive(ids[i]));
                    continue;
                }
                Assert.Equal(i, store.Get<Value>(ids[i]).V);
                Assert.Equal(new Vector3(i, 0, 0), store.GetLocalPosition(ids[i]));
            }
        });
    }

    [Fact]
    public void Adding_and_removing_a_component_moves_the_entity_and_keeps_its_data()
    {
        Run(() =>
        {
            EntityStore store = World.World.Store;
            EntityId id = store.Spawn(Valued, new EntitySpawn(new Vector3(1, 2, 3)));
            EntityId other = store.Spawn(Valued);
            store.Set(id, new Value { V = 7 });
            store.Set(other, new Value { V = 9 });
            store.Add(id, new Velocity { V = Vector3.UnitY });
            Assert.True(store.Has<Velocity>(id));
            Assert.Equal(Valued.With<Velocity>(), store.GetArchetype(id));
            Assert.Equal(7, store.Get<Value>(id).V);
            Assert.Equal(Vector3.UnitY, store.Get<Velocity>(id).V);
            Assert.Equal(new Vector3(1, 2, 3), store.GetLocalPosition(id));
            store.Remove<Value>(id);
            Assert.False(store.Has<Value>(id));
            Assert.Equal(Vector3.UnitY, store.Get<Velocity>(id).V);
            Assert.Equal(9, store.Get<Value>(other).V);
        });
    }

    [Fact]
    public void A_command_buffer_applies_its_commands_in_order_on_playback()
    {
        Run(() =>
        {
            EntityStore store = World.World.Store;
            EntityId a = store.Spawn(Valued);
            EntityId b = store.Spawn(Valued);
            var commands = new CommandBuffer();
            commands.Spawn<Value>(Valued, new EntitySpawn(new Vector3(5, 0, 0)), new Value { V = 42 });
            commands.Set(a, new Value { V = 3 });
            commands.Set(a, new Value { V = 4 }); // the last write wins
            commands.SetLocalPosition(a, new Vector3(0, 1, 0));
            commands.SetEnabled(b, false);
            commands.Destroy(b);
            commands.Destroy(b); // a stale handle is ignored
            Assert.Equal(2, store.Count);
            commands.Playback(store);
            Assert.Equal(0, commands.Count);
            Assert.Equal(2, store.Count);
            Assert.Equal(4, store.Get<Value>(a).V);
            Assert.Equal(new Vector3(0, 1, 0), store.GetLocalPosition(a));
            Assert.False(store.IsAlive(b));
            var spawned = new List<int>();
            foreach (EntityId id in AllIds(store))
                if (id != a)
                    spawned.Add(store.Get<Value>(id).V);
            Assert.Equal(new[] { 42 }, spawned);
        });
    }

    internal static IEnumerable<EntityId> AllIds(EntityStore store) => store.LiveIds();

    [Fact]
    public void A_facade_reads_and_writes_the_store_and_runs_class_behaviours()
    {
        Run(() =>
        {
            EntityStore store = World.World.Store;
            EntityId id = store.Spawn(Valued, new EntitySpawn(new Vector3(1, 0, 0)));
            Entity facade = World.World.GetEntity(id)!;
            Assert.Same(facade, World.World.GetEntity(id));
            Assert.Equal(id, facade.Id);
            Assert.Equal(new Vector3(1, 0, 0), facade.GetPosition());
            facade.SetPosition(new Vector3(0, 2, 0));
            Assert.Equal(new Vector3(0, 2, 0), store.GetLocalPosition(id));
            var log = new List<string>();
            facade.AddBehaviour(new LoggingBehaviour(log));
            World.World.Tick(0.016);
            Assert.Contains("Update", log);
            facade.SetActive(false);
            Assert.False(store.IsEnabled(id));
            store.Destroy(id);
            Assert.Contains("OnDestroy", log);
            Assert.Null(World.World.GetEntity(id));
        });
    }

    private sealed class LoggingBehaviour : Behaviour, IUpdate, IOnDestroy
    {
        private readonly List<string> m_log;
        public LoggingBehaviour(List<string> log) { m_log = log; }
        public void Update()    => m_log.Add("Update");
        public void OnDestroy() => m_log.Add("OnDestroy");
    }

    [Fact]
    public void Packed_transforms_round_trip_through_spartans_packed_instance_layout()
    {
        Assert.Equal(32, System.Runtime.CompilerServices.Unsafe.SizeOf<PackedTransform>());
        var position = new Vector3(3, -2, 10);
        Quaternion rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 1.9f));
        var scale = new Vector3(0.5f, 2, 1.5f);
        Matrix4x4 expected = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        AssertNear(expected, PackedTransform.Pack(position, rotation, scale).GetMatrix(), 1e-3f);
        AssertNear(expected, PackedTransform.Pack(expected).GetMatrix(), 1e-3f);
    }

    internal static void AssertNear(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= tolerance, $"[{r},{c}] expected {expected[r, c]} got {actual[r, c]}");
    }
}

// the transform pass (EntityStore.Transforms.cs): hierarchy, depth order, mixing with class entities
[Collection("World")]
public class EntityHierarchyTests
{
    [Fact]
    public void World_matrices_follow_the_hierarchy_after_a_sync()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            EntityArchetype plain = EntityArchetype.Empty;
            var root_rotation = Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0);
            EntityId root = store.Spawn(plain, new EntitySpawn(new Vector3(10, 0, 0)) { Rotation = root_rotation, Scale = new Vector3(2) });
            EntityId child = store.Spawn(plain, new EntitySpawn(new Vector3(0, 1, 0)) { Parent = root });
            EntityId grandchild = store.Spawn(plain, new EntitySpawn(new Vector3(0, 0, 1)) { Parent = child, Rotation = Quaternion.CreateFromYawPitchRoll(0, 0.4f, 0) });
            store.SyncTransforms();

            Matrix4x4 m_root = Matrix4x4.CreateScale(2) * Matrix4x4.CreateFromQuaternion(root_rotation) * Matrix4x4.CreateTranslation(10, 0, 0);
            Matrix4x4 m_child = Matrix4x4.CreateTranslation(0, 1, 0) * m_root;
            Matrix4x4 m_grandchild = Matrix4x4.CreateFromYawPitchRoll(0, 0.4f, 0) * Matrix4x4.CreateTranslation(0, 0, 1) * m_child;
            EntityStoreTests.AssertNear(m_root, store.GetSyncedWorldMatrix(root), 1e-4f);
            EntityStoreTests.AssertNear(m_child, store.GetSyncedWorldMatrix(child), 1e-4f);
            EntityStoreTests.AssertNear(m_grandchild, store.GetSyncedWorldMatrix(grandchild), 1e-4f);

            store.SetLocalPosition(root, new Vector3(-5, 0, 0));
            store.SyncTransforms();
            m_root = Matrix4x4.CreateScale(2) * Matrix4x4.CreateFromQuaternion(root_rotation) * Matrix4x4.CreateTranslation(-5, 0, 0);
            EntityStoreTests.AssertNear(Matrix4x4.CreateTranslation(0, 1, 0) * m_root, store.GetSyncedWorldMatrix(child), 1e-4f);

            Assert.ThrowsAny<Exception>(() => store.SetParent(root, grandchild)); // a cycle
            store.SetParent(child, EntityId.Null);
            store.SyncTransforms();
            EntityStoreTests.AssertNear(Matrix4x4.CreateTranslation(0, 1, 0), store.GetSyncedWorldMatrix(child), 1e-5f);
            EntityStoreTests.AssertNear(Matrix4x4.CreateFromYawPitchRoll(0, 0.4f, 0) * Matrix4x4.CreateTranslation(0, 0, 1) * Matrix4x4.CreateTranslation(0, 1, 0), store.GetSyncedWorldMatrix(grandchild), 1e-5f);

            store.Destroy(child); // takes its subtree
            Assert.False(store.IsAlive(grandchild));
            Assert.True(store.IsAlive(root));
        });
    }

    [Fact]
    public void Many_roots_are_transformed_like_the_scalar_reference()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            var random = new Random(7);
            var ids = new EntityId[20000];
            var expected = new Matrix4x4[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var p = new Vector3(random.NextSingle() * 100, random.NextSingle() * 100, random.NextSingle() * 100);
                Quaternion q = Quaternion.Normalize(new Quaternion(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
                var s = new Vector3(0.5f + random.NextSingle(), 0.5f + random.NextSingle(), 0.5f + random.NextSingle());
                ids[i] = store.Spawn(EntityArchetype.Empty, new EntitySpawn(p) { Rotation = q, Scale = s });
                expected[i] = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);
            }
            store.SyncTransforms();
            for (int i = 0; i < ids.Length; i++)
                EntityStoreTests.AssertNear(expected[i], store.GetSyncedWorldMatrix(ids[i]), 1e-4f);
        });
    }

    [Fact]
    public void A_store_entity_follows_its_class_entity_parent()
    {
        EntityStoreTests.Run(() =>
        {
            Entity parent = World.World.CreateEntity();
            parent.SetPosition(new Vector3(1, 2, 3));
            World.World.Tick(0.0);
            EntityId child = World.World.Store.Spawn(EntityArchetype.Empty, new EntitySpawn(new Vector3(0, 0, 1)) { Parent = parent.Id });
            World.World.Tick(0.0);
            Assert.Equal(new Vector3(1, 2, 4), World.World.Store.GetSyncedWorldMatrix(child).Translation);
            parent.SetPosition(new Vector3(-1, 0, 0));
            World.World.Tick(0.0);
            Assert.Equal(new Vector3(-1, 0, 1), World.World.Store.GetSyncedWorldMatrix(child).Translation);
        });
    }
}

// batched behaviours (World.Batched.cs): parallel chunks, phases, ordering, deterministic command buffers
[Collection("World")]
public class BatchedBehaviourTests
{
    private sealed class Integrate : BatchedBehaviour<EntityStoreTests.Velocity>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<EntityStoreTests.Velocity> v = chunk.Get<EntityStoreTests.Velocity>();
            Span<Vector3> p = chunk.Positions;
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.IsEnabled(i))
                    p[i] += v[i].V * context.DeltaTime;
        }
    }

    private sealed class Logger : BatchedBehaviour<EntityStoreTests.Value>, IBatchedFixedUpdate, IBatchedUpdate, IBatchedLateUpdate
    {
        private readonly List<string> m_log;
        private readonly string m_name;
        public Logger(List<string> log, string name) { m_log = log; m_name = name; RunInParallel = false; }
        public void FixedUpdate(in BatchContext context, in ChunkView chunk) => m_log.Add($"{m_name}.FixedUpdate");
        public void Update(in BatchContext context, in ChunkView chunk) => m_log.Add($"{m_name}.Update");
        public void LateUpdate(in BatchContext context, in ChunkView chunk) => m_log.Add($"{m_name}.LateUpdate");
    }

    private sealed class ClassLogger : Behaviour, IFixedUpdate, IUpdate, ILateUpdate
    {
        private readonly List<string> m_log;
        public ClassLogger(List<string> log) { m_log = log; }
        public void FixedUpdate() => m_log.Add("Class.FixedUpdate");
        public void Update()      => m_log.Add("Class.Update");
        public void LateUpdate()  => m_log.Add("Class.LateUpdate");
    }

    private sealed class Spawner : BatchedBehaviour<EntityStoreTests.Velocity>, IBatchedUpdate
    {
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            ReadOnlySpan<Vector3> p = chunk.ReadPositions;
            for (int i = 0; i < chunk.Count; i++)
                if (i % 7 == 0)
                    context.Commands.Spawn<EntityStoreTests.Value>(EntityArchetype.Empty.With<EntityStoreTests.Value>(), new EntitySpawn(p[i]), new EntityStoreTests.Value { V = (int)p[i].X });
                else if (i % 11 == 0)
                    context.Commands.Destroy(chunk.Entities[i]);
        }
    }

    private static void WithBehaviours(BatchedBehaviour[] behaviours, Action body)
    {
        EntityStoreTests.Run(() =>
        {
            foreach (BatchedBehaviour b in behaviours)
                World.World.RegisterBatched(b);
            try
            {
                body();
            }
            finally
            {
                foreach (BatchedBehaviour b in behaviours)
                    World.World.UnregisterBatched(b);
            }
        });
    }

    [Fact]
    public void A_parallel_update_moves_every_enabled_entity_exactly_once()
    {
        WithBehaviours(new BatchedBehaviour[] { new Integrate() }, () =>
        {
            EntityStore store = World.World.Store;
            var archetype = EntityArchetype.Empty.With<EntityStoreTests.Velocity>();
            var ids = new EntityId[100000];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = store.Spawn(archetype, new EntitySpawn(new Vector3(i, 0, 0)) { Enabled = i % 5 != 0 });
                store.Set(ids[i], new EntityStoreTests.Velocity { V = new Vector3(0, i % 13, 1) });
            }
            for (int t = 0; t < 3; t++)
                World.World.Tick(0.5);
            for (int i = 0; i < ids.Length; i++)
            {
                Vector3 expected = i % 5 == 0 ? new Vector3(i, 0, 0) : new Vector3(i, 1.5f * (i % 13), 1.5f);
                Assert.Equal(expected, store.GetLocalPosition(ids[i]));
                Assert.Equal(expected, store.GetSyncedWorldMatrix(ids[i]).Translation); // the tick ends with the transform pass
            }
        });
    }

    [Fact]
    public void Phases_run_batched_behaviours_in_order_before_the_class_behaviours()
    {
        var log = new List<string>();
        WithBehaviours(new BatchedBehaviour[] { new Logger(log, "Second") { Order = 1 }, new Logger(log, "First") }, () =>
        {
            World.World.FixedTimeStep = 0.02;
            World.World.Store.Spawn(EntityArchetype.Empty.With<EntityStoreTests.Value>());
            Entity entity = World.World.CreateEntity();
            entity.AddBehaviour(new ClassLogger(log));
            World.World.Tick(0.0);
            log.Clear();
            World.World.Tick(0.02);
            Assert.Equal(new[]
            {
                "First.FixedUpdate", "Second.FixedUpdate", "Class.FixedUpdate",
                "First.Update", "Second.Update", "Class.Update",
                "First.LateUpdate", "Second.LateUpdate", "Class.LateUpdate"
            }, log);
        });
    }

    // the commands of parallel chunks play back in chunk and row order, so a run is the same every time
    private static List<(uint, int)> SpawnRun()
    {
        var result = new List<(uint, int)>();
        WithBehaviours(new BatchedBehaviour[] { new Spawner() }, () =>
        {
            EntityStore store = World.World.Store;
            var archetype = EntityArchetype.Empty.With<EntityStoreTests.Velocity>();
            for (int i = 0; i < 60000; i++)
                store.Spawn(archetype, new EntitySpawn(new Vector3(i, 0, 0)));
            World.World.Tick(0.016);
            foreach (EntityId id in EntityStoreTests.AllIds(store))
                if (store.Has<EntityStoreTests.Value>(id))
                    result.Add((id.Index, store.Get<EntityStoreTests.Value>(id).V));
        });
        return result;
    }

    [Fact]
    public void Command_buffers_of_parallel_chunks_play_back_deterministically()
    {
        SpawnRun(); // the world store's free indices are then the same before each of the next two runs
        List<(uint, int)> first = SpawnRun();
        List<(uint, int)> second = SpawnRun();
        Assert.True(first.Count > 8000);
        Assert.Equal(first, second); // the same indices for the same spawns, whatever thread ran which chunk
        Assert.Equal(first.Count, first.Select(x => x.Item2).Distinct().Count()); // each spawn once
    }

    [Fact]
    public void A_structural_change_inside_a_job_is_refused()
    {
        WithBehaviours(new BatchedBehaviour[] { new DirectSpawner() }, () =>
        {
            EntityStore store = World.World.Store;
            store.Spawn(EntityArchetype.Empty.With<EntityStoreTests.Velocity>());
            World.World.Tick(0.016); // the exception is logged, the tick goes on
            Assert.Equal(1, store.Count);
            Assert.True(DirectSpawner.Refused);
        });
    }

    private sealed class DirectSpawner : BatchedBehaviour<EntityStoreTests.Velocity>, IBatchedUpdate
    {
        public static bool Refused;
        public void Update(in BatchContext context, in ChunkView chunk)
        {
            try
            {
                World.World.Store.Spawn(EntityArchetype.Empty);
            }
            catch (InvalidOperationException)
            {
                Refused = true;
            }
        }
    }
}

// the store's per view culling (EntityStore.Culling.cs): layers, frustum, chunk skipping, shadow casters, callbacks
[Collection("World")]
public class EntityCullingTests
{
    private static readonly EntityArchetype Drawn = EntityArchetype.Empty.Renderable();

    private static Frustum LookAlongZ(Vector3 eye)
    {
        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(eye, eye + Vector3.UnitZ, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(1.0f, 1.0f, 0.1f, 500.0f);
        return new Frustum(view, projection);
    }

    private static HashSet<uint> Cull(EntityStore store, InstanceCullRequest request)
    {
        store.CullInstances(new[] { request });
        store.FinishCull(request);
        return request.Slots.Take(request.Total).ToHashSet();
    }

    [Fact]
    public void Layers_and_frustum_match_the_reference_test_for_every_view()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch batch_a = RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)));
            RenderBatch batch_b = RenderBatches.GetForBounds(new BoundingBox(new Vector3(-1.0f), new Vector3(1.0f)));
            var random = new Random(3);
            var ids = new List<EntityId>();
            for (int i = 0; i < 50000; i++)
            {
                var p = new Vector3(random.NextSingle() * 400 - 200, random.NextSingle() * 40 - 20, random.NextSingle() * 400 - 200);
                ids.Add(store.Spawn(Drawn, new EntitySpawn(p, (i & 1) == 0 ? batch_a : batch_b) { Layers = 1u << random.Next(3), Enabled = i % 17 != 0 }));
            }
            store.SyncTransforms();
            Frustum frustum = LookAlongZ(new Vector3(0, 0, -150));
            foreach (uint mask in new[] { uint.MaxValue, 1u, 6u })
            {
                var request = new InstanceCullRequest { Mask = mask };
                request.SetFrustum(frustum);
                HashSet<uint> visible = Cull(store, request);
                var expected = new HashSet<uint>();
                foreach (EntityId id in ids)
                {
                    if (!store.IsEnabled(id) || (store.GetLayers(id) & mask) == 0)
                        continue;
                    BoundingBox box = store.GetBounds(id);
                    if (frustum.IsVisible(box.GetCenter(), box.GetExtents()))
                        expected.Add(store.GetSlot(id));
                }
                Assert.True(expected.Count > 1000);
                Assert.Equal(expected.Count, request.Total); // nothing twice
                Assert.True(expected.SetEquals(visible), $"mask {mask}");
                // grouped by batch: a's slots, then b's
                Assert.Equal(new[] { batch_a, batch_b }.Select(b => b.Id), request.Batches);
                foreach (uint b in request.Batches)
                    foreach (uint slot in request.GetSlots(b))
                        Assert.Equal(b, store.GetRenderBatch(ids.First(id => store.GetSlot(id) == slot)).Id);
            }
        });
    }

    [Fact]
    public void Whole_chunks_outside_the_view_or_its_layers_are_skipped()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            var unit = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f));
            RenderBatch in_front = RenderBatches.GetForBounds(unit);
            RenderBatch behind = RenderBatches.GetForBounds(unit);
            RenderBatch other_layer = RenderBatches.GetForBounds(unit);
            for (int i = 0; i < 1000; i++)
            {
                store.Spawn(Drawn, new EntitySpawn(new Vector3(i % 10, i / 100, 20 + i % 7), in_front));
                store.Spawn(Drawn, new EntitySpawn(new Vector3(i % 10, 0, -20 - i % 7), behind));
                store.Spawn(Drawn, new EntitySpawn(new Vector3(i % 10, 0, 20), other_layer) { Layers = 4 });
            }
            store.SyncTransforms();
            var request = new InstanceCullRequest { Mask = 1 };
            request.SetFrustum(LookAlongZ(Vector3.Zero));
            Cull(store, request);
            Assert.Equal(3, store.RenderableChunkCount);
            Assert.Equal(1, request.ChunksTested);
            Assert.Equal(new[] { in_front.Id }, request.Batches);
            Assert.Equal(1000, request.Total);
        });
    }

    [Fact]
    public void Shadow_requests_take_only_casters_and_callback_rows_are_set_aside()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch batch = RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)));
            EntityId caster = store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0, 10), batch));
            EntityId not_caster = store.Spawn(Drawn, new EntitySpawn(new Vector3(1, 0, 10), batch) { CastsShadows = false });
            EntityId with_callbacks = store.Spawn(Drawn, new EntitySpawn(new Vector3(-1, 0, 10), batch));
            World.World.GetEntity(with_callbacks)!.AddBehaviour(new PreDrawBehaviour());
            World.World.Tick(0.0); // the behaviour starts and registers its callbacks, the transforms sync

            var shadow = new InstanceCullRequest { ShadowCasters = true };
            HashSet<uint> casters = Cull(store, shadow);
            Assert.Equal(new[] { store.GetSlot(caster), store.GetSlot(with_callbacks) }.ToHashSet(), casters);

            var view = new InstanceCullRequest { Callbacks = true };
            view.SetFrustum(LookAlongZ(Vector3.Zero));
            store.CullInstances(new[] { view });
            Assert.Single(view.CallbackRows);
            Assert.Equal(with_callbacks, view.CallbackRows[0].Id);
            view.AddExtra(batch.Id, view.CallbackRows[0].Slot); // the facade's callbacks kept it
            store.FinishCull(view);
            Assert.Equal(new[] { caster, not_caster, with_callbacks }.Select(store.GetSlot).ToHashSet(), view.Slots.Take(view.Total).ToHashSet());
        });
    }

    private sealed class PreDrawBehaviour : Behaviour, IPreDraw
    {
        public void OnPreDraw(in ViewContext view) { }
    }
}

// the class behaviour dispatch lists and the incremental world lists (World.Behaviours.cs, World.cs)
[Collection("World")]
public class WorldListTests
{
    private sealed class Counter : Behaviour, IUpdate
    {
        public readonly List<int> Log;
        public readonly int Index;
        public Counter(List<int> log, int index) { Log = log; Index = index; }
        public void Update() => Log.Add(Index);
    }

    [Fact]
    public void Disabling_many_behaviours_keeps_the_order_of_the_rest()
    {
        EntityStoreTests.Run(() =>
        {
            var log = new List<int>();
            var behaviours = new List<Counter>();
            for (int i = 0; i < 20000; i++)
            {
                Entity entity = World.World.CreateEntity();
                behaviours.Add(entity.AddBehaviour(new Counter(log, i)));
            }
            World.World.Tick(0.0);
            log.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < behaviours.Count; i += 2)
                behaviours[i].Enabled = false;
            World.World.Tick(0.0);
            Assert.True(clock.Elapsed.TotalSeconds < 2.0, $"{clock.Elapsed.TotalMilliseconds} ms");
            Assert.Equal(Enumerable.Range(0, 10000).Select(i => 2 * i + 1), log);
            log.Clear();
            behaviours[1].Enabled = false;
            behaviours[3].Entity.SetActive(false);
            World.World.Tick(0.0);
            Assert.Equal(Enumerable.Range(2, 9998).Select(i => 2 * i + 1), log);
        });
    }

    [Fact]
    public void The_renderable_list_follows_activity_and_keeps_creation_order()
    {
        EntityStoreTests.Run(() =>
        {
            var entities = new List<Entity>();
            for (int i = 0; i < 5; i++)
            {
                Entity entity = World.World.CreateEntity();
                entity.AddComponent<Render>();
                entities.Add(entity);
            }
            World.World.Tick(0.0);
            Assert.Equal(entities, World.World.GetEntitiesRenderables());
            entities[2].SetActive(false);
            World.World.Tick(0.0);
            Assert.Equal(new[] { entities[0], entities[1], entities[3], entities[4] }, World.World.GetEntitiesRenderables());
            entities[2].SetActive(true);
            World.World.Tick(0.0);
            Assert.Equal(entities, World.World.GetEntitiesRenderables());
            World.World.RemoveEntity(entities[0]);
            World.World.Tick(0.0);
            Assert.Equal(entities.Skip(1), World.World.GetEntitiesRenderables());
        });
    }
}
