// Instanced drawing (S1, PhalanxEngine-design-2m-entities.md §9): every draw of a view or shadow slice is one
// DrawIndexed per mesh and material, whose instances come from a per frame list of 4 byte slots.
// - World entities (the exact path) keep one DrawData entry each with their exact matrices; a view's draws of one mesh
//   and material are grouped and the vertex shader reads draw_data[instance_slots[gl_InstanceIndex]] (mode 1). A group of
//   one stays a plain draw (mode 0, spartan's cpu path), which keeps the frames of the existing scenes bit identical.
// - Store entities (the packed path) are drawn per RenderBatch from their 32 byte PackedTransform (spartan's
//   PackedInstance), which the transform pass packs and this file copies into a persistent instance buffer per frame
//   slot; the vertex shader rebuilds the matrix from instances_current[slot] and the previous frame's from
//   instances_previous[slot] (TAAU velocity) (mode 2). The batch's DrawData entry carries its material and uv transform.
// - An entity whose OnPreDraw hides it is left out of that view's list; one that sets a material or uv transform gets
//   its own exact DrawData entry and DrawCall for that view, drawn in an override batch, and stays out of the packed one.
// - Each view also leaves out the store entities whose bounding sphere is smaller than MinPixelSize pixels of its
//   render resolution (not its output one), measured with its unjittered view projection: spartan's contribution cull
//   (EntityStore.Culling.cs, Phalanx.Math.ScreenContribution, with the deviations listed there). Such an entity isn't
//   drawn in that view, so it gets no OnPreDraw there (the existing rule: only the entities a view draws are called
//   back), it stays enabled and its instance is uploaded as before (the velocity of the frame it comes back is right),
//   and the shadow slices don't use it (they keep ShadowCasterMinTexels). InstancingStats.Views counts it per view.
// - Shadow slices cull the store like views (every layer, casters only), each with its own caster volume
//   (SetShadowCasterLimits): a cascade tests its side planes and its far plane (the near one, toward the light, is
//   dropped: casters between the light and the slice still shadow it), a point or spot face its frustum and the
//   light's range; and in every slice a store caster smaller than ShadowCasterMinTexels texels (the diameter of its aabb
//   sphere against the cascade's texel, or the face's texel at its distance) is left out. World entities (the exact
//   path) keep spartan's test, so the existing scenes render the same.
// Deviations from spartan @ 4c07baa, which instances inside one Render (Render::SetInstances, a static instance list,
// Render.cpp:821) and culls the instances on the gpu (Renderer.cpp:3814-4051, common_vertex_processing.hlsl:58-86):
// - Batches are separate entities that share mesh and material; their instances move every frame, are culled on the
//   cpu per view and slice, and the lists are 4 byte slots into a per entity instance buffer, not instance ranges.
// - The instance buffers and the slot lists are bound in set 0 (bindings 340-342): llvmpipe binds only 8 descriptor
//   sets, so spartan's bindless geometry_instances (set 10) isn't usable by a pipeline there.
// - The world tick packs the instances before the frame slot is free, so they are copied into the frame slot's host
//   buffer instead of being written there directly: only the slot groups (EntityStore.SlotGroupSize slots, 8 KB) that
//   the transform pass stamped since this frame's device local buffer was last written, two frames ago (so the
//   groups of the last two syncs: an entity that stopped gets previous = current once), coalesced into regions and
//   copied over the worker pool (spartan's instances are static per Render, Render::SetInstances, Render.cpp:821).
// - The velocity of an OnPreDraw override's draw uses the history of overrides (s_override_previous: last frame's
//   transform of each overridden slot) instead of a host copy of every instance; a slot overridden for the first time
//   has no previous transform that frame (no velocity), like a new instance.
// - The vertex shaders read device local copies (spartan's geometry_buffer_instances is device local too,
//   GeometryBuffer.cpp:593-600): the frame's command list copies the changed blocks and the slot list from the frame
//   slot's host buffers into them before the passes. The first S1 port bound the host buffers themselves, so every
//   geometry pass (prepass, g-buffer current and previous, each shadow slice) read the instances over PCIe from
//   system memory, tens of megabytes a pass at a million entities. Only two device local instance buffers are kept
//   (this frame and the previous one) and one slot list: the copies are ordered after the earlier frames' vertex
//   shaders by a barrier, so they need no frame slot of their own.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using static Phalanx.Rendering.Renderer_Definitions;

namespace Phalanx.Rendering;

public static partial class Renderer
{
    internal const uint instancing_mode_single = 0, instancing_mode_exact = 1, instancing_mode_packed = 2;
    private const uint binding_instance_slots = 40, binding_instances_current = 41, binding_instances_previous = 42; // t40-t42, set 0

    // one instanced draw: index range, DrawData entry (mode 0 and 2), material, and its instances
    internal struct InstancedDraw
    {
        public uint mode;
        public uint draw_index, material_index;
        public uint index_count, index_offset, vertex_offset;
        public uint first_instance, instance_count;
        public bool has_albedo; // shadow maps: alpha tested with the albedo
    }

    // a per view OnPreDraw override of a store entity: drawn with its own DrawData entry (exact path)
    internal struct PackedOverride
    {
        public uint slot;
        public int override_index;
        public Render render;
        public Entity entity;
    }

    /// <summary>The per frame numbers of the instancing (the --entities stress mode prints them).</summary>
    public static class InstancingStats
    {
        public static double CullMs, ShadowCullMs, ListsMs, UploadMs;
        public static int Batches, Draws, VisibleInstances, ShadowInstances;
        public static long UploadedBytes;
        public static int UploadRegions;
        public static int ShadowSkippedSmall; // store casters the sub-texel threshold left out, summed over the slices
        /// <summary>Per view of the frame, in draw order: the store instances it draws (VisibleInstances is their sum)
        /// and those the contribution cull (MinPixelSize) left out of it.</summary>
        public static readonly List<ViewInstanceStats> Views = new();
        /// <summary>Per shadow slice of the frame, in slice order: its light, its slice (cascade or face) index, the
        /// store casters it draws and those the sub-texel threshold left out of it. ShadowInstances and
        /// ShadowSkippedSmall are their sums, so an entity is counted once per slice it is drawn in or left out of.</summary>
        public static readonly List<ShadowSliceStats> ShadowSlices = new();
    }

    public readonly record struct ViewInstanceStats(int Drawn, int SkippedSubpixel);

    public readonly record struct ShadowSliceStats(LightType Light, uint Slice, int Drawn, int SkippedSubTexel);

    // host visible, one per frame slot: the cpu writes them once the slot's frame finished, the gpu only copies them
    private static readonly RHI_Buffer?[] s_slot_buffers     = new RHI_Buffer?[renderer_draw_data_buffer_count];
    private static readonly RHI_Buffer?[] s_instance_buffers = new RHI_Buffer?[renderer_draw_data_buffer_count];
    // device local, what the vertex shaders read: the slot list, this frame's and the previous frame's instances
    private static RHI_Buffer? s_slot_buffer_gpu;
    private static readonly RHI_Buffer?[] s_instance_buffers_gpu = new RHI_Buffer?[2];
    private static readonly uint[] s_instance_gpu_version = new uint[2];
    private static bool s_instances_gpu_reset;
    private static readonly List<uint> s_fresh_pending = new();      // new slots the next upload gives the previous gpu buffer
    private static readonly HashSet<uint> s_fresh_this_frame = new(); // new slots this frame: no previous transform
    private static readonly List<RHI_BufferCopy> s_copy_regions = new();
    private static readonly RHI_BufferCopy[] s_slot_copy = new RHI_BufferCopy[1];
    private static uint s_slot_capacity, s_instance_capacity;
    private static readonly List<RHI_BufferCopy> s_upload_regions = new(); // this frame's dirty instance groups, in bytes
    private static readonly Dictionary<uint, OverrideHistory> s_override_previous = new(); // GetPreviousInstanceMatrix
    private static uint[] s_frame_slots = new uint[4096];
    private static int s_frame_slot_count;
    private static readonly List<uint> s_fresh_slots = new();
    private static uint[] s_batch_draw_index = Array.Empty<uint>();
    private static readonly List<InstanceCullRequest> s_view_requests = new();
    private static readonly List<InstanceCullRequest> s_shadow_requests = new();
    private static readonly List<InstanceCullRequest> s_shadow_request_pool = new();
    private static uint s_draw_data_capacity = renderer_max_draw_calls;

    private static RHI_Buffer CreateStorage<T>(uint count, string name, bool mappable = true) where T : unmanaged =>
        new(RHI_Buffer_Type.Storage, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), count, ReadOnlySpan<byte>.Empty, mappable, name);

    private static void CreateInstanceBuffers(uint capacity)
    {
        for (int i = 0; i < renderer_draw_data_buffer_count; i++)
        {
            s_instance_buffers[i]?.Dispose();
            s_instance_buffers[i] = CreateStorage<PackedInstance>(capacity, $"instances_upload_{i}");
        }
        for (int i = 0; i < s_instance_buffers_gpu.Length; i++)
        {
            s_instance_buffers_gpu[i]?.Dispose();
            s_instance_buffers_gpu[i] = CreateStorage<PackedInstance>(capacity, $"instances_{i}", mappable: false);
        }
        s_instance_capacity   = capacity;
        s_instances_gpu_reset = true;
    }

    private static void CreateSlotBuffers(uint capacity)
    {
        for (int i = 0; i < renderer_draw_data_buffer_count; i++)
        {
            s_slot_buffers[i]?.Dispose();
            s_slot_buffers[i] = CreateStorage<uint>(capacity, $"instance_slots_upload_{i}");
        }
        s_slot_buffer_gpu?.Dispose();
        s_slot_buffer_gpu = CreateStorage<uint>(capacity, "instance_slots", mappable: false);
        s_slot_capacity   = capacity;
    }

    private static void CreateInstancingBuffers()
    {
        CreateSlotBuffers(4096);
        CreateInstanceBuffers((uint)EntityStore.SlotBlockSize);
        s_fresh_pending.Clear();
        s_fresh_this_frame.Clear();
        s_override_previous.Clear();
        Phalanx.World.World.Store.HasRenderConsumer = true;
        s_draw_data_capacity = renderer_max_draw_calls;
    }

    private static void DestroyInstancingBuffers()
    {
        for (int i = 0; i < renderer_draw_data_buffer_count; i++)
        {
            s_slot_buffers[i]?.Dispose();
            s_instance_buffers[i]?.Dispose();
            s_slot_buffers[i] = s_instance_buffers[i] = null;
        }
        for (int i = 0; i < s_instance_buffers_gpu.Length; i++)
        {
            s_instance_buffers_gpu[i]?.Dispose();
            s_instance_buffers_gpu[i] = null;
        }
        s_slot_buffer_gpu?.Dispose();
        s_slot_buffer_gpu = null;
        s_fresh_pending.Clear();
        s_fresh_this_frame.Clear();
        s_view_requests.Clear();
        s_shadow_requests.Clear();
        s_shadow_request_pool.Clear();
        s_frame_slot_count = 0;
        Phalanx.World.World.Store.DetachRenderConsumer();
    }

    // DrawData grows with the frame (the first port dropped every draw past 32768): every in flight frame is waited
    // for, the buffer is made again with room for the next power of two and the bindless table points at it
    internal static uint DrawDataCapacity => s_draw_data_capacity;

    // the draw lists of the views PrepareViews last prepared, outside a frame (tests of views that aren't rendered yet)
    internal static void RebuildDrawCalls()
    {
        RHI_Device.QueueWaitAll();
        s_draw_data_count = 0;
        UpdateDrawCalls();
    }

    internal static void EnsureDrawDataCapacity(uint needed)
    {
        if (needed <= s_draw_data_capacity)
            return;
        uint capacity = System.Math.Max(System.Numerics.BitOperations.RoundUpToPowerOf2(needed), s_draw_data_capacity * 2);
        RHI_Device.QueueWaitAll();
        s_buffers[(int)Renderer_Buffer.DrawData]?.Dispose();
        s_buffers[(int)Renderer_Buffer.DrawData] = CreateStorage<DrawData>(capacity * renderer_draw_data_buffer_count, "draw_data");
        RHI_Device.UpdateBindlessDrawData(GetBuffer(Renderer_Buffer.DrawData));
        s_draw_data_capacity = capacity;
    }

    private static int ShadowRequestCount() => s_shadow_requests.Count;

    // the views' culling of the store, before their callbacks (PrepareViews)
    private static void CullStoreForViews(IReadOnlyList<SceneView> views)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        EntityStore store = Phalanx.World.World.Store;
        Phalanx.World.World.SyncStoreTransformsIfDirty();
        s_view_requests.Clear();
        foreach (SceneView view in views)
        {
            InstanceCullRequest request = view.State.Instances;
            Camera? camera = view.GetCamera();
            request.HasFrustum = false;
            request.MinPixels  = 0.0f; // without a camera nothing is culled
            if (camera != null)
            {
                Matrix4x4 view_matrix = camera.GetViewMatrix(), projection = camera.GetProjectionMatrix();
                request.SetFrustum(new Phalanx.Math.Frustum(view_matrix, projection));
                // the unjittered view projection (the frame constants' view_projection_unjittered) at the view's
                // render resolution, not its output one
                request.SetContribution(view_matrix * projection, new Vector2(view.State.RenderWidth, view.State.RenderHeight), MinPixelSize);
            }
            request.Mask          = GetCullMask(view);
            request.ShadowCasters = false;
            request.Callbacks     = true;
            view.State.PackedOverrides.Clear();
            s_view_requests.Add(request);
        }
        if (store.RenderableChunkCount > 0)
            store.CullInstances(s_view_requests);
        else
            foreach (InstanceCullRequest request in s_view_requests)
            {
                request.CallbackRows.Clear();
                request.Extras.Clear();
                request.RowsSkippedSmall = 0;
            }
        InstancingStats.CullMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Store entities whose bounding sphere projects to less than this many pixels of a view's render
    /// resolution on both axes aren't drawn in that view (spartan's contribution cull, CULL_CONTRIBUTION_MESH_PX = 2;
    /// each entity's threshold is dithered down to half of it; EntityStore.Culling.cs; --min-pixel-size). 0 draws them
    /// all. Shadow slices keep their own ShadowCasterMinTexels.</summary>
    public static float MinPixelSize { get; set; } = 2.0f;

    // a view's store entities with IPreDraw / IOnBecameVisible behaviours, then its lists (PrepareViews, per view)
    private static void RunStorePreDraw(SceneView view, int view_index)
    {
        ViewState s = view.State;
        InstanceCullRequest request = s.Instances;
        Camera? camera = view.GetCamera();
        EntityStore store = Phalanx.World.World.Store;
        foreach (InstanceCullRequest.CallbackRow row in request.CallbackRows)
        {
            Entity? facade = store.PeekFacade(row.Id.Index);
            if (facade == null || facade.GetComponent<Render>() is not Render render)
            {
                request.AddExtra(row.Batch, row.Slot);
                continue;
            }
            if (facade.m_pre_draw.Count == 0)
            {
                request.AddExtra(row.Batch, row.Slot);
                Phalanx.World.World.MarkVisible(facade);
                continue;
            }
            if (s.OverrideCount == s.Overrides.Count)
                s.Overrides.Add(new ViewDrawOverrides());
            int index = s.OverrideCount++;
            ViewDrawOverrides overrides = s.Overrides[index];
            overrides.Reset();
            var context = new ViewContext(view, view_index, camera, facade, render, overrides);
            Phalanx.World.World.InvokePreDraw(facade, in context);
            if (overrides.Hidden)
                continue;
            Phalanx.World.World.MarkVisible(facade);
            if (overrides.ChangesDraw)
                s.PackedOverrides.Add(new PackedOverride { slot = row.Slot, override_index = index, render = render, entity = facade });
            else
                request.AddExtra(row.Batch, row.Slot);
        }
        store.FinishCull(request);
    }

    // the geometry of a batch's mesh, lod 0 (null while the mesh isn't in the geometry buffer)
    private static bool TryGetBatchGeometry(RenderBatches.Info batch, out uint index_count, out uint index_offset, out uint vertex_offset)
    {
        index_count = index_offset = vertex_offset = 0;
        if (batch.Mesh is not Mesh mesh || !mesh.HasGpuBuffers() || batch.SubMesh >= mesh.GetSubMeshCount() || mesh.GetLodCount(batch.SubMesh) == 0)
            return false;
        MeshLod lod = mesh.GetLod(batch.SubMesh, 0);
        index_count   = lod.index_count;
        index_offset  = mesh.GetGlobalIndexOffset() + lod.index_offset;
        vertex_offset = mesh.GetGlobalVertexOffset() + lod.vertex_offset;
        return index_count > 0;
    }

    // the meshes of the store's batches go in the geometry buffer like the world's (UploadWorldMeshes)
    private static void UploadBatchMeshes()
    {
        int count = RenderBatches.Count;
        for (uint b = 1; b < count; b++)
        {
            if (RenderBatches.GetInfo(b).Mesh is Mesh mesh && !mesh.HasGpuBuffers() && Phalanx.World.World.Store.HasLiveBatch(b))
            {
                mesh.CreateGpuBuffers();
                s_uploaded_meshes.Add(mesh);
            }
        }
    }

    private static uint DrawDataNeeded()
    {
        uint needed = (uint)Phalanx.World.World.GetEntitiesRenderables().Count + (uint)RenderBatches.Count;
        foreach (SceneView view in s_prepared_views)
            needed += (uint)view.State.OverrideCount;
        return needed;
    }

    // the materials of the batches that have live store entities, after the world's (UpdateMaterials)
    private static void AddBatchMaterials()
    {
        EntityStore store = Phalanx.World.World.Store;
        if (store.RenderableChunkCount == 0)
            return;
        int count = RenderBatches.Count;
        for (uint b = 1; b < count; b++)
            if (RenderBatches.GetInfo(b).Material is Material material && store.HasLiveBatch(b))
                AddMaterial(material);
    }

    // a batch's DrawData entry: identity transforms (the instance has the matrix), its material and uv transform
    private static void WriteBatchDrawData()
    {
        int count = RenderBatches.Count;
        if (s_batch_draw_index.Length < count)
            s_batch_draw_index = new uint[System.Math.Max(count, s_batch_draw_index.Length * 2)];
        Array.Fill(s_batch_draw_index, uint.MaxValue);
        EntityStore store = Phalanx.World.World.Store;
        if (store.RenderableChunkCount == 0)
            return;
        for (uint b = 1; b < count; b++)
        {
            if (!store.HasLiveBatch(b))
                continue;
            RenderBatches.Info batch = RenderBatches.GetInfo(b);
            uint material_index = GetMaterialIndex(batch.Material ?? s_standard_material);
            s_batch_draw_index[b] = WriteBatchEntry(material_index, batch.Material ?? s_standard_material);
        }
    }

    private static uint WriteBatchEntry(uint material_index, Material? material)
    {
        static float Uv(Material? m, MaterialProperty p, float fallback) => m != null ? m.GetProperty(p) : fallback;
        var entry = new DrawData
        {
            transform          = Matrix4x4.Identity,
            transform_previous = Matrix4x4.Identity,
            material_index     = material_index,
            uv_tiling          = new Vector2(Uv(material, MaterialProperty.TextureTilingX, 1.0f), Uv(material, MaterialProperty.TextureTilingY, 1.0f)),
            uv_offset          = new Vector2(Uv(material, MaterialProperty.TextureOffsetX, 0.0f), Uv(material, MaterialProperty.TextureOffsetY, 0.0f)),
            uv_invert          = new Vector2(Uv(material, MaterialProperty.TextureInvertX, 0.0f), Uv(material, MaterialProperty.TextureInvertY, 0.0f)),
            uv_rotation        = Uv(material, MaterialProperty.TextureRotation, 0.0f),
            uv_world_space     = Uv(material, MaterialProperty.WorldSpaceUv, 0.0f),
        };
        return WriteDrawDataEntry(in entry);
    }

    // the previous frame's transform of a store entity drawn through an override (its own DrawData entry): the one it
    // was drawn with last frame, else its current one when its slot group didn't change in this frame's sync (or it is
    // new this frame). An entity that moved on the frame it starts being overridden has no velocity on that frame: the
    // host copies only hold this frame's dirty groups, there is no full previous frame copy to read it from
    private static Matrix4x4 GetPreviousInstanceMatrix(uint slot)
    {
        PackedTransform current = Phalanx.World.World.Store.Instances[slot];
        if (s_override_previous.TryGetValue(slot, out OverrideHistory history))
        {
            if (history.frame == s_frame_num - 1)
                return history.transform.GetMatrix();
            if (history.frame == s_frame_num) // a second view (or a rebuild) this frame
                return history.previous.GetMatrix();
        }
        return current.GetMatrix();
    }

    private readonly record struct OverrideHistory(ulong frame, PackedTransform transform, PackedTransform previous);

    // after a view's override draw data: what they are drawn with this frame is the next frame's previous transform
    private static void RememberOverrideTransforms(ViewState s)
    {
        PackedTransform[] instances = Phalanx.World.World.Store.Instances;
        foreach (PackedOverride o in s.PackedOverrides)
        {
            PackedTransform previous = instances[o.slot];
            if (s_override_previous.TryGetValue(o.slot, out OverrideHistory history))
                previous = history.frame == s_frame_num ? history.previous : history.frame == s_frame_num - 1 ? history.transform : previous;
            s_override_previous[o.slot] = new OverrideHistory(s_frame_num, instances[o.slot], previous);
        }
        if (s_override_previous.Count > 4096) // entities that stopped being overridden
        {
            s_override_stale.Clear();
            foreach (KeyValuePair<uint, OverrideHistory> entry in s_override_previous)
                if (entry.Value.frame + 1 < s_frame_num)
                    s_override_stale.Add(entry.Key);
            foreach (uint slot in s_override_stale)
                s_override_previous.Remove(slot);
        }
    }
    private static readonly List<uint> s_override_stale = new();

    // the per view OnPreDraw overrides of store entities: their own DrawData entries and draws (BuildViewDrawLists)
    private static void AddPackedOverrideDraws(ViewState s)
    {
        PackedTransform[] instances = Phalanx.World.World.Store.Instances;
        foreach (PackedOverride o in s.PackedOverrides)
        {
            ViewDrawOverrides overrides = s.Overrides[o.override_index];
            Render render = o.render;
            uint material_index = GetMaterialIndex(overrides.Material ?? GetRenderMaterial(render));
            Matrix4x4 current = instances[o.slot].GetMatrix();
            uint draw_index = WriteDrawData(current, GetPreviousInstanceMatrix(o.slot), material_index, 0, render, overrides);
            if (draw_index == uint.MaxValue || render.GetMesh() is not Mesh mesh || !mesh.HasGpuBuffers() || render.GetIndexCount() == 0)
                continue;
            (IsMaterialIndexAlphaTested(material_index) ? s.DrawCallsAlpha : s.DrawCalls).Add(new DrawCall
            {
                draw_index     = draw_index,
                material_index = material_index,
                index_count    = (uint)render.GetIndexCount(),
                index_offset   = render.GetIndexOffset(),
                vertex_offset  = render.GetVertexOffset(),
                render         = render,
            });
        }
    }

    // the slot groups written since this frame's device local instance buffer was (two frames ago: what changed in the
    // previous frame and in this one, so an entity that stopped moving gets prev = cur once), coalesced into byte
    // ranges, copied from the store into this frame slot's host buffer in parallel; RecordInstanceUploads copies the
    // same ranges on to the gpu. Every group after a growth.
    private static void UploadInstances()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        EntityStore store = Phalanx.World.World.Store;
        s_fresh_slots.Clear();
        store.TakeFreshSlots(s_fresh_slots);
        s_fresh_pending.AddRange(s_fresh_slots);
        s_fresh_this_frame.Clear();
        foreach (uint slot in s_fresh_slots)
            s_fresh_this_frame.Add(slot);
        InstancingStats.UploadedBytes = 0;
        uint slot_count = store.SlotCount;
        if (slot_count > s_instance_capacity)
        {
            // every frame in flight reads the old buffers until it completes
            RHI_Device.QueueWaitAll();
            CreateInstanceBuffers(System.Numerics.BitOperations.RoundUpToPowerOf2(slot_count));
        }
        uint version = s_instances_gpu_reset ? 0u : s_instance_gpu_version[GpuInstanceIndex(false)];
        uint[] stamps = store.SlotGroupStamp;
        int groups = store.SlotGroupCount;
        ulong end_bytes = (ulong)slot_count * 32ul;
        s_upload_regions.Clear();
        for (int g = 0; g < groups; g++)
        {
            if (version != 0 && stamps[g] <= version)
                continue;
            ulong first = (ulong)g * EntityStore.SlotGroupSize * 32ul;
            ulong bytes = System.Math.Min((ulong)EntityStore.SlotGroupSize * 32ul, end_bytes - first);
            if (s_upload_regions.Count > 0 && s_upload_regions[^1] is var last && last.SourceOffset + last.Size == first)
                s_upload_regions[^1] = last with { Size = last.Size + bytes };
            else
                s_upload_regions.Add(new RHI_BufferCopy(first, first, bytes));
            InstancingStats.UploadedBytes += (long)bytes;
        }
        s_upload_job.Prepare(store.Instances, s_instance_buffers[s_frame_cb_ring_slot]!, s_upload_regions);
        if (InstancingStats.UploadedBytes >= 4L << 20)
            WorkerPool.ParallelLoop(s_upload_job, s_upload_job.PieceCount);
        else
            for (int i = 0; i < s_upload_job.PieceCount; i++)
                s_upload_job.Execute(i);
        InstancingStats.UploadRegions = s_upload_regions.Count;
        InstancingStats.UploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // the host copy of the dirty ranges, in pieces of up to 512 KB for the workers
    private sealed class UploadJob : ParallelJob
    {
        private const ulong PieceBytes = 512ul << 10;
        private PackedTransform[] m_instances = Array.Empty<PackedTransform>();
        private RHI_Buffer? m_buffer;
        private readonly List<(ulong offset, ulong size)> m_pieces = new();
        public int PieceCount => m_pieces.Count;

        public void Prepare(PackedTransform[] instances, RHI_Buffer buffer, List<RHI_BufferCopy> regions)
        {
            m_instances = instances;
            m_buffer    = buffer;
            m_pieces.Clear();
            foreach (RHI_BufferCopy region in regions)
                for (ulong offset = 0; offset < region.Size; offset += PieceBytes)
                    m_pieces.Add((region.SourceOffset + offset, System.Math.Min(PieceBytes, region.Size - offset)));
        }

        public override void Execute(int index)
        {
            (ulong offset, ulong size) = m_pieces[index];
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(m_instances.AsSpan()).Slice((int)offset, (int)size);
            m_buffer!.UploadSubRegion(bytes, offset);
        }
    }
    private static readonly UploadJob s_upload_job = new();

    private static int AppendSlots(ReadOnlySpan<uint> slots)
    {
        int needed = s_frame_slot_count + slots.Length;
        if (needed > s_frame_slots.Length)
            Array.Resize(ref s_frame_slots, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)needed));
        int first = s_frame_slot_count;
        slots.CopyTo(s_frame_slots.AsSpan(first));
        s_frame_slot_count = needed;
        return first;
    }

    private static int AppendSlot(uint slot) => AppendSlots(new ReadOnlySpan<uint>(in slot));

    // a culled list's slots: the copy into the frame's list waits for UploadFrameSlots, which runs them in parallel
    private static int AppendSlotsDeferred(uint[] source, int start, int length)
    {
        int needed = s_frame_slot_count + length;
        if (needed > s_frame_slots.Length)
            Array.Resize(ref s_frame_slots, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)needed));
        int first = s_frame_slot_count;
        s_slots_job.Add(source, start, first, length);
        s_frame_slot_count = needed;
        return first;
    }

    // the deferred slot copies, then the whole list into the frame's host buffer, both in pieces over the workers
    private sealed class SlotsJob : ParallelJob
    {
        private const int PieceSlots = 128 << 10;
        private readonly List<(uint[] source, int start, int destination, int length)> m_copies = new();
        private readonly List<(uint[] source, int start, int destination, int length)> m_pieces = new();
        private RHI_Buffer? m_buffer;
        private bool m_upload;

        public void Add(uint[] source, int start, int destination, int length) => m_copies.Add((source, start, destination, length));

        public void Run(RHI_Buffer buffer, int count)
        {
            m_pieces.Clear();
            foreach ((uint[] source, int start, int destination, int length) in m_copies)
                for (int offset = 0; offset < length; offset += PieceSlots)
                    m_pieces.Add((source, start + offset, destination + offset, System.Math.Min(PieceSlots, length - offset)));
            m_copies.Clear();
            m_upload = false;
            Dispatch(m_pieces.Count, count);
            m_pieces.Clear();
            for (int offset = 0; offset < count; offset += PieceSlots)
                m_pieces.Add((s_frame_slots, offset, offset, System.Math.Min(PieceSlots, count - offset)));
            m_buffer = buffer;
            m_upload = true;
            Dispatch(m_pieces.Count, count);
            m_pieces.Clear();
            m_buffer = null;
        }

        public void Clear() => m_copies.Clear();

        private void Dispatch(int pieces, int count)
        {
            if (count >= 4 * PieceSlots)
                WorkerPool.ParallelLoop(this, pieces);
            else
                for (int i = 0; i < pieces; i++)
                    Execute(i);
        }

        public override void Execute(int index)
        {
            (uint[] source, int start, int destination, int length) = m_pieces[index];
            if (m_upload)
                m_buffer!.UploadSubRegion(MemoryMarshal.AsBytes(s_frame_slots.AsSpan(start, length)), (ulong)destination * sizeof(uint));
            else
                Array.Copy(source, start, s_frame_slots, destination, length);
        }
    }
    private static readonly SlotsJob s_slots_job = new();

    // a list of exact draws grouped by mesh and material, in the order of their first draw
    private static readonly Dictionary<(uint, uint, uint, uint, Material?), int> s_group_index = new();
    private static readonly List<List<DrawCall>> s_groups = new();
    private static int s_groups_used;

    private static void AddExactDraws(List<DrawCall> draws, List<InstancedDraw> into, bool shadow, Func<DrawCall, bool>? filter = null)
    {
        s_group_index.Clear();
        s_groups_used = 0;
        foreach (DrawCall draw in draws)
        {
            if (filter != null && !filter(draw))
                continue;
            Material? material = shadow ? GetRenderMaterial(draw.render!) : null;
            var key = (draw.index_count, draw.index_offset, draw.vertex_offset, draw.material_index, material);
            if (!s_group_index.TryGetValue(key, out int group))
            {
                group = s_groups_used++;
                if (group == s_groups.Count)
                    s_groups.Add(new List<DrawCall>());
                s_groups[group].Clear();
                s_group_index.Add(key, group);
            }
            s_groups[group].Add(draw);
        }
        for (int g = 0; g < s_groups_used; g++)
        {
            List<DrawCall> group = s_groups[g];
            DrawCall first = group[0];
            bool has_albedo = shadow && GetRenderMaterial(first.render!) is Material m && m.HasTextureOfType(MaterialTextureType.Color);
            if (group.Count == 1)
            {
                into.Add(new InstancedDraw { mode = instancing_mode_single, draw_index = first.draw_index, material_index = first.material_index,
                                             index_count = first.index_count, index_offset = first.index_offset, vertex_offset = first.vertex_offset,
                                             first_instance = 0, instance_count = 1, has_albedo = has_albedo });
                continue;
            }
            int start = s_frame_slot_count;
            foreach (DrawCall draw in group)
                AppendSlot(draw.draw_index);
            into.Add(new InstancedDraw { mode = instancing_mode_exact, draw_index = first.draw_index, material_index = first.material_index,
                                         index_count = first.index_count, index_offset = first.index_offset, vertex_offset = first.vertex_offset,
                                         first_instance = (uint)start, instance_count = (uint)group.Count, has_albedo = has_albedo });
        }
    }

    // the packed draws of a culling result: one per batch with a drawable mesh, into the opaque or the alpha list
    private static void AddPackedDraws(InstanceCullRequest request, List<InstancedDraw> opaque, List<InstancedDraw> alpha, bool shadow)
    {
        foreach (uint b in request.Batches)
        {
            if (b >= s_batch_draw_index.Length || s_batch_draw_index[b] == uint.MaxValue)
                continue;
            RenderBatches.Info batch = RenderBatches.GetInfo(b);
            if (!TryGetBatchGeometry(batch, out uint index_count, out uint index_offset, out uint vertex_offset))
                continue;
            Material? material = batch.Material ?? s_standard_material;
            if (shadow && (material == null || material.IsTransparent()))
                continue;
            uint material_index = GetMaterialIndex(material);
            ReadOnlySpan<uint> slots = request.GetSlots(b);
            if (slots.Length == 0)
                continue;
            int first = AppendSlotsDeferred(request.Slots, request.BatchStart[b], slots.Length);
            var draw = new InstancedDraw
            {
                mode = instancing_mode_packed, draw_index = s_batch_draw_index[b], material_index = material_index,
                index_count = index_count, index_offset = index_offset, vertex_offset = vertex_offset,
                first_instance = (uint)first, instance_count = (uint)slots.Length,
                has_albedo = material != null && material.HasTextureOfType(MaterialTextureType.Color),
            };
            (IsMaterialIndexAlphaTested(material_index) ? alpha : opaque).Add(draw);
            InstancingStats.Batches++;
            if (shadow) InstancingStats.ShadowInstances += slots.Length;
            else        InstancingStats.VisibleInstances += slots.Length;
        }
    }

    // the store's culling for the shadow slices (after UpdateLights placed them)
    private static void CullStoreForShadows()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        s_shadow_requests.Clear();
        InstancingStats.ShadowSkippedSmall = 0;
        EntityStore store = Phalanx.World.World.Store;
        for (int i = 0; i < s_shadow_slices.Count; i++)
        {
            ShadowSlice slice = s_shadow_slices[i];
            if (i == s_shadow_request_pool.Count)
                s_shadow_request_pool.Add(new InstanceCullRequest());
            InstanceCullRequest request = s_shadow_request_pool[i];
            Light light = slice.light;
            bool directional = light.GetLightType() == LightType.Directional;
            Phalanx.Math.Frustum frustum = slice.view != null ? slice.view.GetCascades(light).GetFrustum(slice.slice_index) : light.GetFrustum(slice.slice_index);
            request.SetFrustum(frustum, ignore_depth: directional);
            request.Mask          = uint.MaxValue; // the shadow maps draw every caster whatever its layers
            request.ShadowCasters = true;
            request.Callbacks     = false;
            SetShadowCasterLimits(request, slice, directional);
            s_shadow_requests.Add(request);
        }
        if (s_shadow_requests.Count > 0 && store.RenderableChunkCount > 0)
        {
            store.CullInstances(s_shadow_requests);
            store.FinishCulls(s_shadow_requests);
            foreach (InstanceCullRequest request in s_shadow_requests)
                InstancingStats.ShadowSkippedSmall += request.RowsSkippedSmall;
        }
        else
            foreach (InstanceCullRequest request in s_shadow_requests)
            {
                request.Batches.Clear();
                request.Total = 0;
                request.RowsSkippedSmall = 0;
            }
        InstancingStats.ShadowCullMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Store casters smaller than this many texels of a shadow slice (the diameter of their aabb sphere) are
    /// left out of it, 0 keeps them all (EntityStore.Culling.cs; --shadow-min-texels).</summary>
    public static float ShadowCasterMinTexels { get; set; } = 1.0f;

    // a slice's caster volume beyond its frustum: a cascade keeps its far plane (the side away from the light) and
    // drops the near one, a point or spot face is limited to the light's range; and the sub-texel threshold
    private static void SetShadowCasterLimits(InstanceCullRequest request, ShadowSlice slice, bool directional)
    {
        Light light = slice.light;
        float texels = ShadowCasterMinTexels;
        uint resolution = slice.resolution;
        if (directional)
        {
            Vector3 forward = light.GetEntity()!.GetForward();
            for (int p = 0; p < 2; p++)
                if (Vector3.Dot(request.Planes[p].Normal, forward) < 0.0f) // inside is toward the light: the far side
                    request.PlaneMask |= 1 << p;
            float extent = slice.slice_index == 0 ? DirectionalCascades.cascade_near_extent : DirectionalCascades.cascade_far_extent;
            if (texels > 0.0f && resolution > 0)
                request.MinDiameter = texels * 2.0f * extent / resolution;
            return;
        }
        Vector3 position = light.GetEntity()!.GetPosition();
        request.HasSphere    = true;
        request.SphereCenter = position;
        request.SphereRadius = light.GetRange();
        if (texels > 0.0f && resolution > 0)
        {
            // a face spans 2 tan(fov / 2) at distance 1 over its resolution
            float fov = light.GetLightType() == LightType.Spot ? light.GetAngle() * 2.0f : MathF.PI / 2.0f + 0.02f;
            request.SizeEye = position;
            request.MinDiameterPerDistance = texels * 2.0f * MathF.Tan(fov * 0.5f) / resolution;
        }
    }

    // every view's and slice's instanced draws and the frame's slot list (UpdateDrawCalls, after the DrawData entries)
    private static void BuildInstancedDraws()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        s_frame_slot_count = 0;
        InstancingStats.Batches = InstancingStats.VisibleInstances = InstancingStats.ShadowInstances = 0;
        InstancingStats.ShadowSlices.Clear();
        InstancingStats.Views.Clear();
        foreach (SceneView view in s_prepared_views)
        {
            ViewState s = view.State;
            s.Draws.Clear();
            s.DrawsAlpha.Clear();
            AddExactDraws(s.DrawCalls, s.Draws, shadow: false);
            AddExactDraws(s.DrawCallsAlpha, s.DrawsAlpha, shadow: false);
            int visible = InstancingStats.VisibleInstances;
            AddPackedDraws(s.Instances, s.Draws, s.DrawsAlpha, shadow: false);
            InstancingStats.Views.Add(new ViewInstanceStats(InstancingStats.VisibleInstances - visible, s.Instances.RowsSkippedSmall));
        }
        for (int i = 0; i < s_shadow_slices.Count; i++)
        {
            ShadowSlice slice = s_shadow_slices[i];
            slice.draws.Clear();
            slice.draws_alpha.Clear();
            Light light = slice.light;
            bool InSlice(DrawCall draw)
            {
                Render render = draw.render!;
                if (!render.HasFlag(RenderFlags.CastsShadows))
                    return false;
                Material? material = GetRenderMaterial(render);
                if (material == null || material.IsTransparent())
                    return false;
                return slice.view != null ? slice.view.GetCascades(light).IsInViewFrustum(render, slice.slice_index)
                                          : light.IsInViewFrustum(render, slice.slice_index);
            }
            AddExactDraws(s_draw_calls, slice.draws, shadow: true, InSlice);
            AddExactDraws(s_draw_calls_alpha, slice.draws_alpha, shadow: true, InSlice);
            if (i < s_shadow_requests.Count)
            {
                int before = InstancingStats.ShadowInstances;
                AddPackedDraws(s_shadow_requests[i], slice.draws, slice.draws_alpha, shadow: true);
                InstancingStats.ShadowSlices.Add(new ShadowSliceStats(light.GetLightType(), slice.slice_index,
                    InstancingStats.ShadowInstances - before, s_shadow_requests[i].RowsSkippedSmall));
            }
        }
        UploadFrameSlots();
        int draws = 0;
        foreach (SceneView view in s_prepared_views)
            draws += view.State.Draws.Count + view.State.DrawsAlpha.Count;
        foreach (ShadowSlice slice in s_shadow_slices)
            draws += slice.draws.Count + slice.draws_alpha.Count;
        InstancingStats.Draws  = draws;
        InstancingStats.ListsMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static void UploadFrameSlots()
    {
        if (s_frame_slot_count > s_slot_capacity)
        {
            RHI_Device.QueueWaitAll();
            CreateSlotBuffers(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)s_frame_slot_count));
        }
        if (s_frame_slot_count > 0)
            s_slots_job.Run(s_slot_buffers[s_frame_cb_ring_slot]!, s_frame_slot_count);
        else
            s_slots_job.Clear();
    }

    // the cpu side of the shaders' bounds checks: every instanced draw of the frame's views and shadow slices reads
    // inside the slot list, every listed slot is a store slot the instance buffers hold, every draw data index is one
    // of this frame's entries. Null when the frame is consistent, otherwise the first problem (tests, soak runs)
    internal static string? ValidateFrameDraws()
    {
        uint draw_first = s_frame_cb_ring_slot * s_draw_data_capacity;
        uint draw_end   = draw_first + s_draw_data_count;
        uint slot_count = System.Math.Min(Phalanx.World.World.Store.SlotCount, s_instance_capacity);
        string? Check(List<InstancedDraw> draws, string owner)
        {
            foreach (InstancedDraw draw in draws)
            {
                if (draw.mode != instancing_mode_exact && (draw.draw_index < draw_first || draw.draw_index >= draw_end))
                    return $"{owner}: draw data index {draw.draw_index} outside this frame's [{draw_first}, {draw_end})";
                if (draw.mode == instancing_mode_single)
                    continue;
                if ((ulong)draw.first_instance + draw.instance_count > (ulong)s_frame_slot_count || s_frame_slot_count > s_slot_capacity)
                    return $"{owner}: instances [{draw.first_instance}, +{draw.instance_count}) past the slot list ({s_frame_slot_count}, capacity {s_slot_capacity})";
                for (uint i = draw.first_instance; i < draw.first_instance + draw.instance_count; i++)
                {
                    uint value = s_frame_slots[i];
                    if (draw.mode == instancing_mode_exact && (value < draw_first || value >= draw_end))
                        return $"{owner}: listed draw data index {value} outside this frame's [{draw_first}, {draw_end})";
                    if (draw.mode == instancing_mode_packed && value >= slot_count)
                        return $"{owner}: listed slot {value} past the instance buffers ({slot_count})";
                }
            }
            return null;
        }
        foreach (SceneView view in s_prepared_views)
            if ((Check(view.State.Draws, view.Name) ?? Check(view.State.DrawsAlpha, view.Name)) is string problem)
                return problem;
        foreach (ShadowSlice slice in s_shadow_slices)
            if ((Check(slice.draws, $"shadow {slice.slice_index}") ?? Check(slice.draws_alpha, $"shadow {slice.slice_index}")) is string problem)
                return problem;
        return null;
    }

    // tests: the device local instances the last frame's shaders read (its own or, previous, the frame before's),
    // the first count slots, copied back after the gpu went idle
    internal static PackedTransform[] ReadGpuInstances(bool previous, int count)
    {
        var result = new PackedTransform[count];
        RHI_Buffer? source = s_instance_buffers_gpu[GpuInstanceIndex(previous)];
        if (source == null || count == 0)
            return result;
        RHI_Device.QueueWaitAll();
        using var readback = CreateStorage<PackedInstance>((uint)count, "instances_readback");
        RHI_CommandList cmd = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
        cmd.CopyBuffer(source, readback, new[] { new RHI_BufferCopy(0, 0, (ulong)count * (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<PackedInstance>()) });
        RHI_CommandList.ImmediateExecutionEnd(cmd);
        readback.ReadSubRegion(MemoryMarshal.AsBytes(result.AsSpan()), 0);
        return result;
    }

    // the device local buffer of this frame (and the previous frame's) out of the two
    private static int GpuInstanceIndex(bool previous) => (int)((s_frame_cb_ring_slot + (previous ? 1u : 0u)) % 2u);

    // on the frame's list before the passes: the blocks that changed since this frame's device local instance buffer
    // was written, the slots new this frame into the previous frame's buffer (no velocity on their first frame, every
    // block after a growth) and the slot list, between barriers against the vertex shaders that read them
    private static void RecordInstanceUploads(RHI_CommandList cmd)
    {
        RHI_Buffer staging = s_instance_buffers[s_frame_cb_ring_slot]!;
        RHI_Buffer current = s_instance_buffers_gpu[GpuInstanceIndex(false)]!;
        RHI_Buffer previous = s_instance_buffers_gpu[GpuInstanceIndex(true)]!;
        EntityStore store = Phalanx.World.World.Store;
        uint slot_count = System.Math.Min(store.SlotCount, s_instance_capacity);
        cmd.BeginMarker("instance_upload");
        cmd.InsertBarrierVertexReadUpload(before_copies: true);

        cmd.CopyBuffer(staging, current, CollectionsMarshal.AsSpan(s_upload_regions));
        if (s_instances_gpu_reset)
            cmd.CopyBuffer(staging, previous, CollectionsMarshal.AsSpan(s_upload_regions)); // every group after a growth
        else
        {
            s_copy_regions.Clear();
            foreach (uint slot in s_fresh_pending)
                if (slot < slot_count)
                    s_copy_regions.Add(new RHI_BufferCopy(slot * 32ul, slot * 32ul, 32ul));
            cmd.CopyBuffer(staging, previous, CollectionsMarshal.AsSpan(s_copy_regions));
        }
        if (s_frame_slot_count > 0)
        {
            s_slot_copy[0] = new RHI_BufferCopy(0, 0, (ulong)s_frame_slot_count * sizeof(uint));
            cmd.CopyBuffer(s_slot_buffers[s_frame_cb_ring_slot]!, s_slot_buffer_gpu!, s_slot_copy);
        }

        cmd.InsertBarrierVertexReadUpload(before_copies: false);
        cmd.EndMarker();
        s_instance_gpu_version[GpuInstanceIndex(false)] = store.SyncVersion;
        if (s_instances_gpu_reset)
            s_instance_gpu_version[GpuInstanceIndex(true)] = store.SyncVersion;
        s_instances_gpu_reset = false;
        s_fresh_pending.Clear();
    }

    // the device local instancing buffers of this frame, for the passes that draw (after SetStandardResources)
    private static void SetInstancingResources(RHI_CommandList cmd)
    {
        cmd.SetBuffer(binding_instance_slots, s_slot_buffer_gpu!);
        cmd.SetBuffer(binding_instances_current, s_instance_buffers_gpu[GpuInstanceIndex(false)]!);
        cmd.SetBuffer(binding_instances_previous, s_instance_buffers_gpu[GpuInstanceIndex(previous: InstanceVelocity)]!);
    }

    /// <summary>Spartan has no switch: off binds this frame's instance buffer as the previous one too, so the store
    /// entities get no motion of their own in the velocity buffer (the camera's still counts) and no draw reads the
    /// buffer the previous frame wrote (--instance-velocity off, a diagnostic). On by default.</summary>
    public static bool InstanceVelocity { get; set; } = true;

    // the prepass and g-buffer draw loop
    private static void DrawInstanced(RHI_CommandList cmd, List<InstancedDraw> draws)
    {
        RHI_Buffer? vertices = GeometryBuffer.GetVertexBuffer();
        RHI_Buffer? indices  = GeometryBuffer.GetIndexBuffer();
        if (vertices == null || indices == null || draws.Count == 0)
            return;
        SetInstancingResources(cmd);
        cmd.SetBufferVertex(vertices);
        cmd.SetBufferIndex(indices);
        foreach (InstancedDraw draw in draws)
        {
            var pass = new PassBufferData { draw_index = draw.draw_index, material_index = draw.material_index };
            pass.Set(16, draw.mode); // pass_instancing::mode
            cmd.PushConstants(pass);
            DrawIndexedInstances(cmd, in draw);
        }
    }

    /// <summary>The most instances one draw call carries, 0 for no limit (--max-instances-per-draw). A larger
    /// instanced draw is issued as consecutive draws over its slot range, drawing the same instances: smaller units
    /// of gpu work, the same result (spartan draws a Render's instances in one call).</summary>
    public static uint MaxInstancesPerDraw { get; set; }

    // one instanced draw, in pieces of MaxInstancesPerDraw instances when it is set (the prepass, the g-buffer and
    // the shadow maps)
    internal static void DrawIndexedInstances(RHI_CommandList cmd, in InstancedDraw draw)
    {
        uint limit = MaxInstancesPerDraw;
        if (limit == 0 || draw.instance_count <= limit)
        {
            cmd.DrawIndexed(draw.index_count, draw.index_offset, (int)draw.vertex_offset, draw.first_instance, draw.instance_count);
            return;
        }
        for (uint done = 0; done < draw.instance_count; done += limit)
            cmd.DrawIndexed(draw.index_count, draw.index_offset, (int)draw.vertex_offset, draw.first_instance + done, System.Math.Min(limit, draw.instance_count - done));
    }
}
