// The scheduling of batched behaviours (BatchedBehaviour.cs) and the entity store's place in the world tick: each phase
// runs the batched behaviours (parallel over chunks, one behaviour after the other) and then the class behaviours, the
// store commands queued in the phase are played back after it, and the store's transforms are synced last.
using System.Diagnostics;

namespace Phalanx.World;

public static partial class World
{
    private static readonly List<BatchedBehaviour> s_batched = new();
    private static int s_batched_registrations;
    private static readonly List<CommandBuffer> s_worker_buffers = new();
    [ThreadStatic] private static CommandBuffer? t_worker_buffer;
    private static double s_batched_time;
    private static long s_tick_count;

    /// <summary>The store of the world's batched entities (EntityStore.cs).</summary>
    public static EntityStore Store { get; } = new EntityStore();

    /// <summary>Timings of the last world tick, in milliseconds (the --entities stress mode prints them).</summary>
    public static class Stats
    {
        public static double FixedUpdateMs, UpdateMs, LateUpdateMs, ClassBehavioursMs, PlaybackMs, TransformsMs, TickMs;
    }

    /// <summary>Starts calling <paramref name="behaviour"/> from the next tick.</summary>
    public static void RegisterBatched(BatchedBehaviour behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        AssertMainThread();
        if (s_batched.Contains(behaviour))
            return;
        behaviour.m_registration = s_batched_registrations++;
        s_batched.Add(behaviour);
        s_batched.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.m_registration.CompareTo(b.m_registration));
    }

    public static bool UnregisterBatched(BatchedBehaviour behaviour)
    {
        AssertMainThread();
        return s_batched.Remove(behaviour);
    }

    /// <summary>The class entity of a store entity: the world entity for one that has an <see cref="Entity.Id"/>, else a
    /// facade made on first request whose transform, activity and Render read and write the store, and which can have
    /// class behaviours (OnPreDraw included). Null for a dead id.</summary>
    public static Entity? GetEntity(EntityId id)
    {
        AssertMainThread();
        return Store.GetFacade(id);
    }

    private static CommandBuffer GetWorkerBuffer()
    {
        if (t_worker_buffer is CommandBuffer buffer)
            return buffer;
        buffer = new CommandBuffer();
        lock (s_worker_buffers)
            s_worker_buffers.Add(buffer);
        t_worker_buffer = buffer;
        return buffer;
    }

    private static readonly List<EntityChunk> s_batched_chunks = new();

    // one phase of every batched behaviour that has it
    private static void RunBatched(BatchPhase phase, float delta_time)
    {
        if (s_batched.Count == 0 || Store.Count == 0)
            return;
        for (int b = 0; b < s_batched.Count; b++)
        {
            BatchedBehaviour behaviour = s_batched[b];
            if (!behaviour.Enabled)
                continue;
            bool has_phase = phase switch
            {
                BatchPhase.FixedUpdate => behaviour is IBatchedFixedUpdate,
                BatchPhase.Update      => behaviour is IBatchedUpdate,
                _                      => behaviour is IBatchedLateUpdate,
            };
            if (!has_phase)
                continue;
            s_batched_chunks.Clear();
            ulong mask = behaviour.Query.Mask;
            foreach (Archetype archetype in Store.m_archetype_list)
                if (archetype.Key.Kind != ArchetypeKind.Managed && (archetype.Key.Mask & mask) == mask)
                    foreach (EntityChunk chunk in archetype.Chunks)
                        if (chunk.Count > 0)
                            s_batched_chunks.Add(chunk);
            if (s_batched_chunks.Count == 0)
                continue;
            Exception? failure = null;
            ulong key_base = (ulong)b << 48;
            void RunChunk(int c)
            {
                CommandBuffer commands = GetWorkerBuffer();
                commands.BeginChunk(key_base | ((ulong)c << 24));
                var context = new BatchContext(phase, delta_time, s_batched_time, s_tick_count, commands);
                var view = new ChunkView(s_batched_chunks[c]);
                try
                {
                    switch (phase)
                    {
                        case BatchPhase.FixedUpdate: ((IBatchedFixedUpdate)behaviour).FixedUpdate(in context, in view); break;
                        case BatchPhase.Update:      ((IBatchedUpdate)behaviour).Update(in context, in view); break;
                        default:                     ((IBatchedLateUpdate)behaviour).LateUpdate(in context, in view); break;
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref failure, e, null);
                }
            }
            Store.m_jobs_running = true;
            try
            {
                if (behaviour.RunInParallel && s_batched_chunks.Count > 1)
                    Parallel.For(0, s_batched_chunks.Count, RunChunk);
                else
                    for (int c = 0; c < s_batched_chunks.Count; c++)
                        RunChunk(c);
            }
            finally
            {
                Store.m_jobs_running = false;
            }
            if (failure != null)
                Log.LogError($"{behaviour.GetType().Name}: {failure}"); // a failing behaviour doesn't stop the others
        }
        PlaybackWorkerBuffers();
    }

    private static void PlaybackWorkerBuffers()
    {
        long start = Stopwatch.GetTimestamp();
        lock (s_worker_buffers)
            CommandBuffer.Playback(Store, s_worker_buffers);
        Stats.PlaybackMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Brings the store's world matrices, bounds and instance transforms up to date (the end of every tick; the
    /// renderer calls it too in case entities moved after the tick).</summary>
    internal static void SyncStoreTransforms()
    {
        long start = Stopwatch.GetTimestamp();
        Store.SyncTransforms();
        Stats.TransformsMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    internal static void SyncStoreTransformsIfDirty()
    {
        if (Store.NeedsSync)
            Store.SyncTransforms();
    }

    // a store bound facade's entity is gone: its behaviours are disabled and destroyed
    internal static void DestroyFacadeBehaviours(Entity facade) => DestroyBehaviours(facade);
}
