// The culling of instanced store entities for the views and shadow slices of a frame (Renderer.Instancing.cs): every
// request (a frustum, a layer mask, casters only or not) gets, per render batch, the instance slots it draws. Chunks
// are skipped whole when no row is on the request's layers, none casts a shadow (shadow requests) or their merged aabb
// is outside the frustum; the rows of the others are tested 8 at a time (layers, then the 6 planes, Frustum.CheckCube's
// test) in parallel over chunks, every request in the same pass over a chunk.
// Spartan @ 4c07baa culls each Render once against the camera (Render::UpdateFrustumAndDistanceCulling,
// Render.cpp:417-475) and its instanced renderables on the gpu (the instance cull, Renderer.cpp:3814-4051).
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Phalanx.World;

/// <summary>One view's or shadow slice's culling of the store's instanced entities, and its result.</summary>
internal sealed class InstanceCullRequest
{
    public struct CallbackRow
    {
        public EntityId Id;
        public uint Slot;
        public uint Batch;
    }

    // the request
    public bool HasFrustum;
    public readonly Plane[] Planes = new Plane[6];
    public bool IgnoreDepth;     // orthographic shadow slices: no near and far planes
    public uint Mask = uint.MaxValue;
    public bool ShadowCasters;   // only rows that cast shadows
    public bool Callbacks;       // rows with facade callbacks go to CallbackRows instead of the lists

    // the result: per batch id a range of Slots, and the batches that have one
    public int[] BatchStart = Array.Empty<int>();
    public int[] BatchCount = Array.Empty<int>();
    public uint[] Slots = Array.Empty<uint>();
    public int Total;
    public readonly List<uint> Batches = new();
    public readonly List<CallbackRow> CallbackRows = new();
    private readonly List<(uint batch, uint slot)> m_extras = new();

    // per chunk of the pass
    internal uint[]?[] ChunkSlots = Array.Empty<uint[]?>();
    internal int[] ChunkCounts = Array.Empty<int>();
    internal List<CallbackRow>?[] ChunkCallbacks = Array.Empty<List<CallbackRow>?>();
    internal int ChunksTested; // chunks whose rows were tested (not skipped whole), for the tests and the stats

    public void SetFrustum(Phalanx.Math.Frustum frustum, bool ignore_depth = false)
    {
        HasFrustum = true;
        IgnoreDepth = ignore_depth;
        for (int i = 0; i < 6; i++)
            Planes[i] = frustum.GetPlane(i);
    }

    /// <summary>A callback row that the view draws after all, in its batch's list (after the culled ones).</summary>
    public void AddExtra(uint batch, uint slot) => m_extras.Add((batch, slot));
    internal List<(uint batch, uint slot)> Extras => m_extras;

    public ReadOnlySpan<uint> GetSlots(uint batch) => batch < BatchCount.Length ? Slots.AsSpan(BatchStart[batch], BatchCount[batch]) : default;
}

public sealed partial class EntityStore
{
    private readonly List<EntityChunk> m_cull_chunks = new();
    private int m_cull_layout_version = -1;

    // every renderable chunk with a batch, in batch, archetype and chunk order (the order of the lists)
    private List<EntityChunk> GetCullChunks()
    {
        if (m_cull_layout_version == m_layout_version)
            return m_cull_chunks;
        m_cull_chunks.Clear();
        var archetypes = m_archetype_list.Where(a => a.IsRenderable && a.Key.Batch != 0).OrderBy(a => a.Key.Batch).ThenBy(a => a.Index);
        foreach (Archetype archetype in archetypes)
            m_cull_chunks.AddRange(archetype.Chunks);
        m_cull_layout_version = m_layout_version;
        return m_cull_chunks;
    }

    internal int RenderableChunkCount => GetCullChunks().Count;

    /// <summary>Culls every request against the store; then each request's callback rows are decided (AddExtra) and
    /// FinishCull builds its lists.</summary>
    internal void CullInstances(IReadOnlyList<InstanceCullRequest> requests)
    {
        List<EntityChunk> chunks = GetCullChunks();
        foreach (InstanceCullRequest request in requests)
        {
            if (request.ChunkCounts.Length < chunks.Count)
            {
                int size = System.Math.Max(chunks.Count, request.ChunkCounts.Length * 2);
                Array.Resize(ref request.ChunkSlots, size);
                Array.Resize(ref request.ChunkCounts, size);
                Array.Resize(ref request.ChunkCallbacks, size);
            }
            request.CallbackRows.Clear();
            request.Extras.Clear();
            request.ChunksTested = 0;
        }
        int rows = 0;
        foreach (EntityChunk chunk in chunks)
            rows += chunk.Count;
        if (rows < 16384 || chunks.Count == 1)
        {
            for (int c = 0; c < chunks.Count; c++)
                CullChunk(chunks[c], c, requests);
        }
        else
            Parallel.For(0, chunks.Count, c => CullChunk(chunks[c], c, requests));
        foreach (InstanceCullRequest request in requests)
            for (int c = 0; c < chunks.Count; c++)
                if (request.ChunkCallbacks[c] is { Count: > 0 } list)
                    request.CallbackRows.AddRange(list);
    }

    internal void FinishCull(InstanceCullRequest request)
    {
        List<EntityChunk> chunks = GetCullChunks();
        int batch_count = RenderBatches.Count;
        if (request.BatchCount.Length < batch_count)
        {
            request.BatchCount = new int[batch_count];
            request.BatchStart = new int[batch_count];
        }
        else
        {
            Array.Clear(request.BatchCount);
            Array.Clear(request.BatchStart);
        }
        int[] counts = request.BatchCount;
        for (int c = 0; c < chunks.Count; c++)
            counts[chunks[c].Archetype.Key.Batch] += request.ChunkCounts[c];
        foreach ((uint batch, uint _) in request.Extras)
            counts[batch]++;
        request.Batches.Clear();
        int total = 0;
        for (int b = 1; b < batch_count; b++)
        {
            if (counts[b] == 0)
                continue;
            request.Batches.Add((uint)b);
            request.BatchStart[b] = total;
            total += counts[b];
        }
        request.Total = total;
        if (request.Slots.Length < total)
            request.Slots = new uint[System.Math.Max(total, request.Slots.Length * 2)];
        Array.Clear(counts); // reused as the cursors, then restored
        for (int c = 0; c < chunks.Count; c++)
        {
            int count = request.ChunkCounts[c];
            if (count == 0)
                continue;
            uint batch = chunks[c].Archetype.Key.Batch;
            Array.Copy(request.ChunkSlots[c]!, 0, request.Slots, request.BatchStart[batch] + counts[batch], count);
            counts[batch] += count;
        }
        foreach ((uint batch, uint slot) in request.Extras)
            request.Slots[request.BatchStart[batch] + counts[batch]++] = slot;
    }

    private static void CullChunk(EntityChunk chunk, int c, IReadOnlyList<InstanceCullRequest> requests)
    {
        foreach (InstanceCullRequest request in requests)
        {
            request.ChunkCounts[c] = 0;
            request.ChunkCallbacks[c]?.Clear();
            if (chunk.Count == 0 || (chunk.LayersOr & request.Mask) == 0 || (request.ShadowCasters && chunk.CasterCount == 0))
                continue;
            if (request.HasFrustum && chunk.BoundsValid && !BoxVisible(request, (chunk.BoundsMin + chunk.BoundsMax) * 0.5f, (chunk.BoundsMax - chunk.BoundsMin) * 0.5f))
                continue;
            Interlocked.Increment(ref request.ChunksTested);
            uint[]? output = request.ChunkSlots[c];
            if (output == null || output.Length < chunk.Count)
                request.ChunkSlots[c] = output = new uint[chunk.Capacity];
            request.ChunkCounts[c] = CullRows(chunk, request, output, c);
        }
    }

    private static bool BoxVisible(InstanceCullRequest request, Vector3 center, Vector3 extent)
    {
        for (int p = request.IgnoreDepth ? 2 : 0; p < 6; p++)
        {
            Plane plane = request.Planes[p];
            float d = Vector3.Dot(plane.Normal, center) + plane.D;
            float r = Vector3.Dot(Vector3.Abs(plane.Normal), extent);
            if (d + r < 0.0f)
                return false;
        }
        return true;
    }

    private static int CullRows(EntityChunk chunk, InstanceCullRequest request, uint[] output, int c)
    {
        int count = chunk.Count, n = chunk.Capacity, visible = 0;
        uint[] layers = chunk.CullLayers!;
        float[] b = chunk.Bounds!;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref uint layer_ref = ref MemoryMarshal.GetArrayDataReference(layers);
            ref float bounds = ref MemoryMarshal.GetArrayDataReference(b);
            Vector256<uint> mask = Vector256.Create(request.Mask);
            int first_plane = request.IgnoreDepth ? 2 : 0;
            Span<Vector256<float>> plane_n = stackalloc Vector256<float>[24]; // nx ny nz d per plane
            for (int p = 0; p < 6; p++)
            {
                Plane plane = request.Planes[p];
                plane_n[4 * p]     = Vector256.Create(plane.Normal.X);
                plane_n[4 * p + 1] = Vector256.Create(plane.Normal.Y);
                plane_n[4 * p + 2] = Vector256.Create(plane.Normal.Z);
                plane_n[4 * p + 3] = Vector256.Create(plane.D);
            }
            nuint cs = (nuint)n;
            for (; i + 8 <= count; i += 8)
            {
                nuint o = (nuint)i;
                uint bits = ~Vector256.Equals(Vector256.LoadUnsafe(ref layer_ref, o) & mask, Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFFu;
                if (bits == 0)
                    continue;
                if (request.HasFrustum)
                {
                    Vector256<float> cx = Vector256.LoadUnsafe(ref bounds, o), cy = Vector256.LoadUnsafe(ref bounds, o + cs), cz = Vector256.LoadUnsafe(ref bounds, o + 2 * cs);
                    Vector256<float> ex = Vector256.LoadUnsafe(ref bounds, o + 3 * cs), ey = Vector256.LoadUnsafe(ref bounds, o + 4 * cs), ez = Vector256.LoadUnsafe(ref bounds, o + 5 * cs);
                    Vector256<float> outside = Vector256<float>.Zero;
                    for (int p = first_plane; p < 6; p++)
                    {
                        Vector256<float> nx = plane_n[4 * p], ny = plane_n[4 * p + 1], nz = plane_n[4 * p + 2];
                        Vector256<float> d = nx * cx + ny * cy + nz * cz + plane_n[4 * p + 3];
                        Vector256<float> r = Vector256.Abs(nx) * ex + Vector256.Abs(ny) * ey + Vector256.Abs(nz) * ez;
                        outside |= Vector256.LessThan(d + r, Vector256<float>.Zero);
                    }
                    bits &= ~outside.ExtractMostSignificantBits();
                }
                while (bits != 0)
                {
                    int row = i + System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    Emit(chunk, request, row, output, ref visible, c);
                }
            }
        }
        for (; i < count; i++)
        {
            if ((layers[i] & request.Mask) == 0)
                continue;
            if (request.HasFrustum)
            {
                var center = new Vector3(b[i], b[n + i], b[2 * n + i]);
                var extent = new Vector3(b[3 * n + i], b[4 * n + i], b[5 * n + i]);
                if (!BoxVisible(request, center, extent))
                    continue;
            }
            Emit(chunk, request, i, output, ref visible, c);
        }
        return visible;
    }

    private static void Emit(EntityChunk chunk, InstanceCullRequest request, int row, uint[] output, ref int visible, int c)
    {
        RowFlags flags = chunk.Flags[row];
        if (request.ShadowCasters && (flags & RowFlags.CastsShadows) == 0)
            return;
        if (request.Callbacks && (flags & RowFlags.Callbacks) != 0)
        {
            (request.ChunkCallbacks[c] ??= new List<InstanceCullRequest.CallbackRow>()).Add(new InstanceCullRequest.CallbackRow { Id = chunk.Ids[row], Slot = chunk.Slot![row], Batch = chunk.Archetype.Key.Batch });
            return;
        }
        output[visible++] = chunk.Slot![row];
    }
}
