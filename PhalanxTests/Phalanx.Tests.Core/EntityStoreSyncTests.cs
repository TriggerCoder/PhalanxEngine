using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Phalanx.Math;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the store's transform pass (EntityStore.Transforms.cs: the vector and scalar root paths, row dirty ranges, slot
// group stamps), the worker pool (WorkerPool.cs) and the shadow caster limits of the culling (EntityStore.Culling.cs)
[Collection("World")]
public class EntityStoreSyncTests
{
    private static readonly EntityArchetype Drawn = EntityArchetype.Empty.Renderable();

    private static RenderBatch UnitBatch() => RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)));

    private static Quaternion RandomRotation(Random random) =>
        Quaternion.Normalize(new Quaternion(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f + 0.01f));

    private static Matrix4x4 Reference(EntityStore store, EntityId id) =>
        Matrix4x4.CreateScale(store.GetLocalScale(id)) * Matrix4x4.CreateFromQuaternion(store.GetLocalRotation(id)) * Matrix4x4.CreateTranslation(store.GetLocalPosition(id));

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= 1e-4f * (1.0f + MathF.Abs(expected[r, c])), $"[{r},{c}] {expected[r, c]} vs {actual[r, c]}");
    }

    [Fact]
    public void Root_world_matrices_and_packed_instances_match_the_scalar_reference()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch batch = UnitBatch();
            var random = new Random(11);
            var ids = new List<EntityId>();
            for (int i = 0; i < 20000 + 5; i++) // whole chunks, a partial one and a scalar tail
                ids.Add(store.Spawn(Drawn, new EntitySpawn(new Vector3(random.NextSingle() * 100, random.NextSingle() * 10, random.NextSingle() * 100), batch)
                {
                    Rotation = RandomRotation(random),
                    Scale    = new Vector3(0.1f + random.NextSingle(), 0.1f + random.NextSingle(), 0.1f + random.NextSingle())
                }));
            store.SyncTransforms();
            foreach (EntityId id in ids)
            {
                AssertClose(Reference(store, id), store.GetSyncedWorldMatrix(id));
                PackedTransform expected = PackedTransform.Pack(store.GetLocalPosition(id), store.GetLocalRotation(id), store.GetLocalScale(id));
                PackedTransform actual = store.Instances[store.GetSlot(id)];
                Assert.Equal((expected.pos_x, expected.pos_y, expected.pos_z), (actual.pos_x, actual.pos_y, actual.pos_z));
                Assert.Equal((expected.scale_x, expected.scale_y, expected.scale_z), (actual.scale_x, actual.scale_y, actual.scale_z));
                for (int shift = 0; shift < 32; shift += 16)
                {
                    // the vector path normalizes with a reciprocal square root: one snorm step at most
                    Assert.InRange(System.Math.Abs((short)(expected.rotation_xy >> shift) - (short)(actual.rotation_xy >> shift)), 0, 1);
                    Assert.InRange(System.Math.Abs((short)(expected.rotation_zw >> shift) - (short)(actual.rotation_zw >> shift)), 0, 1);
                }
                BoundingBox box = store.GetBounds(id);
                BoundingBox reference = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)).Transform(Reference(store, id));
                Assert.True(Vector3.Distance(box.GetMin(), reference.GetMin()) < 1e-3f && Vector3.Distance(box.GetMax(), reference.GetMax()) < 1e-3f);
            }
        });
    }

    [Fact]
    public void One_moved_entity_resyncs_only_its_rows_group_and_keeps_the_chunk_bounds_tight()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch batch = UnitBatch();
            var ids = new List<EntityId>();
            for (int i = 0; i < 5000; i++)
                ids.Add(store.Spawn(Drawn, new EntitySpawn(new Vector3(i % 100, 0, i / 100), batch)));
            store.SyncTransforms();
            uint first = store.SyncVersion;
            uint[] stamps = (uint[])store.SlotGroupStamp.Clone();

            EntityId moved = ids[3000];
            store.SetLocalPosition(moved, new Vector3(500, 0, 0));
            store.SetLocalScale(ids[3001], new Vector3(4.0f));
            store.SyncTransforms();
            Assert.True(store.SyncVersion != first);
            int group = (int)(store.GetSlot(moved) >> EntityStore.SlotGroupShift);
            for (int g = 0; g < stamps.Length; g++)
                if (g == group)
                    Assert.Equal(store.SyncVersion, store.SlotGroupStamp[g]);
                else
                    Assert.Equal(stamps[g], store.SlotGroupStamp[g]);
            Assert.Equal(new Vector3(500, 0, 0), store.GetSyncedWorldMatrix(moved).Translation);
            Assert.Equal(500.0f, store.Instances[store.GetSlot(moved)].pos_x);
            foreach (EntityId id in ids.Where(id => id != moved && id != ids[3001]))
                Assert.Equal(store.GetLocalPosition(id), store.GetSyncedWorldMatrix(id).Translation);

            // the chunk's bounds and largest row cover the moved and the scaled one (merged over every row)
            var request = new InstanceCullRequest();
            request.SetFrustum(new Frustum(Matrix4x4.CreateLookAtLeftHanded(new Vector3(500, 0, -10), new Vector3(500, 0, 0), Vector3.UnitY),
                                           Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(0.2f, 1.0f, 0.1f, 50.0f)));
            store.CullInstances(new[] { request });
            store.FinishCull(request);
            Assert.Equal(new[] { store.GetSlot(moved) }, request.Slots.Take(request.Total));
        });
    }

    [Fact]
    public void Casters_below_the_texel_threshold_are_left_out_and_tiny_chunks_skipped_whole()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch big = UnitBatch();
            RenderBatch tiny = RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.01f), new Vector3(0.01f)));
            var random = new Random(5);
            var ids = new List<EntityId>();
            for (int i = 0; i < 3000; i++)
            {
                var p = new Vector3(random.NextSingle() * 200 - 100, 0, random.NextSingle() * 200 - 100);
                ids.Add(store.Spawn(Drawn, new EntitySpawn(p, big) { Scale = new Vector3(0.2f + 2.0f * random.NextSingle()) }));
                ids.Add(store.Spawn(Drawn, new EntitySpawn(p, tiny)));
            }
            store.SyncTransforms();

            // orthographic: a caster stays when the diameter of its aabb sphere is at least MinDiameter
            var ortho = new InstanceCullRequest { ShadowCasters = true, MinDiameter = 1.5f };
            HashSet<uint> kept = Cull(store, ortho);
            var expected = ids.Where(id => 2.0f * store.GetBounds(id).GetExtents().Length() >= 1.5f).Select(store.GetSlot).ToHashSet();
            Assert.True(expected.Count > 500 && expected.Count < 3000);
            Assert.True(expected.SetEquals(kept));
            Assert.Equal(1, ortho.ChunksSkippedSmall); // the tiny batch's chunk, whole
            Assert.Equal(6000 - expected.Count, ortho.RowsSkippedSmall);

            // perspective: at least MinDiameterPerDistance times its distance to the eye, and inside the range sphere
            var eye = new Vector3(10, 5, -20);
            var face = new InstanceCullRequest
            {
                ShadowCasters = true, HasSphere = true, SphereCenter = eye, SphereRadius = 90.0f,
                SizeEye = eye, MinDiameterPerDistance = 0.02f
            };
            kept = Cull(store, face);
            expected = new HashSet<uint>();
            foreach (EntityId id in ids)
            {
                BoundingBox box = store.GetBounds(id);
                Vector3 closest = Vector3.Clamp(eye, box.GetMin(), box.GetMax());
                float distance = Vector3.Distance(closest, eye);
                if (distance <= 90.0f && 2.0f * box.GetExtents().Length() >= 0.02f * distance)
                    expected.Add(store.GetSlot(id));
            }
            Assert.True(expected.Count > 200);
            Assert.True(expected.SetEquals(kept));

            // no threshold: everything
            Assert.Equal(6000, Cull(store, new InstanceCullRequest { ShadowCasters = true }).Count);
        });
    }

    [Fact]
    public void A_plane_mask_tests_only_its_planes()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch batch = UnitBatch();
            EntityId near = store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0, -150), batch)); // past the near plane (z = -far: the gl style clip plane)
            EntityId inside = store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0, 10), batch));
            EntityId far = store.Spawn(Drawn, new EntitySpawn(new Vector3(0, 0, 200), batch));   // past the far plane
            store.SyncTransforms();
            var frustum = new Frustum(Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY),
                                      Matrix4x4Extensions.CreateOrthoOffCenterLH(-5, 5, -5, 5, 0.0f, 100.0f));
            var all = new InstanceCullRequest();
            all.SetFrustum(frustum);
            Assert.Equal(new[] { store.GetSlot(inside) }.ToHashSet(), Cull(store, all));
            var sides = new InstanceCullRequest();
            sides.SetFrustum(frustum, ignore_depth: true);
            Assert.Equal(new[] { store.GetSlot(near), store.GetSlot(inside), store.GetSlot(far) }.ToHashSet(), Cull(store, sides));
            for (int p = 0; p < 2; p++)
            {
                var one = new InstanceCullRequest();
                one.SetFrustum(frustum, ignore_depth: true);
                one.PlaneMask |= 1 << p;
                Assert.Equal(2, Cull(store, one).Count); // one depth plane: the entity on its far side goes
            }
        });
    }

    private static HashSet<uint> Cull(EntityStore store, InstanceCullRequest request)
    {
        store.CullInstances(new[] { request });
        store.FinishCulls(new[] { request, new InstanceCullRequest() }.Take(1).ToList());
        return request.Slots.Take(request.Total).ToHashSet();
    }

    private sealed class SumJob : ParallelJob
    {
        public long Sum;
        public int Nested;
        public int Throw = -1;
        public readonly HashSet<int> Threads = new();
        public override void Execute(int index)
        {
            if (index == Throw)
                throw new InvalidOperationException("job failed");
            Interlocked.Add(ref Sum, index);
            lock (Threads)
                Threads.Add(Environment.CurrentManagedThreadId);
            if (Nested > 0 && index < 4)
            {
                var inner = new SumJob();
                WorkerPool.ParallelLoop(inner, Nested); // runs inline on this thread
                Interlocked.Add(ref Sum, inner.Sum * 1000000);
            }
        }
    }

    [Fact]
    public void The_worker_pool_runs_every_index_once_inline_when_nested_and_rethrows()
    {
        var job = new SumJob();
        WorkerPool.ParallelLoop(job, 100000);
        Assert.Equal(100000L * 99999 / 2, job.Sum);
        Assert.True(WorkerPool.ThreadCount >= 1);

        var nested = new SumJob { Nested = 10 };
        WorkerPool.ParallelLoop(nested, 100);
        Assert.Equal(100L * 99 / 2 + 4 * 45L * 1000000, nested.Sum);

        var failing = new SumJob { Throw = 777 };
        Assert.Throws<InvalidOperationException>(() => WorkerPool.ParallelLoop(failing, 5000));
        var after = new SumJob();
        WorkerPool.ParallelLoop(after, 1000); // still usable
        Assert.Equal(1000L * 999 / 2, after.Sum);
    }
}
