// The transform pass of the store (World.Tick's last step): world matrices, world aabbs and packed instance transforms
// of the rows that changed, in parallel over chunks (WorkerPool), depth by depth so parents are done before their
// children. Roots are one fused pass 8 rows at a time with Vector256: the local transforms are deinterleaved from their
// Vector3 / Quaternion columns with permutes (AVX2), scale * rotation * translation is built in registers (the formula
// of System.Numerics.Matrix4x4.CreateFromQuaternion) and written once as the world columns, the world aabbs, the
// chunk's merged aabb and the 32 byte packed instances (an 8x8 transpose, stored straight into Instances when the 8
// slots are consecutive); children multiply by their parent's world matrix.
// A chunk recomputes every row when a span of its local transforms was handed out (ChunkView.Positions...), only the
// range of rows set one at a time otherwise (EntityChunk.MarkRowDirty), and nothing when none changed: static entities
// cost nothing. Every changed instance (a whole dirty chunk's, or a set row's: EntityChunk.DirtyBits) stamps its group
// of SlotGroupSize slots with the sync version, the renderer uploads the groups stamped since its buffers were last
// written (Renderer.Instancing.cs).
// Spartan @ 4c07baa updates each Entity eagerly on every change (Entity::UpdateTransform, Entity.cpp:1056), the world
// entities still do (Entity.cs); the store defers to this one pass per tick.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Phalanx.Math;

namespace Phalanx.World;

public sealed partial class EntityStore
{
    private uint m_sync_version;
    private bool m_managed_changed;
    private readonly List<EntityChunk> m_sync_work = new();

    /// <summary>The number of transform syncs so far; a row's Changed stamp is the sync that last moved it.</summary>
    internal uint SyncVersion => m_sync_version;

    // aabbs are widened by this fraction so the snorm16 rotation of the packed instance stays inside them
    private const float BoundsSlack = 1e-4f;

    // whether a transform sync would change anything
    internal bool NeedsSync
    {
        get
        {
            if (m_managed_changed)
                return true;
            foreach (Archetype archetype in m_archetype_list)
                foreach (EntityChunk chunk in archetype.Chunks)
                    if (chunk.HasDirtyRows || chunk.LayersDirty)
                        return true;
            return false;
        }
    }

    // whether a batch has live entities (the renderer uploads its mesh and writes its DrawData entry)
    internal bool HasLiveBatch(uint batch)
    {
        foreach (EntityChunk chunk in GetCullChunks())
            if (chunk.Archetype.Key.Batch == batch && chunk.Count > 0)
                return true;
        return false;
    }

    // every chunk recomputes at the next sync (benchmarks)
    internal void MarkAllDirty()
    {
        foreach (Archetype archetype in m_archetype_list)
            foreach (EntityChunk chunk in archetype.Chunks)
                chunk.Dirty = true;
    }

    /// <summary>Brings every world matrix, aabb and packed transform up to date with the local transforms.</summary>
    internal void SyncTransforms()
    {
        uint version = ++m_sync_version;
        bool parents_changed = m_managed_changed;
        m_managed_changed = false;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            m_sync_work.Clear();
            int rows = 0;
            foreach (Archetype archetype in m_by_depth[depth])
            {
                if (archetype.Key.Kind == ArchetypeKind.Managed)
                    continue;
                foreach (EntityChunk chunk in archetype.Chunks)
                {
                    if (chunk.Count > 0 && (chunk.HasDirtyRows || chunk.LayersDirty || (depth > 0 && parents_changed)))
                    {
                        m_sync_work.Add(chunk);
                        rows += chunk.Dirty || depth > 0 ? chunk.Count : System.Math.Max(0, System.Math.Min(chunk.DirtyEnd, chunk.Count) - chunk.DirtyStart);
                    }
                }
            }
            if (m_sync_work.Count == 0)
            {
                if (depth > 0)
                    parents_changed = false;
                continue;
            }
            m_sync_job.store           = this;
            m_sync_job.depth           = depth;
            m_sync_job.version         = version;
            m_sync_job.parents_changed = parents_changed;
            m_sync_job.changed         = 0;
            if (rows < 8192 || m_sync_work.Count == 1)
            {
                for (int i = 0; i < m_sync_work.Count; i++)
                    m_sync_job.Execute(i);
            }
            else
                WorkerPool.ParallelLoop(m_sync_job, m_sync_work.Count);
            // a level whose parents didn't change can still have dirty children of its own
            parents_changed = m_sync_job.changed != 0 || (depth == 0 && parents_changed);
        }
    }

    // one chunk of a depth level of the sync, cached: the loop allocates nothing
    private sealed class SyncJob : ParallelJob
    {
        public EntityStore store = null!;
        public int depth;
        public uint version;
        public bool parents_changed;
        public int changed;

        public override void Execute(int index)
        {
            if (store.SyncChunk(store.m_sync_work[index], depth, version, parents_changed))
                Volatile.Write(ref changed, 1);
        }
    }
    private readonly SyncJob m_sync_job = new();

    // returns whether a world matrix changed
    private bool SyncChunk(EntityChunk chunk, int depth, uint version, bool parents_changed)
    {
        bool changed = false;
        int start = chunk.Dirty ? 0 : chunk.DirtyStart;
        int end   = chunk.Dirty ? chunk.Count : System.Math.Min(chunk.DirtyEnd, chunk.Count);
        if (depth == 0)
        {
            if (end > start)
            {
                SyncRoots(chunk, start, end, version);
                changed = true;
            }
        }
        else if (end > start || parents_changed)
            changed = SyncChildren(chunk, version, start, end);
        chunk.ClearDirty();
        if (chunk.LayersDirty && chunk.CullLayers != null)
        {
            uint or = 0;
            for (int i = 0; i < chunk.Count; i++)
                or |= chunk.CullLayers[i];
            chunk.LayersOr    = or;
            chunk.LayersDirty = false;
        }
        return changed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Gather(ref float r, int stride) =>
        Vector256.Create(r, Unsafe.Add(ref r, stride), Unsafe.Add(ref r, 2 * stride), Unsafe.Add(ref r, 3 * stride),
                         Unsafe.Add(ref r, 4 * stride), Unsafe.Add(ref r, 5 * stride), Unsafe.Add(ref r, 6 * stride), Unsafe.Add(ref r, 7 * stride));

    // x y z of 8 consecutive Vector3 (24 floats from v)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadVector3x8(ref float v, out Vector256<float> x, out Vector256<float> y, out Vector256<float> z)
    {
        if (Avx2.IsSupported)
        {
            Vector256<float> a = Vector256.LoadUnsafe(ref v), b = Vector256.LoadUnsafe(ref v, 8), c = Vector256.LoadUnsafe(ref v, 16);
            x = Avx.Blend(Avx.Blend(Avx2.PermuteVar8x32(a, Vector256.Create(0, 3, 6, 0, 0, 0, 0, 0)), Avx2.PermuteVar8x32(b, Vector256.Create(0, 0, 0, 1, 4, 7, 0, 0)), 0b0011_1000),
                          Avx2.PermuteVar8x32(c, Vector256.Create(0, 0, 0, 0, 0, 0, 2, 5)), 0b1100_0000);
            y = Avx.Blend(Avx.Blend(Avx2.PermuteVar8x32(a, Vector256.Create(1, 4, 7, 0, 0, 0, 0, 0)), Avx2.PermuteVar8x32(b, Vector256.Create(0, 0, 0, 2, 5, 0, 0, 0)), 0b0001_1000),
                          Avx2.PermuteVar8x32(c, Vector256.Create(0, 0, 0, 0, 0, 0, 3, 6)), 0b1110_0000);
            z = Avx.Blend(Avx.Blend(Avx2.PermuteVar8x32(a, Vector256.Create(2, 5, 0, 0, 0, 0, 0, 0)), Avx2.PermuteVar8x32(b, Vector256.Create(0, 0, 0, 3, 6, 0, 0, 0)), 0b0001_1100),
                          Avx2.PermuteVar8x32(c, Vector256.Create(0, 0, 0, 0, 0, 1, 4, 7)), 0b1110_0000);
            return;
        }
        x = Gather(ref v, 3);
        y = Gather(ref Unsafe.Add(ref v, 1), 3);
        z = Gather(ref Unsafe.Add(ref v, 2), 3);
    }

    // x y z w of 8 consecutive Quaternion (32 floats from q)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadQuaternionx8(ref float q, out Vector256<float> x, out Vector256<float> y, out Vector256<float> z, out Vector256<float> w)
    {
        if (Avx2.IsSupported)
        {
            Vector256<float> q0 = Vector256.LoadUnsafe(ref q), q1 = Vector256.LoadUnsafe(ref q, 8), q2 = Vector256.LoadUnsafe(ref q, 16), q3 = Vector256.LoadUnsafe(ref q, 24);
            Vector256<float> t0 = Avx.UnpackLow(q0, q1), t1 = Avx.UnpackHigh(q0, q1), t2 = Avx.UnpackLow(q2, q3), t3 = Avx.UnpackHigh(q2, q3);
            Vector256<int> order = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7); // the shuffles leave rows 0 2 4 6 | 1 3 5 7
            x = Avx2.PermuteVar8x32(Avx.Shuffle(t0, t2, 0x44), order);
            y = Avx2.PermuteVar8x32(Avx.Shuffle(t0, t2, 0xEE), order);
            z = Avx2.PermuteVar8x32(Avx.Shuffle(t1, t3, 0x44), order);
            w = Avx2.PermuteVar8x32(Avx.Shuffle(t1, t3, 0xEE), order);
            return;
        }
        x = Gather(ref q, 4);
        y = Gather(ref Unsafe.Add(ref q, 1), 4);
        z = Gather(ref Unsafe.Add(ref q, 2), 4);
        w = Gather(ref Unsafe.Add(ref q, 3), 4);
    }

    // 8 rows of 8 components (r0..r7 one component of 8 instances each) into 8 PackedTransform at destination
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePacked8(Vector256<float> r0, Vector256<float> r1, Vector256<float> r2, Vector256<float> r3,
                                     Vector256<float> r4, Vector256<float> r5, Vector256<float> r6, Vector256<float> r7,
                                     ref PackedTransform instances, ReadOnlySpan<uint> slots)
    {
        Vector256<float> t0 = Avx.UnpackLow(r0, r1), t1 = Avx.UnpackHigh(r0, r1), t2 = Avx.UnpackLow(r2, r3), t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5), t5 = Avx.UnpackHigh(r4, r5), t6 = Avx.UnpackLow(r6, r7), t7 = Avx.UnpackHigh(r6, r7);
        Vector256<float> u0 = Avx.Shuffle(t0, t2, 0x44), u1 = Avx.Shuffle(t0, t2, 0xEE), u2 = Avx.Shuffle(t1, t3, 0x44), u3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> u4 = Avx.Shuffle(t4, t6, 0x44), u5 = Avx.Shuffle(t4, t6, 0xEE), u6 = Avx.Shuffle(t5, t7, 0x44), u7 = Avx.Shuffle(t5, t7, 0xEE);
        Vector256<float> e0 = Avx.Permute2x128(u0, u4, 0x20), e1 = Avx.Permute2x128(u1, u5, 0x20), e2 = Avx.Permute2x128(u2, u6, 0x20), e3 = Avx.Permute2x128(u3, u7, 0x20);
        Vector256<float> e4 = Avx.Permute2x128(u0, u4, 0x31), e5 = Avx.Permute2x128(u1, u5, 0x31), e6 = Avx.Permute2x128(u2, u6, 0x31), e7 = Avx.Permute2x128(u3, u7, 0x31);
        uint first = slots[0];
        if (slots[7] - first == 7 && slots[1] - first == 1 && slots[2] - first == 2 && slots[3] - first == 3 && slots[4] - first == 4 && slots[5] - first == 5 && slots[6] - first == 6)
        {
            ref float d = ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)first));
            e0.StoreUnsafe(ref d); e1.StoreUnsafe(ref d, 8); e2.StoreUnsafe(ref d, 16); e3.StoreUnsafe(ref d, 24);
            e4.StoreUnsafe(ref d, 32); e5.StoreUnsafe(ref d, 40); e6.StoreUnsafe(ref d, 48); e7.StoreUnsafe(ref d, 56);
            return;
        }
        e0.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[0])));
        e1.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[1])));
        e2.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[2])));
        e3.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[3])));
        e4.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[4])));
        e5.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[5])));
        e6.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[6])));
        e7.StoreUnsafe(ref Unsafe.As<PackedTransform, float>(ref Unsafe.Add(ref instances, (nint)slots[7])));
    }

    // the rows [start, end) of a root chunk: world columns, changed stamps and, renderable, world aabbs, packed
    // instances and their group stamps, then the chunk's merged aabb
    private void SyncRoots(EntityChunk chunk, int start, int end, uint version)
    {
        int n = chunk.Capacity;
        float[] w = chunk.World;
        ref float world = ref MemoryMarshal.GetArrayDataReference(w);
        ref float pos = ref Unsafe.As<Vector3, float>(ref MemoryMarshal.GetArrayDataReference(chunk.Position));
        ref float rot = ref Unsafe.As<Quaternion, float>(ref MemoryMarshal.GetArrayDataReference(chunk.Rotation));
        ref float scl = ref Unsafe.As<Vector3, float>(ref MemoryMarshal.GetArrayDataReference(chunk.Scale));
        ref uint changed = ref MemoryMarshal.GetArrayDataReference(chunk.Changed);
        uint[]? slots = chunk.Slot;
        float[]? b = chunk.Bounds;
        PackedTransform[] instances = Instances;
        uint[] stamps = SlotGroupStamp;
        Vector3 lc = default, le = default;
        float lr = 0.0f;
        if (b != null)
        {
            RenderBatches.Info batch = RenderBatches.GetInfo(chunk.Archetype.Key.Batch);
            lc = batch.LocalCenter;
            le = batch.LocalExtent * (1.0f + BoundsSlack);
            lr = batch.LocalExtent.Length();
        }
        bool whole = start == 0 && end == chunk.Count;
        bool stamp_all = chunk.Dirty; // otherwise only the rows set restamp (the others of the range don't change)
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        float radius2 = 0.0f;
        int i = start;
        if (Vector256.IsHardwareAccelerated && Avx2.IsSupported)
        {
            Vector256<float> one = Vector256.Create(1.0f), two = Vector256.Create(2.0f);
            Vector256<float> snorm = Vector256.Create(32767.0f), minus_one = Vector256.Create(-1.0f);
            Vector256<int> low = Vector256.Create(0xFFFF);
            Vector256<uint> stamp = Vector256.Create(version);
            Vector256<float> cx = Vector256.Create(lc.X), cy = Vector256.Create(lc.Y), cz = Vector256.Create(lc.Z);
            Vector256<float> ex = Vector256.Create(le.X), ey = Vector256.Create(le.Y), ez = Vector256.Create(le.Z);
            Vector256<float> er = Vector256.Create(lr);
            Vector256<float> min_x = Vector256.Create(float.MaxValue), min_y = min_x, min_z = min_x;
            Vector256<float> max_x = Vector256.Create(float.MinValue), max_y = max_x, max_z = max_x;
            Vector256<float> max_r2 = Vector256<float>.Zero;
            ref float bounds = ref b != null ? ref MemoryMarshal.GetArrayDataReference(b) : ref world;
            ref PackedTransform instance_ref = ref MemoryMarshal.GetArrayDataReference(instances);
            nuint c = (nuint)n;
            for (; i + 8 <= end; i += 8)
            {
                LoadQuaternionx8(ref Unsafe.Add(ref rot, 4 * i), out Vector256<float> x, out Vector256<float> y, out Vector256<float> z, out Vector256<float> qw);
                LoadVector3x8(ref Unsafe.Add(ref scl, 3 * i), out Vector256<float> sx, out Vector256<float> sy, out Vector256<float> sz);
                LoadVector3x8(ref Unsafe.Add(ref pos, 3 * i), out Vector256<float> px, out Vector256<float> py, out Vector256<float> pz);
                Vector256<float> xx = x * x, yy = y * y, zz = z * z;
                Vector256<float> xy = x * y, wz = z * qw, xz = z * x, wy = y * qw, yz = y * z, wx = x * qw;
                Vector256<float> m11 = (one - (yy + zz) * two) * sx, m12 = (xy + wz) * two * sx, m13 = (xz - wy) * two * sx;
                Vector256<float> m21 = (xy - wz) * two * sy, m22 = (one - (zz + xx) * two) * sy, m23 = (yz + wx) * two * sy;
                Vector256<float> m31 = (xz + wy) * two * sz, m32 = (yz - wx) * two * sz, m33 = (one - (yy + xx) * two) * sz;
                nuint o = (nuint)i;
                m11.StoreUnsafe(ref world, o);         m12.StoreUnsafe(ref world, o + c);      m13.StoreUnsafe(ref world, o + 2 * c);
                m21.StoreUnsafe(ref world, o + 3 * c); m22.StoreUnsafe(ref world, o + 4 * c);  m23.StoreUnsafe(ref world, o + 5 * c);
                m31.StoreUnsafe(ref world, o + 6 * c); m32.StoreUnsafe(ref world, o + 7 * c);  m33.StoreUnsafe(ref world, o + 8 * c);
                px.StoreUnsafe(ref world, o + 9 * c);  py.StoreUnsafe(ref world, o + 10 * c);  pz.StoreUnsafe(ref world, o + 11 * c);
                stamp.StoreUnsafe(ref changed, o);
                if (b == null)
                    continue;

                // the world aabb (ComputeBounds' formula) and the chunk's
                Vector256<float> bx = cx * m11 + cy * m21 + cz * m31 + px, by = cx * m12 + cy * m22 + cz * m32 + py, bz = cx * m13 + cy * m23 + cz * m33 + pz;
                Vector256<float> bex = ex * Vector256.Abs(m11) + ey * Vector256.Abs(m21) + ez * Vector256.Abs(m31);
                Vector256<float> bey = ex * Vector256.Abs(m12) + ey * Vector256.Abs(m22) + ez * Vector256.Abs(m32);
                Vector256<float> bez = ex * Vector256.Abs(m13) + ey * Vector256.Abs(m23) + ez * Vector256.Abs(m33);
                bx.StoreUnsafe(ref bounds, o); by.StoreUnsafe(ref bounds, o + c); bz.StoreUnsafe(ref bounds, o + 2 * c);
                bex.StoreUnsafe(ref bounds, o + 3 * c); bey.StoreUnsafe(ref bounds, o + 4 * c); bez.StoreUnsafe(ref bounds, o + 5 * c);
                (er * Vector256.Max(Vector256.Max(Vector256.Abs(sx), Vector256.Abs(sy)), Vector256.Abs(sz))).StoreUnsafe(ref bounds, o + 6 * c);
                min_x = Vector256.Min(min_x, bx - bex); min_y = Vector256.Min(min_y, by - bey); min_z = Vector256.Min(min_z, bz - bez);
                max_x = Vector256.Max(max_x, bx + bex); max_y = Vector256.Max(max_y, by + bey); max_z = Vector256.Max(max_z, bz + bez);
                max_r2 = Vector256.Max(max_r2, bex * bex + bey * bey + bez * bez);

                // PackedTransform.Pack, 8 at a time: normalize, clamp, snorm16 round to even
                Vector256<float> inv = one / Vector256.Sqrt(xx + yy + zz + qw * qw);
                Vector256<int> rx = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(x * inv, minus_one), one) * snorm));
                Vector256<int> ry = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(y * inv, minus_one), one) * snorm));
                Vector256<int> rz = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(z * inv, minus_one), one) * snorm));
                Vector256<int> rw = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(qw * inv, minus_one), one) * snorm));
                ReadOnlySpan<uint> row_slots = new ReadOnlySpan<uint>(slots, i, 8);
                StorePacked8(px, py, pz, ((rx & low) | (ry << 16)).AsSingle(), ((rz & low) | (rw << 16)).AsSingle(), sx, sy, sz, ref instance_ref, row_slots);
                uint first = row_slots[0], last = row_slots[7];
                if (!stamp_all)
                    for (uint set = chunk.RowDirtyBits8(i); set != 0; set &= set - 1)
                        stamps[row_slots[BitOperations.TrailingZeroCount(set)] >> SlotGroupShift] = version;
                else if (last - first == 7)
                {
                    stamps[first >> SlotGroupShift] = version;
                    stamps[last >> SlotGroupShift]  = version;
                }
                else
                    foreach (uint slot in row_slots)
                        stamps[slot >> SlotGroupShift] = version;
            }
            if (b != null)
                for (int l = 0; l < 8; l++)
                {
                    min = Vector3.Min(min, new Vector3(min_x[l], min_y[l], min_z[l]));
                    max = Vector3.Max(max, new Vector3(max_x[l], max_y[l], max_z[l]));
                    radius2 = MathF.Max(radius2, max_r2[l]);
                }
        }
        int vector_end = i;
        for (; i < end; i++)
        {
            Quaternion q = chunk.Rotation[i];
            Vector3 s = chunk.Scale[i];
            float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
            float xy = q.X * q.Y, wz = q.Z * q.W, xz = q.Z * q.X, wy = q.Y * q.W, yz = q.Y * q.Z, wx = q.X * q.W;
            w[i]         = (1.0f - (yy + zz) * 2.0f) * s.X;
            w[n + i]     = (xy + wz) * 2.0f * s.X;
            w[2 * n + i] = (xz - wy) * 2.0f * s.X;
            w[3 * n + i] = (xy - wz) * 2.0f * s.Y;
            w[4 * n + i] = (1.0f - (zz + xx) * 2.0f) * s.Y;
            w[5 * n + i] = (yz + wx) * 2.0f * s.Y;
            w[6 * n + i] = (xz + wy) * 2.0f * s.Z;
            w[7 * n + i] = (yz - wx) * 2.0f * s.Z;
            w[8 * n + i] = (1.0f - (yy + xx) * 2.0f) * s.Z;
            Vector3 p = chunk.Position[i];
            w[9 * n + i] = p.X; w[10 * n + i] = p.Y; w[11 * n + i] = p.Z;
            chunk.Changed[i] = version;
        }
        if (b == null || slots == null)
            return;
        ComputeBounds(chunk, vector_end, end);
        for (int r = vector_end; r < end; r++)
        {
            uint slot = slots[r];
            instances[slot] = PackedTransform.Pack(chunk.Position[r], chunk.Rotation[r], chunk.Scale[r]);
            if (stamp_all || chunk.IsRowDirty(r))
                stamps[slot >> SlotGroupShift] = version;
        }
        if (whole)
        {
            // the vector rows' merged aabb plus the tail's
            for (int r = vector_end; r < end; r++)
            {
                var center = new Vector3(b[r], b[n + r], b[2 * n + r]);
                var extent = new Vector3(b[3 * n + r], b[4 * n + r], b[5 * n + r]);
                min = Vector3.Min(min, center - extent);
                max = Vector3.Max(max, center + extent);
                radius2 = MathF.Max(radius2, extent.LengthSquared());
            }
            chunk.BoundsMin        = min;
            chunk.BoundsMax        = max;
            chunk.BoundsValid      = chunk.Count > 0;
            chunk.MaxRadiusSquared = radius2;
        }
        else
            MergeChunkBounds(chunk); // the other rows' aabbs are unchanged, the merge reads them all
    }

    // the world aabbs of rows [start, end)
    private static void ComputeBounds(EntityChunk chunk, int start, int end)
    {
        RenderBatches.Info batch = RenderBatches.GetInfo(chunk.Archetype.Key.Batch);
        Vector3 lc = batch.LocalCenter, le = batch.LocalExtent * (1.0f + BoundsSlack);
        float lr = batch.LocalExtent.Length();
        int n = chunk.Capacity;
        float[] w = chunk.World, b = chunk.Bounds!;
        for (int i = start; i < end; i++)
        {
            float m11 = w[i], m12 = w[n + i], m13 = w[2 * n + i], m21 = w[3 * n + i], m22 = w[4 * n + i], m23 = w[5 * n + i];
            float m31 = w[6 * n + i], m32 = w[7 * n + i], m33 = w[8 * n + i];
            b[i]         = lc.X * m11 + lc.Y * m21 + lc.Z * m31 + w[9 * n + i];
            b[n + i]     = lc.X * m12 + lc.Y * m22 + lc.Z * m32 + w[10 * n + i];
            b[2 * n + i] = lc.X * m13 + lc.Y * m23 + lc.Z * m33 + w[11 * n + i];
            b[3 * n + i] = le.X * MathF.Abs(m11) + le.Y * MathF.Abs(m21) + le.Z * MathF.Abs(m31);
            b[4 * n + i] = le.X * MathF.Abs(m12) + le.Y * MathF.Abs(m22) + le.Z * MathF.Abs(m32);
            b[5 * n + i] = le.X * MathF.Abs(m13) + le.Y * MathF.Abs(m23) + le.Z * MathF.Abs(m33);
            float scale2 = MathF.Max(MathF.Max(m11 * m11 + m12 * m12 + m13 * m13, m21 * m21 + m22 * m22 + m23 * m23), m31 * m31 + m32 * m32 + m33 * m33);
            b[6 * n + i] = lr * MathF.Sqrt(scale2);
        }
    }

    private static void MergeChunkBounds(EntityChunk chunk)
    {
        int count = chunk.Count, n = chunk.Capacity;
        float[] b = chunk.Bounds!;
        ref float bounds = ref MemoryMarshal.GetArrayDataReference(b);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        float radius2 = 0.0f;
        int i = 0;
        if (Vector256.IsHardwareAccelerated && count >= 8)
        {
            nuint c = (nuint)n;
            Vector256<float> min_x = Vector256.Create(float.MaxValue), min_y = min_x, min_z = min_x;
            Vector256<float> max_x = Vector256.Create(float.MinValue), max_y = max_x, max_z = max_x;
            Vector256<float> max_r2 = Vector256<float>.Zero;
            for (; i + 8 <= count; i += 8)
            {
                nuint o = (nuint)i;
                Vector256<float> px = Vector256.LoadUnsafe(ref bounds, o), py = Vector256.LoadUnsafe(ref bounds, o + c), pz = Vector256.LoadUnsafe(ref bounds, o + 2 * c);
                Vector256<float> qx = Vector256.LoadUnsafe(ref bounds, o + 3 * c), qy = Vector256.LoadUnsafe(ref bounds, o + 4 * c), qz = Vector256.LoadUnsafe(ref bounds, o + 5 * c);
                min_x = Vector256.Min(min_x, px - qx); min_y = Vector256.Min(min_y, py - qy); min_z = Vector256.Min(min_z, pz - qz);
                max_x = Vector256.Max(max_x, px + qx); max_y = Vector256.Max(max_y, py + qy); max_z = Vector256.Max(max_z, pz + qz);
                max_r2 = Vector256.Max(max_r2, qx * qx + qy * qy + qz * qz);
            }
            for (int l = 0; l < 8; l++)
            {
                min = Vector3.Min(min, new Vector3(min_x[l], min_y[l], min_z[l]));
                max = Vector3.Max(max, new Vector3(max_x[l], max_y[l], max_z[l]));
                radius2 = MathF.Max(radius2, max_r2[l]);
            }
        }
        for (; i < count; i++)
        {
            var center = new Vector3(b[i], b[n + i], b[2 * n + i]);
            var extent = new Vector3(b[3 * n + i], b[4 * n + i], b[5 * n + i]);
            min = Vector3.Min(min, center - extent);
            max = Vector3.Max(max, center + extent);
            radius2 = MathF.Max(radius2, extent.LengthSquared());
        }
        chunk.BoundsMin        = min;
        chunk.BoundsMax        = max;
        chunk.BoundsValid      = count > 0;
        chunk.MaxRadiusSquared = radius2;
    }

    // the rows [start, end) always, the others whose parent's world matrix changed in this sync
    private bool SyncChildren(EntityChunk chunk, uint version, int start, int end)
    {
        int count = chunk.Count;
        bool changed = false;
        PackedTransform[] instances = Instances;
        uint[] stamps = SlotGroupStamp;
        for (int i = 0; i < count; i++)
        {
            uint parent = chunk.Parent[i];
            EntityChunk? parent_chunk = parent != uint.MaxValue ? m_chunk[parent] : null;
            int parent_row = parent_chunk != null ? m_row[parent] : 0;
            bool own = i >= start && i < end;
            if (!own && (parent_chunk == null || parent_chunk.Changed[parent_row] != version))
                continue;
            Matrix4x4 world = Matrix4x4Extensions.CreateTRS(chunk.Position[i], chunk.Rotation[i], chunk.Scale[i]);
            if (parent_chunk != null)
                world *= parent_chunk.GetWorld(parent_row);
            chunk.SetWorld(i, world);
            chunk.Changed[i] = version;
            changed = true;
            if (chunk.Bounds != null)
            {
                uint slot = chunk.Slot![i];
                instances[slot] = PackedTransform.Pack(world);
                if (!own || chunk.Dirty || chunk.IsRowDirty(i))
                    stamps[slot >> SlotGroupShift] = version;
            }
        }
        if (changed && chunk.Bounds != null)
        {
            ComputeBounds(chunk, 0, count);
            MergeChunkBounds(chunk);
        }
        return changed;
    }
}
