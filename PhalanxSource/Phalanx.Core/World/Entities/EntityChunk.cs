// The storage of the entity store: archetypes (one component set, hierarchy depth, kind and render batch) and their
// chunks of up to 16384 entities, every per entity value in its own column (structure of arrays) so the batched
// behaviours, the transform pass and the culling stream through contiguous memory.
using System.Numerics;

namespace Phalanx.World;

internal enum ArchetypeKind : byte
{
    Plain,      // a store entity that isn't drawn
    Renderable, // a store entity drawn instanced (RenderBatch, layers, bounds, instance slot)
    Managed,    // the store row of a world (class) Entity: its world matrix is mirrored, the class owns the transform
}

[Flags]
internal enum RowFlags : byte
{
    None         = 0,
    Enabled      = 1 << 0,
    CastsShadows = 1 << 1,
    Callbacks    = 1 << 2, // a facade with IPreDraw or IOnBecameVisible behaviours: culled through the callbacks
}

internal readonly record struct ArchetypeKey(ulong Mask, byte Depth, ArchetypeKind Kind, uint Batch);

internal sealed class Archetype
{
    public readonly ArchetypeKey Key;
    public readonly int[] ComponentIds;
    public readonly List<EntityChunk> Chunks = new();
    public readonly int Index; // creation order, deterministic

    public Archetype(ArchetypeKey key, int index)
    {
        Key   = key;
        Index = index;
        var ids = new List<int>();
        for (int i = 0; i < ComponentRegistry.MaxTypes; i++)
            if ((key.Mask & (1ul << i)) != 0)
                ids.Add(i);
        ComponentIds = ids.ToArray();
    }

    public bool IsRenderable => Key.Kind == ArchetypeKind.Renderable;
    public int Count
    {
        get
        {
            int count = 0;
            foreach (EntityChunk chunk in Chunks)
                count += chunk.Count;
            return count;
        }
    }

    // the chunk a new row goes in: the last one while it has room (it grows up to the chunk size), else a new one
    public EntityChunk GetChunkWithRoom()
    {
        if (Chunks.Count > 0)
        {
            EntityChunk last = Chunks[^1];
            if (last.Count < EntityChunk.MaxCapacity)
            {
                if (last.Count == last.Capacity)
                    last.Grow();
                return last;
            }
        }
        var chunk = new EntityChunk(this, Chunks.Count == 0 ? EntityChunk.InitialCapacity : EntityChunk.MaxCapacity);
        Chunks.Add(chunk);
        return chunk;
    }
}

internal sealed class EntityChunk
{
    public const int MaxCapacity     = 16384;
    public const int InitialCapacity = 64;
    public const int WorldColumns    = 12; // the 3x4 world matrix: m11 m12 m13 m21 m22 m23 m31 m32 m33 m41 m42 m43
    // world aabb: center xyz, extents xyz; then the radius of the bounding sphere the views' contribution cull
    // measures (spartan's |local aabb extent| * largest world scale, which a rotation doesn't change)
    public const int BoundsColumns   = 7;

    public readonly Archetype Archetype;
    public int Capacity;
    public int Count;

    public EntityId[] Ids;
    public Vector3[] Position;
    public Quaternion[] Rotation;
    public Vector3[] Scale;
    public float[] World;     // WorldColumns columns of Capacity floats
    public uint[] Parent;     // the parent's entity index, uint.MaxValue for none
    public uint[] Changed;    // the transform sync that last changed the world matrix
    public RowFlags[] Flags;
    public readonly Array?[] Components = new Array?[ComponentRegistry.MaxTypes];

    // renderable archetypes
    public float[]? Bounds;   // BoundsColumns columns of Capacity floats
    public uint[]? Layers;    // the render layers
    public uint[]? CullLayers; // the render layers while enabled, 0 while disabled (the culling's first test)
    public uint[]? Slot;      // the instance slot (EntityStore.Instances)

    // chunk wide state, for the passes to skip whole chunks
    public bool Dirty = true;  // every row's local transform may have changed since the last transform sync
    public int DirtyStart = int.MaxValue, DirtyEnd; // otherwise the rows [DirtyStart, DirtyEnd) did (one row's set)
    public ulong[] DirtyBits; // and of those exactly the rows set (one bit per row): only they restamp their slot group
    public bool LayersDirty;
    public uint LayersOr;      // every row's CullLayers or'd (conservative until the next transform sync)
    public Vector3 BoundsMin, BoundsMax; // every row's world aabb merged (conservative until the next sync)
    public bool BoundsValid;
    public float MaxRadiusSquared;       // the largest row's |aabb extent|^2 (the shadow culling's size threshold)
    public int EnabledCount;
    public int CasterCount;
    public int CallbackCount;

    public EntityChunk(Archetype archetype, int capacity)
    {
        Archetype = archetype;
        Capacity  = capacity;
        Ids       = new EntityId[capacity];
        Position  = new Vector3[capacity];
        Rotation  = new Quaternion[capacity];
        Scale     = new Vector3[capacity];
        World     = new float[WorldColumns * capacity];
        Parent    = new uint[capacity];
        Changed   = new uint[capacity];
        Flags     = new RowFlags[capacity];
        DirtyBits = new ulong[(capacity + 63) >> 6];
        foreach (int id in archetype.ComponentIds)
            Components[id] = ComponentRegistry.Get(id).Create(capacity);
        if (archetype.IsRenderable)
        {
            Bounds     = new float[BoundsColumns * capacity];
            Layers     = new uint[capacity];
            CullLayers = new uint[capacity];
            Slot       = new uint[capacity];
        }
    }

    public void Grow()
    {
        int capacity = System.Math.Min(Capacity * 2, MaxCapacity);
        Array.Resize(ref Ids, capacity);
        Array.Resize(ref Position, capacity);
        Array.Resize(ref Rotation, capacity);
        Array.Resize(ref Scale, capacity);
        World = ResizeColumns(World, WorldColumns, Capacity, capacity);
        Array.Resize(ref Parent, capacity);
        Array.Resize(ref Changed, capacity);
        Array.Resize(ref Flags, capacity);
        Array.Resize(ref DirtyBits, (capacity + 63) >> 6);
        foreach (int id in Archetype.ComponentIds)
            Components[id] = ComponentRegistry.Get(id).Resize(Components[id]!, Count, capacity);
        if (Bounds != null)
        {
            Bounds = ResizeColumns(Bounds, BoundsColumns, Capacity, capacity);
            Array.Resize(ref Layers, capacity);
            Array.Resize(ref CullLayers, capacity);
            Array.Resize(ref Slot, capacity);
        }
        Capacity = capacity;
    }

    private float[] ResizeColumns(float[] columns, int column_count, int old_capacity, int capacity)
    {
        var resized = new float[column_count * capacity];
        for (int c = 0; c < column_count; c++)
            Array.Copy(columns, c * old_capacity, resized, c * capacity, Count);
        return resized;
    }

    public T[] GetColumn<T>() where T : unmanaged, IComponentData
    {
        if (Components[ComponentId<T>.Value] is T[] column)
            return column;
        throw new InvalidOperationException($"The entities of this chunk have no {typeof(T).Name}");
    }

    // copies row source_row of source into row row of this chunk (the columns both have; the rest is cleared)
    public void CopyRow(int row, EntityChunk source, int source_row)
    {
        Ids[row]      = source.Ids[source_row];
        Position[row] = source.Position[source_row];
        Rotation[row] = source.Rotation[source_row];
        Scale[row]    = source.Scale[source_row];
        for (int c = 0; c < WorldColumns; c++)
            World[c * Capacity + row] = source.World[c * source.Capacity + source_row];
        Parent[row]  = source.Parent[source_row];
        Changed[row] = source.Changed[source_row];
        Flags[row]   = source.Flags[source_row];
        foreach (int id in Archetype.ComponentIds)
        {
            ComponentInfo info = ComponentRegistry.Get(id);
            if (source.Components[id] is Array from)
                info.Copy(from, source_row, Components[id]!, row);
            else
                info.Clear(Components[id]!, row);
        }
        if (Bounds != null)
        {
            if (source.Bounds != null)
            {
                for (int c = 0; c < BoundsColumns; c++)
                    Bounds[c * Capacity + row] = source.Bounds[c * source.Capacity + source_row];
                Layers![row]     = source.Layers![source_row];
                CullLayers![row] = source.CullLayers![source_row];
                Slot![row]       = source.Slot![source_row];
            }
            else
            {
                Layers![row]     = 1u;
                CullLayers![row] = (Flags[row] & RowFlags.Enabled) != 0 ? 1u : 0u;
                Slot![row]       = uint.MaxValue;
            }
        }
    }

    public void CountRow(int row, int sign)
    {
        RowFlags flags = Flags[row];
        if ((flags & RowFlags.Enabled) != 0) EnabledCount += sign;
        if ((flags & RowFlags.CastsShadows) != 0 && (flags & RowFlags.Enabled) != 0) CasterCount += sign;
        if ((flags & RowFlags.Callbacks) != 0) CallbackCount += sign;
        if (sign > 0 && CullLayers != null)
            LayersOr |= CullLayers[row];
        if (sign < 0)
            LayersDirty = true;
    }

    /// <summary>Whether the next transform sync has rows of this chunk to recompute.</summary>
    public bool HasDirtyRows => Dirty || DirtyEnd > DirtyStart;

    /// <summary>One row's local transform changed: only it is recomputed (a span of the column marks them all).</summary>
    public void MarkRowDirty(int row)
    {
        if (row < DirtyStart) DirtyStart = row;
        if (row + 1 > DirtyEnd) DirtyEnd = row + 1;
        DirtyBits[row >> 6] |= 1ul << (row & 63);
    }

    public bool IsRowDirty(int row) => (DirtyBits[row >> 6] & (1ul << (row & 63))) != 0;

    // the dirty bits of the 8 rows from row on, bit l row + l
    public uint RowDirtyBits8(int row)
    {
        int word = row >> 6, bit = row & 63;
        ulong value = DirtyBits[word] >> bit;
        if (bit > 56 && word + 1 < DirtyBits.Length)
            value |= DirtyBits[word + 1] << (64 - bit);
        return (uint)value & 0xFFu;
    }

    public void ClearDirty()
    {
        if (DirtyEnd > DirtyStart)
            Array.Clear(DirtyBits, DirtyStart >> 6, ((DirtyEnd - 1) >> 6) - (DirtyStart >> 6) + 1);
        Dirty      = false;
        DirtyStart = int.MaxValue;
        DirtyEnd   = 0;
    }

    public Matrix4x4 GetWorld(int row)
    {
        int n = Capacity;
        float[] w = World;
        return new Matrix4x4(w[row],         w[n + row],     w[2 * n + row],  0,
                             w[3 * n + row], w[4 * n + row], w[5 * n + row],  0,
                             w[6 * n + row], w[7 * n + row], w[8 * n + row],  0,
                             w[9 * n + row], w[10 * n + row], w[11 * n + row], 1);
    }

    public void SetWorld(int row, in Matrix4x4 m)
    {
        int n = Capacity;
        float[] w = World;
        w[row] = m.M11;         w[n + row] = m.M12;      w[2 * n + row] = m.M13;
        w[3 * n + row] = m.M21; w[4 * n + row] = m.M22;  w[5 * n + row] = m.M23;
        w[6 * n + row] = m.M31; w[7 * n + row] = m.M32;  w[8 * n + row] = m.M33;
        w[9 * n + row] = m.M41; w[10 * n + row] = m.M42; w[11 * n + row] = m.M43;
    }
}
