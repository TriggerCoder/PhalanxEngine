// Batched behaviours: the per type logic of store entities, called once per chunk with the chunk's columns, in parallel
// over chunks, in the same fixed, update and late update phases as the class behaviours (World.Batched.cs). Not in
// SpartanEngine; the class Behaviour (Behaviour.cs) stays for unique main thread objects.
using System.Numerics;

namespace Phalanx.World;

/// <summary>Called every fixed step for every chunk the behaviour matches.</summary>
public interface IBatchedFixedUpdate { void FixedUpdate(in BatchContext context, in ChunkView chunk); }

/// <summary>Called every world tick for every chunk the behaviour matches.</summary>
public interface IBatchedUpdate { void Update(in BatchContext context, in ChunkView chunk); }

/// <summary>Called every world tick after every Update, for every chunk the behaviour matches.</summary>
public interface IBatchedLateUpdate { void LateUpdate(in BatchContext context, in ChunkView chunk); }

public enum BatchPhase : byte { FixedUpdate, Update, LateUpdate }

/// <summary>
/// The logic of every store entity that has the components of <see cref="Query"/>. Implement one or more of
/// <see cref="IBatchedFixedUpdate"/>, <see cref="IBatchedUpdate"/> and <see cref="IBatchedLateUpdate"/> and register it
/// with <see cref="World.RegisterBatched"/>. The callbacks run on worker threads, one chunk per call and several chunks
/// at once: write only the chunk you were given (its spans), keep shared state read only, and queue anything else
/// (spawns, destroys, other entities) in <see cref="BatchContext.Commands"/>. Disabled entities are in the chunk too,
/// <see cref="ChunkView.IsEnabled"/> tells them apart; whether a camera sees an entity doesn't matter.
/// </summary>
public abstract class BatchedBehaviour
{
    protected BatchedBehaviour(EntityArchetype query) { Query = query; }

    /// <summary>The components an entity needs for this behaviour to run on it (whether it is renderable doesn't matter).</summary>
    public EntityArchetype Query { get; }

    /// <summary>Lower runs first within a phase; equal orders run in registration order.</summary>
    public int Order { get; init; }

    /// <summary>False runs the chunks one after the other on the main thread (behaviours that aren't thread safe).</summary>
    public bool RunInParallel { get; init; } = true;

    /// <summary>A disabled behaviour isn't called.</summary>
    public bool Enabled { get; set; } = true;

    internal int m_registration;
}

/// <summary>A batched behaviour of the entities that have a <typeparamref name="T"/>.</summary>
public abstract class BatchedBehaviour<T> : BatchedBehaviour where T : unmanaged, IComponentData
{
    protected BatchedBehaviour() : base(EntityArchetype.Empty.With<T>()) { }
}

/// <summary>A batched behaviour of the entities that have a <typeparamref name="T1"/> and a <typeparamref name="T2"/>.</summary>
public abstract class BatchedBehaviour<T1, T2> : BatchedBehaviour where T1 : unmanaged, IComponentData where T2 : unmanaged, IComponentData
{
    protected BatchedBehaviour() : base(EntityArchetype.Empty.With<T1>().With<T2>()) { }
}

/// <summary>What a batched callback gets besides the chunk: the time step and its thread's command buffer.</summary>
public readonly struct BatchContext
{
    internal BatchContext(BatchPhase phase, float delta_time, double time, long tick, CommandBuffer commands)
    {
        Phase     = phase;
        DeltaTime = delta_time;
        Time      = time;
        Tick      = tick;
        Commands  = commands;
    }

    public BatchPhase Phase { get; }
    /// <summary>The fixed step in FixedUpdate, the tick's time step otherwise, in seconds.</summary>
    public float DeltaTime { get; }
    /// <summary>The simulated time since the world started ticking, in seconds.</summary>
    public double Time { get; }
    /// <summary>The world tick count.</summary>
    public long Tick { get; }
    /// <summary>Structural changes, played back on the main thread after this phase.</summary>
    public CommandBuffer Commands { get; }
}

/// <summary>One chunk of entities as a batched callback sees it: columns of <see cref="Count"/> rows.</summary>
public readonly struct ChunkView
{
    private readonly EntityChunk m_chunk;

    internal ChunkView(EntityChunk chunk) { m_chunk = chunk; }

    public int Count => m_chunk.Count;
    public ReadOnlySpan<EntityId> Entities => m_chunk.Ids.AsSpan(0, m_chunk.Count);
    public bool IsEnabled(int row) => (m_chunk.Flags[row] & RowFlags.Enabled) != 0;
    /// <summary>Whether every entity of the chunk is enabled (no need for <see cref="IsEnabled"/>).</summary>
    public bool AllEnabled => m_chunk.EnabledCount == m_chunk.Count;
    public bool IsRenderable => m_chunk.Archetype.IsRenderable;

    public bool Has<T>() where T : unmanaged, IComponentData => (m_chunk.Archetype.Key.Mask & ComponentId<T>.Bit) != 0;
    /// <summary>The chunk's <typeparamref name="T"/> column, writable.</summary>
    public Span<T> Get<T>() where T : unmanaged, IComponentData => m_chunk.GetColumn<T>().AsSpan(0, m_chunk.Count);

    /// <summary>The local positions, writable: the chunk's transforms are recomputed at the end of the tick.</summary>
    public Span<Vector3> Positions { get { m_chunk.Dirty = true; return m_chunk.Position.AsSpan(0, m_chunk.Count); } }
    public Span<Quaternion> Rotations { get { m_chunk.Dirty = true; return m_chunk.Rotation.AsSpan(0, m_chunk.Count); } }
    public Span<Vector3> Scales { get { m_chunk.Dirty = true; return m_chunk.Scale.AsSpan(0, m_chunk.Count); } }
    public ReadOnlySpan<Vector3> ReadPositions => m_chunk.Position.AsSpan(0, m_chunk.Count);
    public ReadOnlySpan<Quaternion> ReadRotations => m_chunk.Rotation.AsSpan(0, m_chunk.Count);
    public ReadOnlySpan<Vector3> ReadScales => m_chunk.Scale.AsSpan(0, m_chunk.Count);

    /// <summary>The world matrix as of the last transform sync.</summary>
    public Matrix4x4 GetWorldMatrix(int row) => m_chunk.GetWorld(row);
    public Vector3 GetWorldPosition(int row)
    {
        int n = m_chunk.Capacity;
        return new Vector3(m_chunk.World[9 * n + row], m_chunk.World[10 * n + row], m_chunk.World[11 * n + row]);
    }
    public uint GetLayers(int row) => m_chunk.Layers?[row] ?? 0u;
}
