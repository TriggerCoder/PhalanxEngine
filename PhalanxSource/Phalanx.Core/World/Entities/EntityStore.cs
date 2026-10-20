// The entity store of S1 (PhalanxEngine-design-2m-entities.md §9): millions of entities as rows of chunked columns,
// next to the World's class entities. Not in SpartanEngine @ 4c07baa, where every entity is a heap Entity with a
// component array and an eager transform (source/world/Entity.cpp); the class Entity stays for unique, main thread
// objects and is a facade over a store row when one is asked for (World.GetEntity).
// Design:
// - EntityId: index + generation, 8 bytes; a destroyed entity's index is reused with the next generation, so stale ids
//   are detected (IsAlive). Removal swaps the chunk's last row into the hole.
// - Archetype: component mask, hierarchy depth, kind (plain, renderable, managed) and render batch; its chunks hold up to
//   16384 rows (the first grows from 64). The depth in the key lets the transform pass walk parents before children,
//   the batch in the key makes every renderable chunk one instancing batch.
// - Structural changes (spawn, destroy, components, parent, batch) are main thread only; batched behaviours queue them
//   in their thread's CommandBuffer, played back in a deterministic order after each phase.
// - Store entities are enabled or not on their own (no inherited activity, unlike Entity.GetActive), and a world
//   (class) entity can't be the child of a store entity, only the other way round.
using System.Numerics;
using Phalanx.Math;

namespace Phalanx.World;

public sealed partial class EntityStore
{
    public const int MaxDepth = 16;

    // the records, by entity index: where the entity's row is and the index's current generation
    private EntityChunk?[] m_chunk      = new EntityChunk?[1024];
    private int[]          m_row        = new int[1024];
    private uint[]         m_generation = new uint[1024];
    private uint m_record_count;
    private readonly Queue<uint> m_free = new();
    private int m_count;

    private readonly Dictionary<ArchetypeKey, Archetype> m_archetypes = new();
    internal readonly List<Archetype> m_archetype_list = new();
    internal readonly List<Archetype>[] m_by_depth = new List<Archetype>[MaxDepth];
    internal int m_layout_version; // an archetype or chunk was added or removed
    private readonly Dictionary<uint, Entity> m_facades = new();
    internal bool m_jobs_running;

    public EntityStore()
    {
        for (int i = 0; i < MaxDepth; i++)
            m_by_depth[i] = new List<Archetype>();
    }

    /// <summary>The live store entities (including the rows of world entities that have an <see cref="Entity.Id"/>).</summary>
    public int Count => m_count;

    private void AssertCanChange()
    {
        if (m_jobs_running)
            throw new InvalidOperationException("Structural store changes aren't allowed while batched behaviours run; use the context's CommandBuffer");
        World.AssertMainThread();
    }

    // inside a render callback the change waits for the next world tick, like the class entities' changes
    private static bool DeferIfRendering(Action change)
    {
        if (!World.IsInRenderCallback)
            return false;
        World.Defer(change);
        return true;
    }

    internal Archetype GetArchetype(ArchetypeKey key)
    {
        if (m_archetypes.TryGetValue(key, out Archetype? archetype))
            return archetype;
        archetype = new Archetype(key, m_archetype_list.Count);
        m_archetypes.Add(key, archetype);
        m_archetype_list.Add(archetype);
        m_by_depth[key.Depth].Add(archetype);
        m_layout_version++;
        return archetype;
    }

    private EntityChunk AddRow(Archetype archetype, out int row)
    {
        int chunks = archetype.Chunks.Count;
        EntityChunk? chunk = null;
        foreach (EntityChunk candidate in archetype.Chunks)
        {
            if (candidate.Count < EntityChunk.MaxCapacity)
            {
                chunk = candidate;
                break;
            }
        }
        if (chunk == null)
            chunk = archetype.GetChunkWithRoom();
        else if (chunk.Count == chunk.Capacity)
            chunk.Grow();
        if (archetype.Chunks.Count != chunks)
            m_layout_version++;
        row = chunk.Count++;
        return chunk;
    }

    // the record of an id, or false for a stale or null one
    internal bool TryLocate(EntityId id, out EntityChunk chunk, out int row)
    {
        if (id.Generation != 0 && id.Index < m_record_count && m_generation[id.Index] == id.Generation && m_chunk[id.Index] is EntityChunk c)
        {
            chunk = c;
            row   = m_row[id.Index];
            return true;
        }
        chunk = null!;
        row   = 0;
        return false;
    }

    private EntityChunk Locate(EntityId id, out int row)
    {
        if (!TryLocate(id, out EntityChunk chunk, out row))
            throw new ArgumentException($"{id} isn't a live entity", nameof(id));
        return chunk;
    }

    internal EntityChunk? ChunkOfIndex(uint index, out int row)
    {
        row = index < m_record_count ? m_row[index] : 0;
        return index < m_record_count ? m_chunk[index] : null;
    }

    internal EntityId IdOfIndex(uint index) => new(index, m_generation[index]);

    public bool IsAlive(EntityId id) => TryLocate(id, out _, out _);

    // every live entity in index order (tests, tools)
    internal IEnumerable<EntityId> LiveIds()
    {
        for (uint index = 0; index < m_record_count; index++)
            if (m_chunk[index] != null)
                yield return new EntityId(index, m_generation[index]);
    }

    // the instance slot of a renderable entity, uint.MaxValue otherwise
    internal uint GetSlot(EntityId id) { EntityChunk c = Locate(id, out int row); return c.Slot?[row] ?? uint.MaxValue; }

    private uint AllocateIndex()
    {
        if (m_free.Count > 0)
            return m_free.Dequeue();
        if (m_record_count == m_chunk.Length)
        {
            int size = m_chunk.Length * 2;
            Array.Resize(ref m_chunk, size);
            Array.Resize(ref m_row, size);
            Array.Resize(ref m_generation, size);
        }
        uint index = m_record_count++;
        m_generation[index] = 1;
        return index;
    }

    #region SPAWN AND DESTROY ======================================================================================
    /// <summary>A new entity of <paramref name="archetype"/> at the origin (enabled, layer 1, no batch).</summary>
    public EntityId Spawn(EntityArchetype archetype) => Spawn(archetype, new EntitySpawn());

    /// <summary>A new entity of <paramref name="archetype"/> as <paramref name="spawn"/> describes; its components start
    /// zeroed. Main thread, outside batched behaviours (they use <see cref="CommandBuffer.Spawn"/>).</summary>
    public EntityId Spawn(EntityArchetype archetype, in EntitySpawn spawn)
    {
        AssertCanChange();
        if (World.IsInRenderCallback)
            throw new InvalidOperationException("Spawning isn't allowed in render callbacks; use ViewContext.Defer");
        return SpawnRow(archetype.Mask, archetype.IsRenderable ? ArchetypeKind.Renderable : ArchetypeKind.Plain, spawn);
    }

    /// <summary>Spawns <paramref name="ids"/>.Length entities alike (see <see cref="Spawn(EntityArchetype, in EntitySpawn)"/>).</summary>
    public void Spawn(EntityArchetype archetype, in EntitySpawn spawn, Span<EntityId> ids)
    {
        AssertCanChange();
        for (int i = 0; i < ids.Length; i++)
            ids[i] = SpawnRow(archetype.Mask, archetype.IsRenderable ? ArchetypeKind.Renderable : ArchetypeKind.Plain, spawn);
    }

    internal EntityId SpawnRow(ulong mask, ArchetypeKind kind, in EntitySpawn spawn)
    {
        byte depth = 0;
        uint parent_index = uint.MaxValue;
        Matrix4x4 parent_world = Matrix4x4.Identity;
        if (!spawn.Parent.IsNull)
        {
            EntityChunk parent_chunk = Locate(spawn.Parent, out int parent_row);
            depth = (byte)(parent_chunk.Archetype.Key.Depth + 1);
            if (depth >= MaxDepth)
                throw new InvalidOperationException($"Store hierarchies are at most {MaxDepth} levels deep");
            parent_index = spawn.Parent.Index;
            parent_world = GetWorldMatrix(spawn.Parent);
        }
        uint batch = kind == ArchetypeKind.Renderable ? spawn.Batch.Id : 0u;
        Archetype archetype = GetArchetype(new ArchetypeKey(mask, depth, kind, batch));
        uint index = AllocateIndex();
        var id = new EntityId(index, m_generation[index]);
        EntityChunk chunk = AddRow(archetype, out int row);
        m_chunk[index] = chunk;
        m_row[index]   = row;

        chunk.Ids[row]      = id;
        chunk.Position[row] = spawn.Position;
        chunk.Rotation[row] = spawn.Rotation;
        chunk.Scale[row]    = spawn.Scale;
        chunk.Parent[row]   = parent_index;
        chunk.Changed[row]  = 0;
        chunk.SetWorld(row, Matrix4x4Extensions.CreateTRS(spawn.Position, spawn.Rotation, spawn.Scale) * parent_world);
        RowFlags flags = RowFlags.None;
        if (spawn.Enabled) flags |= RowFlags.Enabled;
        if (spawn.CastsShadows) flags |= RowFlags.CastsShadows;
        chunk.Flags[row] = flags;
        foreach (int component in archetype.ComponentIds)
            ComponentRegistry.Get(component).Clear(chunk.Components[component]!, row);
        if (chunk.Bounds != null)
        {
            chunk.Layers![row]     = spawn.Layers;
            chunk.CullLayers![row] = spawn.Enabled ? spawn.Layers : 0u;
            chunk.Slot![row]       = AllocateSlot();
        }
        chunk.CountRow(row, +1);
        chunk.MarkRowDirty(row);
        m_count++;
        return id;
    }

    /// <summary>Destroys the entity and its store descendants (a stale id is ignored). A facade's behaviours are
    /// disabled and destroyed first.</summary>
    public void Destroy(EntityId id)
    {
        AssertCanChange();
        if (DeferIfRendering(() => Destroy(id)))
            return;
        if (!TryLocate(id, out EntityChunk chunk, out int row))
            return;
        if (chunk.Archetype.Key.Kind == ArchetypeKind.Managed && m_facades.TryGetValue(id.Index, out Entity? owner) && owner.m_in_world)
        {
            World.RemoveEntity(owner); // the world entity goes the world's way, which comes back here
            return;
        }
        DestroyTree(id);
    }

    internal void DestroyTree(EntityId id)
    {
        if (!IsAlive(id))
            return;
        var doomed = new List<uint> { id.Index };
        CollectDescendants(doomed);
        foreach (uint index in doomed)
            DestroyRow(new EntityId(index, m_generation[index]));
    }

    // appends every store descendant of the indices in list (parents come before their children)
    private void CollectDescendants(List<uint> list)
    {
        var set = new HashSet<uint>(list);
        int min_depth = MaxDepth;
        foreach (uint index in list)
            if (m_chunk[index] is EntityChunk c)
                min_depth = System.Math.Min(min_depth, c.Archetype.Key.Depth);
        for (int depth = min_depth + 1; depth < MaxDepth; depth++)
        {
            foreach (Archetype archetype in m_by_depth[depth])
                foreach (EntityChunk chunk in archetype.Chunks)
                    for (int row = 0; row < chunk.Count; row++)
                        if (set.Contains(chunk.Parent[row]) && set.Add(chunk.Ids[row].Index))
                            list.Add(chunk.Ids[row].Index);
        }
    }

    private void DestroyRow(EntityId id)
    {
        if (!TryLocate(id, out EntityChunk chunk, out int row))
            return;
        if (m_facades.Remove(id.Index, out Entity? facade))
        {
            facade.m_store_id = EntityId.Null;
            if (facade.m_store_bound)
            {
                facade.m_in_world = false;
                World.DestroyFacadeBehaviours(facade);
            }
        }
        if (chunk.Slot != null)
            FreeSlot(chunk.Slot[row]);
        RemoveRow(chunk, row);
        m_chunk[id.Index] = null;
        uint generation = m_generation[id.Index] + 1;
        m_generation[id.Index] = generation == 0 ? 1 : generation;
        m_free.Enqueue(id.Index);
        m_count--;
    }

    private void RemoveRow(EntityChunk chunk, int row)
    {
        chunk.CountRow(row, -1);
        int last = chunk.Count - 1;
        if (row != last)
        {
            chunk.CopyRow(row, chunk, last);
            m_row[chunk.Ids[row].Index] = row;
            chunk.MarkRowDirty(row); // the moved row's instance and the chunk's merged bounds are redone
        }
        chunk.Count--;
        if (chunk.Count == 0)
        {
            chunk.Archetype.Chunks.Remove(chunk);
            m_layout_version++;
        }
    }

    // moves a row to another archetype, keeping the transform and the components both have
    private void MoveRow(EntityId id, ArchetypeKey key)
    {
        EntityChunk source = Locate(id, out int source_row);
        if (source.Archetype.Key == key)
            return;
        Archetype archetype = GetArchetype(key);
        EntityChunk destination = AddRow(archetype, out int row);
        destination.CopyRow(row, source, source_row);
        if (destination.Slot != null && source.Slot == null)
            destination.Slot[row] = AllocateSlot();
        if (source.Slot != null && destination.Slot == null)
            FreeSlot(source.Slot[source_row]);
        destination.CountRow(row, +1);
        destination.MarkRowDirty(row);
        RemoveRow(source, source_row);
        m_chunk[id.Index] = destination;
        m_row[id.Index]   = row;
    }

    /// <summary>Destroys every store entity (World.ClearScene).</summary>
    internal void Clear()
    {
        foreach (Entity facade in m_facades.Values.ToArray())
        {
            facade.m_store_id = EntityId.Null;
            if (facade.m_store_bound)
            {
                facade.m_in_world = false;
                World.DestroyFacadeBehaviours(facade);
            }
        }
        m_facades.Clear();
        foreach (Archetype archetype in m_archetype_list)
            archetype.Chunks.Clear();
        m_archetypes.Clear();
        m_archetype_list.Clear();
        foreach (List<Archetype> list in m_by_depth)
            list.Clear();
        Array.Clear(m_chunk);
        m_free.Clear();
        for (uint i = 0; i < m_record_count; i++)
        {
            uint generation = m_generation[i] + 1;
            m_generation[i] = generation == 0 ? 1 : generation;
            m_free.Enqueue(i);
        }
        m_count = 0;
        m_layout_version++;
        ClearSlots();
    }
    #endregion

    #region COMPONENTS =============================================================================================
    public bool Has<T>(EntityId id) where T : unmanaged, IComponentData =>
        TryLocate(id, out EntityChunk chunk, out _) && (chunk.Archetype.Key.Mask & ComponentId<T>.Bit) != 0;

    /// <summary>The entity's <typeparamref name="T"/>, by reference (valid until the next structural change).</summary>
    public ref T Get<T>(EntityId id) where T : unmanaged, IComponentData
    {
        EntityChunk chunk = Locate(id, out int row);
        return ref chunk.GetColumn<T>()[row];
    }

    public void Set<T>(EntityId id, in T value) where T : unmanaged, IComponentData
    {
        EntityChunk chunk = Locate(id, out int row);
        chunk.GetColumn<T>()[row] = value;
    }

    /// <summary>Gives the entity a <typeparamref name="T"/> (moving it to another archetype), or sets the one it has.</summary>
    public void Add<T>(EntityId id, in T value) where T : unmanaged, IComponentData
    {
        AssertCanChange();
        EntityChunk chunk = Locate(id, out _);
        ArchetypeKey key = chunk.Archetype.Key;
        if ((key.Mask & ComponentId<T>.Bit) == 0)
            MoveRow(id, key with { Mask = key.Mask | ComponentId<T>.Bit });
        Set(id, value);
    }

    public void Remove<T>(EntityId id) where T : unmanaged, IComponentData
    {
        AssertCanChange();
        EntityChunk chunk = Locate(id, out _);
        ArchetypeKey key = chunk.Archetype.Key;
        if ((key.Mask & ComponentId<T>.Bit) != 0)
            MoveRow(id, key with { Mask = key.Mask & ~ComponentId<T>.Bit });
    }

    // a component written from raw bytes (command buffer playback)
    internal void SetRaw(EntityId id, int component, ReadOnlySpan<byte> bytes)
    {
        if (!TryLocate(id, out EntityChunk chunk, out int row) || chunk.Components[component] is not Array column)
            return;
        ComponentRegistry.Get(component).Write(column, row, bytes);
    }

    public EntityArchetype GetArchetype(EntityId id)
    {
        EntityChunk chunk = Locate(id, out _);
        return new EntityArchetype(chunk.Archetype.Key.Mask, chunk.Archetype.IsRenderable);
    }
    #endregion

    #region TRANSFORM ==============================================================================================
    public Vector3 GetLocalPosition(EntityId id) { EntityChunk c = Locate(id, out int row); return c.Position[row]; }
    public Quaternion GetLocalRotation(EntityId id) { EntityChunk c = Locate(id, out int row); return c.Rotation[row]; }
    public Vector3 GetLocalScale(EntityId id) { EntityChunk c = Locate(id, out int row); return c.Scale[row]; }

    public void SetLocalPosition(EntityId id, Vector3 position)
    {
        if (DeferIfRendering(() => SetLocalPosition(id, position))) return;
        EntityChunk c = Locate(id, out int row);
        c.Position[row] = position;
        c.MarkRowDirty(row);
    }
    public void SetLocalRotation(EntityId id, Quaternion rotation)
    {
        if (DeferIfRendering(() => SetLocalRotation(id, rotation))) return;
        EntityChunk c = Locate(id, out int row);
        c.Rotation[row] = rotation;
        c.MarkRowDirty(row);
    }
    public void SetLocalScale(EntityId id, Vector3 scale)
    {
        if (DeferIfRendering(() => SetLocalScale(id, scale))) return;
        EntityChunk c = Locate(id, out int row);
        c.Scale[row] = scale;
        c.MarkRowDirty(row);
    }
    public void SetLocalTransform(EntityId id, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (DeferIfRendering(() => SetLocalTransform(id, position, rotation, scale))) return;
        EntityChunk c = Locate(id, out int row);
        c.Position[row] = position;
        c.Rotation[row] = rotation;
        c.Scale[row]    = scale;
        c.MarkRowDirty(row);
    }

    /// <summary>The entity's world matrix as of now (local changes since the last transform sync included).</summary>
    public Matrix4x4 GetWorldMatrix(EntityId id)
    {
        EntityChunk chunk = Locate(id, out int row);
        return GetWorldMatrix(chunk, row);
    }

    internal Matrix4x4 GetWorldMatrix(EntityChunk chunk, int row)
    {
        if (chunk.Archetype.Key.Kind == ArchetypeKind.Managed)
            return chunk.GetWorld(row);
        Matrix4x4 local = Matrix4x4Extensions.CreateTRS(chunk.Position[row], chunk.Rotation[row], chunk.Scale[row]);
        uint parent = chunk.Parent[row];
        if (parent == uint.MaxValue || m_chunk[parent] is not EntityChunk parent_chunk)
            return local;
        return local * GetWorldMatrix(parent_chunk, m_row[parent]);
    }

    /// <summary>The world matrix as of the last transform sync (what was culled and drawn).</summary>
    public Matrix4x4 GetSyncedWorldMatrix(EntityId id)
    {
        EntityChunk chunk = Locate(id, out int row);
        return chunk.GetWorld(row);
    }

    public EntityId GetParent(EntityId id)
    {
        EntityChunk chunk = Locate(id, out int row);
        uint parent = chunk.Parent[row];
        return parent == uint.MaxValue ? EntityId.Null : new EntityId(parent, m_generation[parent]);
    }

    /// <summary>Parents the entity (and its descendants) under <paramref name="parent"/> (Null: a root), keeping its local
    /// transform, like <see cref="Entity.SetParent"/>.</summary>
    public void SetParent(EntityId id, EntityId parent)
    {
        AssertCanChange();
        if (DeferIfRendering(() => SetParent(id, parent))) return;
        EntityChunk chunk = Locate(id, out int row);
        if (chunk.Archetype.Key.Kind == ArchetypeKind.Managed)
            throw new InvalidOperationException("A world entity is parented with Entity.SetParent");
        int depth = 0;
        if (!parent.IsNull)
        {
            if (parent == id)
                return;
            EntityChunk parent_chunk = Locate(parent, out _);
            for (EntityId ancestor = parent; !ancestor.IsNull; ancestor = GetParent(ancestor))
                if (ancestor == id)
                    throw new InvalidOperationException("The new parent is a descendant of the entity");
            depth = parent_chunk.Archetype.Key.Depth + 1;
        }
        var subtree = new List<uint> { id.Index };
        CollectDescendants(subtree);
        int old_depth = chunk.Archetype.Key.Depth;
        int max_depth = old_depth;
        foreach (uint index in subtree)
            max_depth = System.Math.Max(max_depth, m_chunk[index]!.Archetype.Key.Depth);
        if (max_depth - old_depth + depth >= MaxDepth)
            throw new InvalidOperationException($"Store hierarchies are at most {MaxDepth} levels deep");

        chunk.Parent[row] = parent.IsNull ? uint.MaxValue : parent.Index;
        chunk.MarkRowDirty(row);
        int shift = depth - old_depth;
        if (shift == 0)
            return;
        foreach (uint index in subtree)
        {
            EntityChunk c = m_chunk[index]!;
            ArchetypeKey key = c.Archetype.Key;
            MoveRow(new EntityId(index, m_generation[index]), key with { Depth = (byte)(key.Depth + shift) });
        }
    }
    #endregion

    #region STATE ==================================================================================================
    public bool IsEnabled(EntityId id) { EntityChunk c = Locate(id, out int row); return (c.Flags[row] & RowFlags.Enabled) != 0; }

    /// <summary>A disabled entity isn't drawn and batched behaviours see it as disabled (<see cref="ChunkView.IsEnabled"/>);
    /// its facade's behaviours are disabled.</summary>
    public void SetEnabled(EntityId id, bool enabled)
    {
        if (DeferIfRendering(() => SetEnabled(id, enabled))) return;
        EntityChunk c = Locate(id, out int row);
        if (((c.Flags[row] & RowFlags.Enabled) != 0) == enabled)
            return;
        c.CountRow(row, -1);
        c.Flags[row] = enabled ? c.Flags[row] | RowFlags.Enabled : c.Flags[row] & ~RowFlags.Enabled;
        if (c.CullLayers != null)
            c.CullLayers[row] = enabled ? c.Layers![row] : 0u;
        c.CountRow(row, +1);
        if (m_facades.TryGetValue(id.Index, out Entity? facade) && facade.m_store_bound)
            World.RefreshBehaviours(facade, recursive: false);
    }

    public uint GetLayers(EntityId id) { EntityChunk c = Locate(id, out int row); return c.Layers?[row] ?? 0u; }
    public void SetLayers(EntityId id, uint layers)
    {
        if (DeferIfRendering(() => SetLayers(id, layers))) return;
        EntityChunk c = Locate(id, out int row);
        if (c.Layers == null)
            throw new InvalidOperationException("The entity isn't renderable");
        c.Layers[row] = layers;
        c.CullLayers![row] = (c.Flags[row] & RowFlags.Enabled) != 0 ? layers : 0u;
        c.LayersOr |= c.CullLayers[row];
        c.LayersDirty = true;
    }

    public bool GetCastsShadows(EntityId id) { EntityChunk c = Locate(id, out int row); return (c.Flags[row] & RowFlags.CastsShadows) != 0; }
    public void SetCastsShadows(EntityId id, bool casts)
    {
        if (DeferIfRendering(() => SetCastsShadows(id, casts))) return;
        EntityChunk c = Locate(id, out int row);
        c.CountRow(row, -1);
        c.Flags[row] = casts ? c.Flags[row] | RowFlags.CastsShadows : c.Flags[row] & ~RowFlags.CastsShadows;
        c.CountRow(row, +1);
    }

    internal void SetCallbacks(EntityId id, bool callbacks)
    {
        if (!TryLocate(id, out EntityChunk c, out int row) || ((c.Flags[row] & RowFlags.Callbacks) != 0) == callbacks)
            return;
        c.CountRow(row, -1);
        c.Flags[row] = callbacks ? c.Flags[row] | RowFlags.Callbacks : c.Flags[row] & ~RowFlags.Callbacks;
        c.CountRow(row, +1);
    }

    public RenderBatch GetRenderBatch(EntityId id) { EntityChunk c = Locate(id, out _); return new RenderBatch(c.Archetype.Key.Batch); }

    /// <summary>Draws a renderable entity with another mesh and material (it moves to that batch's chunks).</summary>
    public void SetRenderBatch(EntityId id, RenderBatch batch)
    {
        AssertCanChange();
        if (DeferIfRendering(() => SetRenderBatch(id, batch))) return;
        EntityChunk c = Locate(id, out _);
        if (!c.Archetype.IsRenderable)
            throw new InvalidOperationException("The entity isn't renderable");
        MoveRow(id, c.Archetype.Key with { Batch = batch.Id });
    }

    /// <summary>The world aabb as of the last transform sync (renderable entities; empty otherwise).</summary>
    public BoundingBox GetBounds(EntityId id)
    {
        EntityChunk c = Locate(id, out int row);
        if (c.Bounds == null)
            return new BoundingBox(Vector3.Zero, Vector3.Zero);
        int n = c.Capacity;
        var center = new Vector3(c.Bounds[row], c.Bounds[n + row], c.Bounds[2 * n + row]);
        var extent = new Vector3(c.Bounds[3 * n + row], c.Bounds[4 * n + row], c.Bounds[5 * n + row]);
        return new BoundingBox(center - extent, center + extent);
    }

    /// <summary>The bounding sphere the views' contribution cull measures, as of the last transform sync: the world
    /// center of the mesh aabb and its |extent| times the largest world scale (renderable entities; empty otherwise).</summary>
    public Sphere GetCullSphere(EntityId id)
    {
        EntityChunk c = Locate(id, out int row);
        if (c.Bounds == null)
            return new Sphere(Vector3.Zero, 0.0f);
        int n = c.Capacity;
        return new Sphere(new Vector3(c.Bounds[row], c.Bounds[n + row], c.Bounds[2 * n + row]), c.Bounds[6 * n + row]);
    }
    #endregion

    #region FACADES ================================================================================================
    // the class Entity of a store entity, made on first request: the world entity itself for a managed row, else a
    // store bound facade (Entity.cs) whose transform, activity and Render read and write the row
    internal Entity? GetFacade(EntityId id)
    {
        if (!TryLocate(id, out EntityChunk chunk, out _))
            return null;
        if (m_facades.TryGetValue(id.Index, out Entity? facade))
            return facade;
        facade = new Entity(this, id, chunk.Archetype.IsRenderable);
        m_facades.Add(id.Index, facade);
        return facade;
    }

    internal bool HasFacade(EntityId id) => m_facades.ContainsKey(id.Index) && IsAlive(id);
    internal Entity? PeekFacade(uint index) => m_facades.TryGetValue(index, out Entity? facade) ? facade : null;

    // the row of a world entity (Entity.Id), its world matrix mirrored by Entity.UpdateTransform
    internal EntityId SpawnManaged(Entity entity)
    {
        EntityId id = SpawnRow(0, ArchetypeKind.Managed, new EntitySpawn());
        m_facades.Add(id.Index, entity);
        MirrorManaged(id, entity.GetMatrix());
        return id;
    }

    internal void MirrorManaged(EntityId id, in Matrix4x4 world)
    {
        if (!TryLocate(id, out EntityChunk chunk, out int row))
            return;
        chunk.SetWorld(row, world);
        chunk.Changed[row] = m_sync_version + 1; // the coming sync sees it changed, the children follow
        m_managed_changed = true;
    }
    #endregion
}
