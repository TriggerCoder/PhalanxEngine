// The culling of instanced store entities for the views and shadow slices of a frame (Renderer.Instancing.cs): every
// request (a frustum, a layer mask, casters only or not) gets, per render batch, the instance slots it draws. Chunks
// are skipped whole when no row is on the request's layers, none casts a shadow (shadow requests), their merged aabb
// is outside the frustum or the light's range, or their largest row is below the request's size threshold; the rows
// of the others are tested 8 at a time (layers, then the planes, Frustum.CheckCube's test, the range sphere and the
// size) in parallel over chunks (WorkerPool), every request in the same pass over a chunk.
// Shadow requests can also leave out the casters too small to matter: smaller than MinTexels texels of their slice,
// measured by the diameter of the row's aabb sphere, against a fixed texel size (orthographic cascades) or against
// the distance to the light (perspective faces). A shadow that small is a sub-texel speckle that flickers as the
// caster moves (the rasterizer covers a texel centre or not).
// View requests also leave out the rows too small to matter on screen: spartan's contribution cull (instance_cull.hlsl:
// 106-108, sphere_contributes in common_culling.hlsl:198, CULL_CONTRIBUTION_MESH_PX = 2), a row whose bounding sphere
// (column 6 of the bounds: the mesh aabb's |extent| times the largest world scale, spartan's lod_aabb_diag * 0.5 *
// max_world_scale, so a spinning cube keeps its size) projects to less than MinPixels on both axes of the view's
// render resolution (Phalanx.Math.ScreenContribution), tested 8 at a time after the planes. A row it leaves out isn't
// in the lists nor in CallbackRows (no OnPreDraw: not drawn, like a frustum culled one); it stays enabled and keeps
// updating and its instance keeps being uploaded, so the frame it comes back its previous transform is last frame's.
// Spartan @ 4c07baa culls each Render once against the camera (Render::UpdateFrustumAndDistanceCulling,
// Render.cpp:417-475), its instanced renderables on the gpu (the instance cull, Renderer.cpp:3814-4051) and its shadow
// casters on the gpu per slice (shadow_cull.hlsl:56-90: the 4 side planes of a cascade, 6 planes of a face, a
// per caster max shadow distance from the camera). Deviations:
// - The cpu culls the casters per slice (no gpu culling pass), with the far plane of a cascade kept (the near one is
//   dropped: the volume is extruded toward the light, casters between the light and the cascade still shadow it;
//   spartan drops both), a range sphere for point and spot lights, and the sub-texel threshold, which spartan doesn't
//   have (Renderer.ShadowCasterMinTexels, 0 turns it off).
// - The contribution cull runs on the cpu per view (spartan's on the gpu in the instance cull), with the unjittered
//   view projection where spartan's uses buffer_frame.view_projection, the jittered one: the taau jitter (up to half a
//   pixel) would move a sphere's measure from frame to frame and make the ones near the threshold flicker.
// - Each row's threshold is MinPixels times ScreenContribution.DitherScale of its instance slot, a fixed hash in
//   [0.5, 1): spartan's hard cut at 2 pixels becomes a band from 1 to 2 pixels over which a dense field thins out
//   gradually instead of ending at a sharp ring, and nothing spartan keeps is left out. No hysteresis (it would need
//   per view per entity state): with the unjittered matrix and the rotation invariant sphere an entity's measure only
//   changes as it or the camera moves, so it crosses its threshold once, not every frame.
// - Chunks aren't skipped whole by it (a chunk's merged aabb has no single projected size): every row is tested.
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
    public int PlaneMask = 0x3F; // the planes tested (bit i plane i: near, far, left, right, top, bottom)
    // shadow slices: the light's range (a sphere), and the smallest caster kept, by the diameter of its aabb sphere:
    // at least MinDiameter (orthographic), at least MinDiameterPerDistance * its distance to SizeEye (perspective)
    public bool HasSphere;
    public Vector3 SphereCenter;
    public float SphereRadius;
    public float MinDiameter;
    public float MinDiameterPerDistance;
    public Vector3 SizeEye;
    // views: spartan's contribution cull (Phalanx.Math.ScreenContribution), rows whose bounding sphere projects to
    // less than MinPixels (times the row's dither scale) on both axes of ContributionResolution are left out; 0 is off
    public float MinPixels;
    public Matrix4x4 ContributionViewProjection;
    public Vector2 ContributionResolution;
    internal int ChunksSkippedSmall; // chunks skipped whole by the size threshold (stats and tests)
    internal int RowsSkippedSmall;   // rows left out by the size thresholds (those of the skipped chunks included)
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
        PlaneMask = ignore_depth ? 0x3C : 0x3F;
        for (int i = 0; i < 6; i++)
            Planes[i] = frustum.GetPlane(i);
        HasSphere = false;
        MinDiameter = MinDiameterPerDistance = MinPixels = 0.0f;
    }

    /// <summary>The views' contribution cull: the unjittered view projection, the render resolution, the threshold in
    /// pixels (0 turns it off).</summary>
    public void SetContribution(in Matrix4x4 view_projection, Vector2 resolution, float min_pixels)
    {
        ContributionViewProjection = view_projection;
        ContributionResolution     = resolution;
        MinPixels                  = resolution.X > 0.0f && resolution.Y > 0.0f ? min_pixels : 0.0f;
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
            request.ChunksSkippedSmall = 0;
            request.RowsSkippedSmall = 0;
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
        {
            m_cull_job.chunks   = chunks;
            m_cull_job.requests = requests;
            WorkerPool.ParallelLoop(m_cull_job, chunks.Count);
            m_cull_job.chunks   = null!;
            m_cull_job.requests = null!;
        }
        foreach (InstanceCullRequest request in requests)
            for (int c = 0; c < chunks.Count; c++)
                if (request.ChunkCallbacks[c] is { Count: > 0 } list)
                    request.CallbackRows.AddRange(list);
    }

    private sealed class CullJob : ParallelJob
    {
        public List<EntityChunk> chunks = null!;
        public IReadOnlyList<InstanceCullRequest> requests = null!;
        public override void Execute(int c) => CullChunk(chunks[c], c, requests);
    }
    private readonly CullJob m_cull_job = new();

    /// <summary>FinishCull of every request, the requests in parallel (they share nothing but the chunks they read).</summary>
    internal void FinishCulls(IReadOnlyList<InstanceCullRequest> requests)
    {
        if (requests.Count < 2)
        {
            foreach (InstanceCullRequest request in requests)
                FinishCull(request);
            return;
        }
        GetCullChunks(); // built here, on the calling thread, before the workers read it
        m_finish_job.Store    = this;
        m_finish_job.Requests = requests;
        WorkerPool.ParallelLoop(m_finish_job, requests.Count);
        m_finish_job.Requests = null;
    }

    private sealed class FinishJob : ParallelJob
    {
        public EntityStore? Store;
        public IReadOnlyList<InstanceCullRequest>? Requests;
        public override void Execute(int index) => Store!.FinishCull(Requests![index]);
    }
    private readonly FinishJob m_finish_job = new();

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
            Vector3 center = (chunk.BoundsMin + chunk.BoundsMax) * 0.5f, extent = (chunk.BoundsMax - chunk.BoundsMin) * 0.5f;
            if (request.HasFrustum && chunk.BoundsValid && !BoxVisible(request, center, extent))
                continue;
            if (chunk.BoundsValid && request.HasSphere && BoxDistanceSquared(center, extent, request.SphereCenter) > request.SphereRadius * request.SphereRadius)
                continue;
            if (chunk.BoundsValid && TooSmall(request, 2.0f * MathF.Sqrt(chunk.MaxRadiusSquared), BoxDistanceSquared(center, extent, request.SizeEye)))
            {
                Interlocked.Increment(ref request.ChunksSkippedSmall);
                Interlocked.Add(ref request.RowsSkippedSmall, chunk.CasterCount);
                continue;
            }
            Interlocked.Increment(ref request.ChunksTested);
            uint[]? output = request.ChunkSlots[c];
            if (output == null || output.Length < chunk.Count)
                request.ChunkSlots[c] = output = new uint[chunk.Capacity];
            request.ChunkCounts[c] = CullRows(chunk, request, output, c);
        }
    }

    // whether a caster of this diameter, this squared distance from the eye at the closest, is below the threshold
    private static bool TooSmall(InstanceCullRequest request, float diameter, float distance_squared)
    {
        if (request.MinDiameter > 0.0f && diameter < request.MinDiameter)
            return true;
        if (request.MinDiameterPerDistance > 0.0f)
        {
            float k = request.MinDiameterPerDistance;
            return diameter * diameter < k * k * distance_squared;
        }
        return false;
    }

    // the contribution cull of one row, the vector loop's formula
    internal static bool RowContributes(InstanceCullRequest request, Vector3 center, float radius, uint slot)
    {
        float pixels = Phalanx.Math.ScreenContribution.SpherePixels(request.ContributionViewProjection, center, radius, request.ContributionResolution);
        return pixels >= request.MinPixels * Phalanx.Math.ScreenContribution.DitherScale(slot);
    }

    private static float BoxDistanceSquared(Vector3 center, Vector3 extent, Vector3 point)
    {
        Vector3 d = Vector3.Max(Vector3.Abs(point - center) - extent, Vector3.Zero);
        return Vector3.Dot(d, d);
    }

    private static bool BoxVisible(InstanceCullRequest request, Vector3 center, Vector3 extent)
    {
        for (int p = 0; p < 6; p++)
        {
            if ((request.PlaneMask & (1 << p)) == 0)
                continue;
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
        bool sphere = request.HasSphere;
        bool small_ortho = request.MinDiameter > 0.0f, small_perspective = request.MinDiameterPerDistance > 0.0f;
        bool contribution = request.MinPixels > 0.0f;
        uint[] row_slots = chunk.Slot!;
        int skipped_small = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref uint layer_ref = ref MemoryMarshal.GetArrayDataReference(layers);
            ref float bounds = ref MemoryMarshal.GetArrayDataReference(b);
            Vector256<uint> mask = Vector256.Create(request.Mask);
            Span<Vector256<float>> plane_n = stackalloc Vector256<float>[24]; // nx ny nz d per tested plane
            int planes = 0;
            for (int p = 0; p < 6; p++)
            {
                if ((request.PlaneMask & (1 << p)) == 0)
                    continue;
                Plane plane = request.Planes[p];
                plane_n[4 * planes]     = Vector256.Create(plane.Normal.X);
                plane_n[4 * planes + 1] = Vector256.Create(plane.Normal.Y);
                plane_n[4 * planes + 2] = Vector256.Create(plane.Normal.Z);
                plane_n[4 * planes + 3] = Vector256.Create(plane.D);
                planes++;
            }
            Vector256<float> sphere_x = Vector256.Create(request.SphereCenter.X), sphere_y = Vector256.Create(request.SphereCenter.Y), sphere_z = Vector256.Create(request.SphereCenter.Z);
            Vector256<float> sphere_r2 = Vector256.Create(request.SphereRadius * request.SphereRadius);
            Vector256<float> eye_x = Vector256.Create(request.SizeEye.X), eye_y = Vector256.Create(request.SizeEye.Y), eye_z = Vector256.Create(request.SizeEye.Z);
            // diameter = 2 |extent|: diameter^2 = 4 |extent|^2
            Vector256<float> min_d2 = Vector256.Create(request.MinDiameter * request.MinDiameter * 0.25f);
            Vector256<float> k2 = Vector256.Create(request.MinDiameterPerDistance * request.MinDiameterPerDistance * 0.25f);
            // the contribution cull: the clip axes x, y, w of the view projection, their lengths, half the resolution
            ref readonly Matrix4x4 vp = ref request.ContributionViewProjection;
            Vector256<float> vx_x = Vector256.Create(vp.M11), vx_y = Vector256.Create(vp.M21), vx_z = Vector256.Create(vp.M31), vx_t = Vector256.Create(vp.M41);
            Vector256<float> vy_x = Vector256.Create(vp.M12), vy_y = Vector256.Create(vp.M22), vy_z = Vector256.Create(vp.M32), vy_t = Vector256.Create(vp.M42);
            Vector256<float> vw_x = Vector256.Create(vp.M14), vw_y = Vector256.Create(vp.M24), vw_z = Vector256.Create(vp.M34), vw_t = Vector256.Create(vp.M44);
            Vector256<float> len_x = Vector256.Create(new Vector3(vp.M11, vp.M21, vp.M31).Length());
            Vector256<float> len_y = Vector256.Create(new Vector3(vp.M12, vp.M22, vp.M32).Length());
            Vector256<float> len_w = Vector256.Create(new Vector3(vp.M14, vp.M24, vp.M34).Length());
            Vector256<float> half_w = Vector256.Create(request.ContributionResolution.X * 0.5f), half_h = Vector256.Create(request.ContributionResolution.Y * 0.5f);
            Vector256<float> min_px = Vector256.Create(request.MinPixels), one = Vector256.Create(1.0f);
            Vector256<float> dither = Vector256.Create(Phalanx.Math.ScreenContribution.DitherRange * (1.0f / 16777216.0f));
            Vector256<uint> golden = Vector256.Create(0x9E3779B9u);
            ref uint slot_ref = ref MemoryMarshal.GetArrayDataReference(row_slots);
            nuint cs = (nuint)n;
            for (; i + 8 <= count; i += 8)
            {
                nuint o = (nuint)i;
                uint bits = ~Vector256.Equals(Vector256.LoadUnsafe(ref layer_ref, o) & mask, Vector256<uint>.Zero).ExtractMostSignificantBits() & 0xFFu;
                if (bits == 0)
                    continue;
                if (request.HasFrustum || sphere || small_ortho || small_perspective || contribution)
                {
                    Vector256<float> cx = Vector256.LoadUnsafe(ref bounds, o), cy = Vector256.LoadUnsafe(ref bounds, o + cs), cz = Vector256.LoadUnsafe(ref bounds, o + 2 * cs);
                    Vector256<float> ex = Vector256.LoadUnsafe(ref bounds, o + 3 * cs), ey = Vector256.LoadUnsafe(ref bounds, o + 4 * cs), ez = Vector256.LoadUnsafe(ref bounds, o + 5 * cs);
                    Vector256<float> outside = Vector256<float>.Zero;
                    if (request.HasFrustum)
                    {
                        for (int p = 0; p < planes; p++)
                        {
                            Vector256<float> nx = plane_n[4 * p], ny = plane_n[4 * p + 1], nz = plane_n[4 * p + 2];
                            Vector256<float> d = nx * cx + ny * cy + nz * cz + plane_n[4 * p + 3];
                            Vector256<float> r = Vector256.Abs(nx) * ex + Vector256.Abs(ny) * ey + Vector256.Abs(nz) * ez;
                            outside |= Vector256.LessThan(d + r, Vector256<float>.Zero);
                        }
                    }
                    if (sphere)
                    {
                        Vector256<float> dx = Vector256.Max(Vector256.Abs(sphere_x - cx) - ex, Vector256<float>.Zero);
                        Vector256<float> dy = Vector256.Max(Vector256.Abs(sphere_y - cy) - ey, Vector256<float>.Zero);
                        Vector256<float> dz = Vector256.Max(Vector256.Abs(sphere_z - cz) - ez, Vector256<float>.Zero);
                        outside |= Vector256.GreaterThan(dx * dx + dy * dy + dz * dz, sphere_r2);
                    }
                    uint culled = outside.ExtractMostSignificantBits();
                    if (small_ortho || small_perspective || contribution)
                    {
                        Vector256<float> radius2 = ex * ex + ey * ey + ez * ez;
                        Vector256<float> small = Vector256<float>.Zero;
                        if (contribution)
                        {
                            // ScreenContribution.ProjectSphereNdc, 8 at a time: behind the camera is out, across the
                            // near plane is in, otherwise the larger side of the ndc rectangle in pixels
                            Vector256<float> r = Vector256.LoadUnsafe(ref bounds, o + 6 * cs);
                            Vector256<float> clip_x = vx_x * cx + vx_y * cy + vx_z * cz + vx_t;
                            Vector256<float> clip_y = vy_x * cx + vy_y * cy + vy_z * cz + vy_t;
                            Vector256<float> clip_w = vw_x * cx + vw_y * cy + vw_z * cz + vw_t;
                            Vector256<float> rx = r * len_x, ry = r * len_y, rw = r * len_w;
                            Vector256<float> w_close = clip_w - rw, w_far = clip_w + rw;
                            Vector256<float> behind = Vector256.LessThanOrEqual(w_far, Vector256<float>.Zero);
                            Vector256<float> valid = Vector256.GreaterThan(w_close, Vector256<float>.Zero);
                            Vector256<float> inv_close = one / w_close, inv_far = one / w_far;
                            Vector256<float> xl = clip_x - rx, xh = clip_x + rx, yl = clip_y - ry, yh = clip_y + ry;
                            Vector256<float> xlc = xl * inv_close, xlf = xl * inv_far, xhc = xh * inv_close, xhf = xh * inv_far;
                            Vector256<float> ylc = yl * inv_close, ylf = yl * inv_far, yhc = yh * inv_close, yhf = yh * inv_far;
                            Vector256<float> size_x = (Vector256.Max(Vector256.Max(xlc, xlf), Vector256.Max(xhc, xhf)) - Vector256.Min(Vector256.Min(xlc, xlf), Vector256.Min(xhc, xhf))) * half_w;
                            Vector256<float> size_y = (Vector256.Max(Vector256.Max(ylc, ylf), Vector256.Max(yhc, yhf)) - Vector256.Min(Vector256.Min(ylc, ylf), Vector256.Min(yhc, yhf))) * half_h;
                            Vector256<uint> hash = Vector256.ShiftRightLogical(Vector256.LoadUnsafe(ref slot_ref, o) * golden, 8);
                            Vector256<float> threshold = min_px * (one - Vector256.ConvertToSingle(hash.AsInt32()) * dither);
                            small |= behind | (valid & Vector256.LessThan(Vector256.Max(size_x, size_y), threshold));
                        }
                        if (small_ortho)
                            small |= Vector256.LessThan(radius2, min_d2);
                        if (small_perspective)
                        {
                            Vector256<float> dx = Vector256.Max(Vector256.Abs(eye_x - cx) - ex, Vector256<float>.Zero);
                            Vector256<float> dy = Vector256.Max(Vector256.Abs(eye_y - cy) - ey, Vector256<float>.Zero);
                            Vector256<float> dz = Vector256.Max(Vector256.Abs(eye_z - cz) - ez, Vector256<float>.Zero);
                            small |= Vector256.LessThan(radius2, k2 * (dx * dx + dy * dy + dz * dz));
                        }
                        uint small_bits = small.ExtractMostSignificantBits() & ~culled & bits;
                        skipped_small += System.Numerics.BitOperations.PopCount(small_bits);
                        culled |= small_bits;
                    }
                    bits &= ~culled;
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
            var center = new Vector3(b[i], b[n + i], b[2 * n + i]);
            var extent = new Vector3(b[3 * n + i], b[4 * n + i], b[5 * n + i]);
            if (request.HasFrustum && !BoxVisible(request, center, extent))
                continue;
            if (sphere && BoxDistanceSquared(center, extent, request.SphereCenter) > request.SphereRadius * request.SphereRadius)
                continue;
            if (((small_ortho || small_perspective) && TooSmall(request, 2.0f * extent.Length(), BoxDistanceSquared(center, extent, request.SizeEye)))
                || (contribution && !RowContributes(request, center, b[6 * n + i], row_slots[i])))
            {
                skipped_small++;
                continue;
            }
            Emit(chunk, request, i, output, ref visible, c);
        }
        if (skipped_small > 0)
            Interlocked.Add(ref request.RowsSkippedSmall, skipped_small);
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
