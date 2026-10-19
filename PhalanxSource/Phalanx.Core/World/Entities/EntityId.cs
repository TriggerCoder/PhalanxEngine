// The handles and descriptors of the entity store (EntityStore.cs): an 8 byte generational entity id, the component
// data marker, the archetype a store entity is spawned with and what it starts as. Not in SpartanEngine, whose
// entities are always heap objects (source/world/Entity.h @ 4c07baa); see the S1 design notes (EntityStore.cs).
using System.Numerics;

namespace Phalanx.World;

/// <summary>
/// A store entity: an index into the store's records and the generation that index had when the entity was made, so
/// an id kept after its entity was destroyed never finds the entity that reused the index. 8 bytes, a value type,
/// default is <see cref="Null"/>.
/// </summary>
public readonly struct EntityId : IEquatable<EntityId>
{
    public readonly uint Index;
    public readonly uint Generation; // 0 only for Null

    internal EntityId(uint index, uint generation)
    {
        Index      = index;
        Generation = generation;
    }

    public static EntityId Null => default;
    public bool IsNull => Generation == 0;

    public bool Equals(EntityId other) => Index == other.Index && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public override int GetHashCode() => (int)(Index * 0x9E3779B1u ^ Generation);
    public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
    public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);
    public override string ToString() => IsNull ? "EntityId(null)" : $"EntityId({Index}:{Generation})";
}

/// <summary>Marks a struct as component data of store entities: plain data, stored in chunk columns, read and written by
/// <see cref="BatchedBehaviour"/>s. Unmanaged only (no references), at most 64 types per process.</summary>
public interface IComponentData { }

/// <summary>
/// Which components a store entity has and whether it is drawn instanced (<see cref="Renderable"/>). Built once and
/// reused: <c>EntityArchetype.Empty.With&lt;Velocity&gt;().With&lt;Spin&gt;().Renderable()</c>. It is also the query of a
/// <see cref="BatchedBehaviour"/>, which then runs on every entity that has at least these components.
/// </summary>
public readonly struct EntityArchetype : IEquatable<EntityArchetype>
{
    internal readonly ulong Mask;
    internal readonly bool IsRenderable;

    internal EntityArchetype(ulong mask, bool renderable)
    {
        Mask         = mask;
        IsRenderable = renderable;
    }

    /// <summary>No components, not drawn: a transform only.</summary>
    public static EntityArchetype Empty => default;

    public EntityArchetype With<T>() where T : unmanaged, IComponentData => new(Mask | ComponentId<T>.Bit, IsRenderable);
    public EntityArchetype Without<T>() where T : unmanaged, IComponentData => new(Mask & ~ComponentId<T>.Bit, IsRenderable);

    /// <summary>The entities are drawn: each has a <see cref="RenderBatch"/> (mesh and material), render layers and a
    /// shadow flag, and is drawn instanced with every other entity of its batch.</summary>
    public EntityArchetype Renderable(bool renderable = true) => new(Mask, renderable);

    public bool Has<T>() where T : unmanaged, IComponentData => (Mask & ComponentId<T>.Bit) != 0;

    public bool Equals(EntityArchetype other) => Mask == other.Mask && IsRenderable == other.IsRenderable;
    public override bool Equals(object? obj) => obj is EntityArchetype other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Mask, IsRenderable);
    public static bool operator ==(EntityArchetype a, EntityArchetype b) => a.Equals(b);
    public static bool operator !=(EntityArchetype a, EntityArchetype b) => !a.Equals(b);
}

/// <summary>What a store entity starts as (<see cref="EntityStore.Spawn(EntityArchetype, in EntitySpawn)"/>).</summary>
public struct EntitySpawn
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;
    /// <summary>The parent, a store entity or the <see cref="Entity.Id"/> of a world entity; Null for a root. The
    /// transform is local to it.</summary>
    public EntityId Parent;
    /// <summary>The mesh and material of a renderable archetype's entity (ignored otherwise).</summary>
    public RenderBatch Batch;
    /// <summary>The render layers (Godot's layers, see <see cref="Render.RenderLayers"/>); layer 1 by default.</summary>
    public uint Layers;
    public bool CastsShadows;
    public bool Enabled;

    public EntitySpawn()
    {
        Position     = Vector3.Zero;
        Rotation     = Quaternion.Identity;
        Scale        = Vector3.One;
        Parent       = EntityId.Null;
        Batch        = default;
        Layers       = 1u;
        CastsShadows = true;
        Enabled      = true;
    }

    public EntitySpawn(Vector3 position, RenderBatch batch = default) : this()
    {
        Position = position;
        Batch    = batch;
    }
}
