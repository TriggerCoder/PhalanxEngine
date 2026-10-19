// The transform pass of the store (World.Tick's last step): world matrices, world aabbs and packed instance transforms
// of the chunks that changed, in parallel over chunks, depth by depth so parents are done before their children. Roots
// are computed 8 at a time with Vector256 (scale * rotation * translation, the formula of
// System.Numerics.Matrix4x4.CreateFromQuaternion); children multiply by their parent's world matrix.
// Spartan @ 4c07baa updates each Entity eagerly on every change (Entity::UpdateTransform, Entity.cpp:1056), the world
// entities still do (Entity.cs); the store defers to this one pass per tick.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
                    if (chunk.Dirty || chunk.LayersDirty)
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
                    if (chunk.Count > 0 && (chunk.Dirty || chunk.LayersDirty || (depth > 0 && parents_changed)))
                    {
                        m_sync_work.Add(chunk);
                        rows += chunk.Count;
                    }
                }
            }
            if (m_sync_work.Count == 0)
            {
                if (depth > 0)
                    parents_changed = false;
                continue;
            }
            int changed = 0;
            bool level_parents_changed = parents_changed;
            if (rows < 8192 || m_sync_work.Count == 1)
            {
                foreach (EntityChunk chunk in m_sync_work)
                    if (SyncChunk(chunk, depth, version, level_parents_changed))
                        changed = 1;
            }
            else
            {
                Parallel.For(0, m_sync_work.Count, i =>
                {
                    if (SyncChunk(m_sync_work[i], depth, version, level_parents_changed))
                        Volatile.Write(ref changed, 1);
                });
            }
            // a level whose parents didn't change can still have dirty children of its own
            parents_changed = changed != 0 || (depth == 0 && parents_changed);
        }
    }

    // returns whether a world matrix changed
    private bool SyncChunk(EntityChunk chunk, int depth, uint version, bool parents_changed)
    {
        bool changed = false;
        if (depth == 0)
        {
            if (chunk.Dirty)
            {
                SyncRoots(chunk, version);
                changed = true;
            }
        }
        else if (chunk.Dirty || parents_changed)
            changed = SyncChildren(chunk, version, chunk.Dirty);
        chunk.Dirty = false;
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
    private static Vector256<float> Gather(ReadOnlySpan<float> s, int i, int stride)
    {
        // the caller keeps i + 7 * stride inside the span (rows i / stride .. + 7 exist)
        ref float r = ref Unsafe.Add(ref MemoryMarshal.GetReference(s), i);
        return Vector256.Create(r, Unsafe.Add(ref r, stride), Unsafe.Add(ref r, 2 * stride), Unsafe.Add(ref r, 3 * stride),
                                Unsafe.Add(ref r, 4 * stride), Unsafe.Add(ref r, 5 * stride), Unsafe.Add(ref r, 6 * stride), Unsafe.Add(ref r, 7 * stride));
    }

    private void SyncRoots(EntityChunk chunk, uint version)
    {
        int count = chunk.Count, n = chunk.Capacity;
        float[] w = chunk.World;
        ref float world = ref MemoryMarshal.GetArrayDataReference(w);
        ReadOnlySpan<float> pos = MemoryMarshal.Cast<Vector3, float>(chunk.Position.AsSpan(0, count));
        ReadOnlySpan<float> rot = MemoryMarshal.Cast<Quaternion, float>(chunk.Rotation.AsSpan(0, count));
        ReadOnlySpan<float> scl = MemoryMarshal.Cast<Vector3, float>(chunk.Scale.AsSpan(0, count));
        uint[]? slots = chunk.Slot;
        PackedTransform[] instances = Instances;
        uint[] stamps = SlotBlockStamp;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> one = Vector256.Create(1.0f);
            Vector256<float> snorm = Vector256.Create(32767.0f), minus_one = Vector256.Create(-1.0f);
            Span<int> packed = stackalloc int[16]; // rotation_xy, rotation_zw of 8 rows
            for (; i + 8 <= count; i += 8)
            {
                Vector256<float> x = Gather(rot, 4 * i, 4), y = Gather(rot, 4 * i + 1, 4), z = Gather(rot, 4 * i + 2, 4), qw = Gather(rot, 4 * i + 3, 4);
                Vector256<float> xx = x * x, yy = y * y, zz = z * z;
                Vector256<float> xy = x * y, wz = z * qw, xz = z * x, wy = y * qw, yz = y * z, wx = x * qw;
                Vector256<float> sx = Gather(scl, 3 * i, 3), sy = Gather(scl, 3 * i + 1, 3), sz = Gather(scl, 3 * i + 2, 3);
                nuint o = (nuint)i, c = (nuint)n;
                ((one - (yy + zz) * 2.0f) * sx).StoreUnsafe(ref world, o);
                ((xy + wz) * 2.0f * sx).StoreUnsafe(ref world, o + c);
                ((xz - wy) * 2.0f * sx).StoreUnsafe(ref world, o + 2 * c);
                ((xy - wz) * 2.0f * sy).StoreUnsafe(ref world, o + 3 * c);
                ((one - (zz + xx) * 2.0f) * sy).StoreUnsafe(ref world, o + 4 * c);
                ((yz + wx) * 2.0f * sy).StoreUnsafe(ref world, o + 5 * c);
                ((xz + wy) * 2.0f * sz).StoreUnsafe(ref world, o + 6 * c);
                ((yz - wx) * 2.0f * sz).StoreUnsafe(ref world, o + 7 * c);
                ((one - (yy + xx) * 2.0f) * sz).StoreUnsafe(ref world, o + 8 * c);
                Gather(pos, 3 * i, 3).StoreUnsafe(ref world, o + 9 * c);
                Gather(pos, 3 * i + 1, 3).StoreUnsafe(ref world, o + 10 * c);
                Gather(pos, 3 * i + 2, 3).StoreUnsafe(ref world, o + 11 * c);
                if (slots == null)
                    continue;
                // PackedTransform.Pack, 8 at a time: normalize, clamp, snorm16 round to even
                Vector256<float> inv = one / Vector256.Sqrt(xx + yy + zz + qw * qw);
                Vector256<int> px = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(x * inv, minus_one), one) * snorm));
                Vector256<int> py = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(y * inv, minus_one), one) * snorm));
                Vector256<int> pz = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(z * inv, minus_one), one) * snorm));
                Vector256<int> pw = Vector256.ConvertToInt32(Vector256.Round(Vector256.Min(Vector256.Max(qw * inv, minus_one), one) * snorm));
                Vector256<int> low = Vector256.Create(0xFFFF);
                ((px & low) | (py << 16)).CopyTo(packed);
                ((pz & low) | (pw << 16)).CopyTo(packed.Slice(8));
                for (int l = 0; l < 8; l++)
                {
                    int r = i + l;
                    uint slot = slots[r];
                    ref PackedTransform t = ref instances[slot];
                    Vector3 p = chunk.Position[r], sc = chunk.Scale[r];
                    t.pos_x = p.X; t.pos_y = p.Y; t.pos_z = p.Z;
                    t.rotation_xy = (uint)packed[l];
                    t.rotation_zw = (uint)packed[8 + l];
                    t.scale_x = sc.X; t.scale_y = sc.Y; t.scale_z = sc.Z;
                    stamps[slot / SlotBlockSize] = version;
                }
            }
        }
        int vector_end = i;
        for (; i < count; i++)
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
        }
        Array.Fill(chunk.Changed, version, 0, count);

        if (chunk.Bounds == null || slots == null)
            return;
        ComputeBounds(chunk, 0, count);
        for (int r = vector_end; r < count; r++)
        {
            uint slot = slots[r];
            instances[slot] = PackedTransform.Pack(chunk.Position[r], chunk.Rotation[r], chunk.Scale[r]);
            stamps[slot / SlotBlockSize] = version;
        }
    }

    // the world aabbs of rows [start, end) and the chunk's merged aabb (from every row)
    private static void ComputeBounds(EntityChunk chunk, int start, int end)
    {
        RenderBatches.Info batch = RenderBatches.GetInfo(chunk.Archetype.Key.Batch);
        Vector3 lc = batch.LocalCenter, le = batch.LocalExtent * (1.0f + BoundsSlack);
        int n = chunk.Capacity;
        float[] w = chunk.World, b = chunk.Bounds!;
        ref float world = ref MemoryMarshal.GetArrayDataReference(w);
        ref float bounds = ref MemoryMarshal.GetArrayDataReference(b);
        int i = start;
        if (Vector256.IsHardwareAccelerated)
        {
            nuint c = (nuint)n;
            Vector256<float> cx = Vector256.Create(lc.X), cy = Vector256.Create(lc.Y), cz = Vector256.Create(lc.Z);
            Vector256<float> ex = Vector256.Create(le.X), ey = Vector256.Create(le.Y), ez = Vector256.Create(le.Z);
            for (; i + 8 <= end; i += 8)
            {
                nuint o = (nuint)i;
                Vector256<float> m11 = Vector256.LoadUnsafe(ref world, o),         m12 = Vector256.LoadUnsafe(ref world, o + c),      m13 = Vector256.LoadUnsafe(ref world, o + 2 * c);
                Vector256<float> m21 = Vector256.LoadUnsafe(ref world, o + 3 * c), m22 = Vector256.LoadUnsafe(ref world, o + 4 * c),  m23 = Vector256.LoadUnsafe(ref world, o + 5 * c);
                Vector256<float> m31 = Vector256.LoadUnsafe(ref world, o + 6 * c), m32 = Vector256.LoadUnsafe(ref world, o + 7 * c),  m33 = Vector256.LoadUnsafe(ref world, o + 8 * c);
                Vector256<float> m41 = Vector256.LoadUnsafe(ref world, o + 9 * c), m42 = Vector256.LoadUnsafe(ref world, o + 10 * c), m43 = Vector256.LoadUnsafe(ref world, o + 11 * c);
                (cx * m11 + cy * m21 + cz * m31 + m41).StoreUnsafe(ref bounds, o);
                (cx * m12 + cy * m22 + cz * m32 + m42).StoreUnsafe(ref bounds, o + c);
                (cx * m13 + cy * m23 + cz * m33 + m43).StoreUnsafe(ref bounds, o + 2 * c);
                (ex * Vector256.Abs(m11) + ey * Vector256.Abs(m21) + ez * Vector256.Abs(m31)).StoreUnsafe(ref bounds, o + 3 * c);
                (ex * Vector256.Abs(m12) + ey * Vector256.Abs(m22) + ez * Vector256.Abs(m32)).StoreUnsafe(ref bounds, o + 4 * c);
                (ex * Vector256.Abs(m13) + ey * Vector256.Abs(m23) + ez * Vector256.Abs(m33)).StoreUnsafe(ref bounds, o + 5 * c);
            }
        }
        for (; i < end; i++)
        {
            float m11 = w[i], m12 = w[n + i], m13 = w[2 * n + i], m21 = w[3 * n + i], m22 = w[4 * n + i], m23 = w[5 * n + i];
            float m31 = w[6 * n + i], m32 = w[7 * n + i], m33 = w[8 * n + i];
            b[i]         = lc.X * m11 + lc.Y * m21 + lc.Z * m31 + w[9 * n + i];
            b[n + i]     = lc.X * m12 + lc.Y * m22 + lc.Z * m32 + w[10 * n + i];
            b[2 * n + i] = lc.X * m13 + lc.Y * m23 + lc.Z * m33 + w[11 * n + i];
            b[3 * n + i] = le.X * MathF.Abs(m11) + le.Y * MathF.Abs(m21) + le.Z * MathF.Abs(m31);
            b[4 * n + i] = le.X * MathF.Abs(m12) + le.Y * MathF.Abs(m22) + le.Z * MathF.Abs(m32);
            b[5 * n + i] = le.X * MathF.Abs(m13) + le.Y * MathF.Abs(m23) + le.Z * MathF.Abs(m33);
        }
        MergeChunkBounds(chunk);
    }

    private static void MergeChunkBounds(EntityChunk chunk)
    {
        int count = chunk.Count, n = chunk.Capacity;
        float[] b = chunk.Bounds!;
        ref float bounds = ref MemoryMarshal.GetArrayDataReference(b);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && count >= 8)
        {
            nuint c = (nuint)n;
            Vector256<float> min_x = Vector256.Create(float.MaxValue), min_y = min_x, min_z = min_x;
            Vector256<float> max_x = Vector256.Create(float.MinValue), max_y = max_x, max_z = max_x;
            for (; i + 8 <= count; i += 8)
            {
                nuint o = (nuint)i;
                Vector256<float> px = Vector256.LoadUnsafe(ref bounds, o), py = Vector256.LoadUnsafe(ref bounds, o + c), pz = Vector256.LoadUnsafe(ref bounds, o + 2 * c);
                Vector256<float> qx = Vector256.LoadUnsafe(ref bounds, o + 3 * c), qy = Vector256.LoadUnsafe(ref bounds, o + 4 * c), qz = Vector256.LoadUnsafe(ref bounds, o + 5 * c);
                min_x = Vector256.Min(min_x, px - qx); min_y = Vector256.Min(min_y, py - qy); min_z = Vector256.Min(min_z, pz - qz);
                max_x = Vector256.Max(max_x, px + qx); max_y = Vector256.Max(max_y, py + qy); max_z = Vector256.Max(max_z, pz + qz);
            }
            for (int l = 0; l < 8; l++)
            {
                min = Vector3.Min(min, new Vector3(min_x[l], min_y[l], min_z[l]));
                max = Vector3.Max(max, new Vector3(max_x[l], max_y[l], max_z[l]));
            }
        }
        for (; i < count; i++)
        {
            var center = new Vector3(b[i], b[n + i], b[2 * n + i]);
            var extent = new Vector3(b[3 * n + i], b[4 * n + i], b[5 * n + i]);
            min = Vector3.Min(min, center - extent);
            max = Vector3.Max(max, center + extent);
        }
        chunk.BoundsMin   = min;
        chunk.BoundsMax   = max;
        chunk.BoundsValid = count > 0;
    }

    private bool SyncChildren(EntityChunk chunk, uint version, bool all)
    {
        int count = chunk.Count;
        bool changed = false;
        PackedTransform[] instances = Instances;
        uint[] stamps = SlotBlockStamp;
        for (int i = 0; i < count; i++)
        {
            uint parent = chunk.Parent[i];
            EntityChunk? parent_chunk = parent != uint.MaxValue ? m_chunk[parent] : null;
            int parent_row = parent_chunk != null ? m_row[parent] : 0;
            if (!all && (parent_chunk == null || parent_chunk.Changed[parent_row] != version))
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
                stamps[slot / SlotBlockSize] = version;
            }
        }
        if (changed && chunk.Bounds != null)
            ComputeBounds(chunk, 0, count);
        return changed;
    }
}
