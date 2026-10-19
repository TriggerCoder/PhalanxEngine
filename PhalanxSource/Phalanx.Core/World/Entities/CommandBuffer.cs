// Deferred structural changes of the entity store. Batched behaviours run on worker threads and can't change the
// store's layout under each other, so they queue spawns, destroys and changes in their thread's buffer
// (BatchContext.Commands); the world plays every buffer back on the main thread after each phase, ordered by
// behaviour, chunk and queue order, so the result doesn't depend on which thread ran which chunk.
using System.Runtime.InteropServices;

namespace Phalanx.World;

public sealed class CommandBuffer
{
    private enum Kind : byte { Spawn, Destroy, SetEnabled, SetLayers, SetComponent, SetPosition, SetRotation, SetScale }

    private struct Command
    {
        public ulong Key;
        public Kind Kind;
        public EntityId Id;
        public int Spawn;      // index into m_spawns
        public int Component;  // component id
        public int Payload, Length; // bytes in m_payload
        public uint Value;
        public System.Numerics.Vector4 Vector;
    }

    private readonly List<Command> m_commands = new();
    private readonly List<(EntityArchetype archetype, EntitySpawn spawn)> m_spawns = new();
    private byte[] m_payload = new byte[256];
    private int m_payload_length;
    private ulong m_key_base; // behaviour and chunk of the commands being queued (worker buffers)
    private uint m_sequence;

    public int Count => m_commands.Count;

    internal void BeginChunk(ulong key_base)
    {
        m_key_base = key_base;
        m_sequence = 0;
    }

    private void Add(Command command)
    {
        command.Key = m_key_base | m_sequence++;
        m_commands.Add(command);
    }

    private int Write<T>(in T value) where T : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (m_payload_length + size > m_payload.Length)
            Array.Resize(ref m_payload, System.Math.Max(m_payload.Length * 2, m_payload_length + size));
        MemoryMarshal.Write(m_payload.AsSpan(m_payload_length), in value);
        int offset = m_payload_length;
        m_payload_length += size;
        return offset;
    }

    /// <summary>Spawns an entity at playback.</summary>
    public void Spawn(EntityArchetype archetype, in EntitySpawn spawn)
    {
        m_spawns.Add((archetype, spawn));
        Add(new Command { Kind = Kind.Spawn, Spawn = m_spawns.Count - 1, Component = -1 });
    }

    /// <summary>Spawns an entity at playback with <paramref name="component"/> set.</summary>
    public void Spawn<T>(EntityArchetype archetype, in EntitySpawn spawn, in T component) where T : unmanaged, IComponentData
    {
        m_spawns.Add((archetype.With<T>(), spawn));
        Add(new Command { Kind = Kind.Spawn, Spawn = m_spawns.Count - 1, Component = ComponentId<T>.Value, Payload = Write(component), Length = System.Runtime.CompilerServices.Unsafe.SizeOf<T>() });
    }

    public void Destroy(EntityId id) => Add(new Command { Kind = Kind.Destroy, Id = id });
    public void SetEnabled(EntityId id, bool enabled) => Add(new Command { Kind = Kind.SetEnabled, Id = id, Value = enabled ? 1u : 0u });
    public void SetLayers(EntityId id, uint layers) => Add(new Command { Kind = Kind.SetLayers, Id = id, Value = layers });
    public void SetLocalPosition(EntityId id, System.Numerics.Vector3 position) => Add(new Command { Kind = Kind.SetPosition, Id = id, Vector = new(position, 0) });
    public void SetLocalRotation(EntityId id, System.Numerics.Quaternion rotation) => Add(new Command { Kind = Kind.SetRotation, Id = id, Vector = new(rotation.X, rotation.Y, rotation.Z, rotation.W) });
    public void SetLocalScale(EntityId id, System.Numerics.Vector3 scale) => Add(new Command { Kind = Kind.SetScale, Id = id, Vector = new(scale, 0) });

    /// <summary>Sets another entity's <typeparamref name="T"/> at playback (its own chunk's are written directly).</summary>
    public void Set<T>(EntityId id, in T value) where T : unmanaged, IComponentData =>
        Add(new Command { Kind = Kind.SetComponent, Id = id, Component = ComponentId<T>.Value, Payload = Write(value), Length = System.Runtime.CompilerServices.Unsafe.SizeOf<T>() });

    /// <summary>Plays the buffer back into <paramref name="store"/> in queue order and empties it (main thread). A command
    /// on an entity that is gone by then is skipped.</summary>
    public void Playback(EntityStore store)
    {
        m_commands.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        PlaybackSorted(store, m_commands, this);
        Clear();
    }

    internal void Clear()
    {
        m_commands.Clear();
        m_spawns.Clear();
        m_payload_length = 0;
        m_sequence = 0;
        m_key_base = 0;
    }

    // the commands of several buffers, merged by key (the worker buffers of a phase)
    internal static void Playback(EntityStore store, IReadOnlyList<CommandBuffer> buffers)
    {
        var merged = new List<(Command command, CommandBuffer buffer)>();
        foreach (CommandBuffer buffer in buffers)
            foreach (Command command in buffer.m_commands)
                merged.Add((command, buffer));
        if (merged.Count == 0)
            return;
        merged.Sort(static (a, b) => a.command.Key.CompareTo(b.command.Key));
        foreach ((Command command, CommandBuffer buffer) in merged)
            buffer.Execute(store, command);
        foreach (CommandBuffer buffer in buffers)
            buffer.Clear();
    }

    private static void PlaybackSorted(EntityStore store, List<Command> commands, CommandBuffer buffer)
    {
        foreach (Command command in commands)
            buffer.Execute(store, command);
    }

    private void Execute(EntityStore store, in Command command)
    {
        switch (command.Kind)
        {
            case Kind.Spawn:
            {
                (EntityArchetype archetype, EntitySpawn spawn) = m_spawns[command.Spawn];
                if (!spawn.Parent.IsNull && !store.IsAlive(spawn.Parent))
                    return;
                EntityId id = store.Spawn(archetype, spawn);
                if (command.Component >= 0)
                    store.SetRaw(id, command.Component, m_payload.AsSpan(command.Payload, command.Length));
                break;
            }
            case Kind.Destroy:
                store.Destroy(command.Id);
                break;
            case Kind.SetEnabled:
                if (store.IsAlive(command.Id)) store.SetEnabled(command.Id, command.Value != 0);
                break;
            case Kind.SetLayers:
                if (store.IsAlive(command.Id)) store.SetLayers(command.Id, command.Value);
                break;
            case Kind.SetComponent:
                store.SetRaw(command.Id, command.Component, m_payload.AsSpan(command.Payload, command.Length));
                break;
            case Kind.SetPosition:
                if (store.IsAlive(command.Id)) store.SetLocalPosition(command.Id, new(command.Vector.X, command.Vector.Y, command.Vector.Z));
                break;
            case Kind.SetRotation:
                if (store.IsAlive(command.Id)) store.SetLocalRotation(command.Id, new(command.Vector.X, command.Vector.Y, command.Vector.Z, command.Vector.W));
                break;
            case Kind.SetScale:
                if (store.IsAlive(command.Id)) store.SetLocalScale(command.Id, new(command.Vector.X, command.Vector.Y, command.Vector.Z));
                break;
        }
    }
}
