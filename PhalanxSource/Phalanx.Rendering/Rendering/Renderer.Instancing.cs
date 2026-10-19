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
// - Shadow slices cull the store like views (every layer, casters only, no depth for the directional cascades).
// Deviations from spartan @ 4c07baa, which instances inside one Render (Render::SetInstances, a static instance list,
// Render.cpp:821) and culls the instances on the gpu (Renderer.cpp:3814-4051, common_vertex_processing.hlsl:58-86):
// - Batches are separate entities that share mesh and material; their instances move every frame, are culled on the
//   cpu per view and slice, and the lists are 4 byte slots into a per entity instance buffer, not instance ranges.
// - The instance buffers and the slot lists are bound in set 0 (bindings 340-342): llvmpipe binds only 8 descriptor
//   sets, so spartan's bindless geometry_instances (set 10) isn't usable by a pipeline there.
// - The world tick packs the instances before the frame slot is free, so they are copied (the blocks that changed in
//   the last frames) into the frame slot's host buffer instead of being written there directly.
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
    }

    // host visible, one per frame slot: the cpu writes them once the slot's frame finished, the gpu only copies them
    private static readonly RHI_Buffer?[] s_slot_buffers     = new RHI_Buffer?[renderer_draw_data_buffer_count];
    private static readonly RHI_Buffer?[] s_instance_buffers = new RHI_Buffer?[renderer_draw_data_buffer_count];
    private static readonly uint[] s_instance_region_version = new uint[renderer_draw_data_buffer_count];
    // device local, what the vertex shaders read: the slot list, this frame's and the previous frame's instances
    private static RHI_Buffer? s_slot_buffer_gpu;
    private static readonly RHI_Buffer?[] s_instance_buffers_gpu = new RHI_Buffer?[2];
    private static readonly uint[] s_instance_gpu_version = new uint[2];
    private static bool s_instances_gpu_reset;
    private static readonly List<uint> s_fresh_pending = new();      // new slots the next upload gives the previous gpu buffer
    private static readonly HashSet<uint> s_fresh_this_frame = new(); // new slots this frame: no previous transform
    private static readonly List<RHI_BufferCopy> s_copy_regions = new();
    private static uint s_slot_capacity, s_instance_capacity;
    private static bool s_instances_reset;
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
        s_instances_reset     = true;
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
        Array.Clear(s_instance_region_version);
        s_fresh_pending.Clear();
        s_fresh_this_frame.Clear();
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
            if (camera != null)
                request.SetFrustum(new Phalanx.Math.Frustum(camera.GetViewMatrix(), camera.GetProjectionMatrix()));
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
            }
        InstancingStats.CullMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

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

    // the previous frame's transform of a store entity's slot (the previous frame slot's host copy), the current one for
    // a slot new this frame
    private static Matrix4x4 GetPreviousInstanceMatrix(uint slot)
    {
        if (slot >= s_instance_capacity || s_fresh_this_frame.Contains(slot))
            return Phalanx.World.World.Store.Instances[slot].GetMatrix();
        uint previous = (s_frame_cb_ring_slot + renderer_draw_data_buffer_count - 1) % renderer_draw_data_buffer_count;
        Span<PackedInstance> one = stackalloc PackedInstance[1];
        s_instance_buffers[previous]!.ReadSubRegion(MemoryMarshal.AsBytes(one), (ulong)slot * 32ul);
        return one[0].GetMatrix();
    }

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

    // copies the instances that changed into this frame slot's host buffer (RecordInstanceUploads copies them on to the
    // gpu); after a growth every block goes into the previous slot's host buffer too, for GetPreviousInstanceMatrix
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
        uint current  = s_frame_cb_ring_slot;
        uint previous = (current + renderer_draw_data_buffer_count - 1) % renderer_draw_data_buffer_count;
        if (s_instances_reset)
            Array.Clear(s_instance_region_version); // every block is copied, into the previous slot's buffer too
        PackedTransform[] instances = store.Instances;
        uint[] stamps = store.SlotBlockStamp;
        RHI_Buffer buffer = s_instance_buffers[current]!;
        uint version = s_instance_region_version[current];
        int blocks = store.SlotBlockCount;
        for (int b = 0; b < blocks; b++)
        {
            if (stamps[b] <= version && version != 0)
                continue;
            int first = b * EntityStore.SlotBlockSize;
            int count = (int)System.Math.Min((uint)EntityStore.SlotBlockSize, slot_count - (uint)first);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(instances.AsSpan(first, count));
            buffer.UploadSubRegion(bytes, (ulong)first * 32ul);
            InstancingStats.UploadedBytes += bytes.Length;
            if (s_instances_reset)
                s_instance_buffers[previous]!.UploadSubRegion(bytes, (ulong)first * 32ul);
        }
        s_instance_region_version[current] = store.SyncVersion;
        if (s_instances_reset)
            s_instance_region_version[previous] = store.SyncVersion;
        s_instances_reset = false;
        InstancingStats.UploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

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
            int first = AppendSlots(slots);
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
            s_shadow_requests.Add(request);
        }
        if (s_shadow_requests.Count > 0 && store.RenderableChunkCount > 0)
        {
            store.CullInstances(s_shadow_requests);
            foreach (InstanceCullRequest request in s_shadow_requests)
                store.FinishCull(request);
        }
        else
            foreach (InstanceCullRequest request in s_shadow_requests)
            {
                request.Batches.Clear();
                request.Total = 0;
            }
        InstancingStats.ShadowCullMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // every view's and slice's instanced draws and the frame's slot list (UpdateDrawCalls, after the DrawData entries)
    private static void BuildInstancedDraws()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        s_frame_slot_count = 0;
        InstancingStats.Batches = InstancingStats.VisibleInstances = InstancingStats.ShadowInstances = 0;
        foreach (SceneView view in s_prepared_views)
        {
            ViewState s = view.State;
            s.Draws.Clear();
            s.DrawsAlpha.Clear();
            AddExactDraws(s.DrawCalls, s.Draws, shadow: false);
            AddExactDraws(s.DrawCallsAlpha, s.DrawsAlpha, shadow: false);
            AddPackedDraws(s.Instances, s.Draws, s.DrawsAlpha, shadow: false);
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
                AddPackedDraws(s_shadow_requests[i], slice.draws, slice.draws_alpha, shadow: true);
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
            s_slot_buffers[s_frame_cb_ring_slot]!.UploadSubRegion(MemoryMarshal.AsBytes(s_frame_slots.AsSpan(0, s_frame_slot_count)), 0);
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

        uint version = s_instances_gpu_reset ? 0u : s_instance_gpu_version[GpuInstanceIndex(false)];
        uint[] stamps = store.SlotBlockStamp;
        s_copy_regions.Clear();
        for (int b = 0; b < store.SlotBlockCount && (ulong)b * (ulong)EntityStore.SlotBlockSize < slot_count; b++)
        {
            if (version != 0 && stamps[b] <= version)
                continue;
            ulong first = (ulong)b * (ulong)EntityStore.SlotBlockSize;
            ulong bytes = System.Math.Min((ulong)EntityStore.SlotBlockSize, slot_count - first) * 32ul;
            if (s_copy_regions.Count > 0 && s_copy_regions[^1] is var last && last.SourceOffset + last.Size == first * 32ul)
                s_copy_regions[^1] = last with { Size = last.Size + bytes };
            else
                s_copy_regions.Add(new RHI_BufferCopy(first * 32ul, first * 32ul, bytes));
        }
        cmd.CopyBuffer(staging, current, CollectionsMarshal.AsSpan(s_copy_regions));
        if (!s_instances_gpu_reset)
        {
            s_copy_regions.Clear();
            foreach (uint slot in s_fresh_pending)
                if (slot < slot_count)
                    s_copy_regions.Add(new RHI_BufferCopy(slot * 32ul, slot * 32ul, 32ul));
        }
        cmd.CopyBuffer(staging, previous, CollectionsMarshal.AsSpan(s_copy_regions));
        if (s_frame_slot_count > 0)
            cmd.CopyBuffer(s_slot_buffers[s_frame_cb_ring_slot]!, s_slot_buffer_gpu!, new[] { new RHI_BufferCopy(0, 0, (ulong)s_frame_slot_count * sizeof(uint)) });

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
        cmd.SetBuffer(binding_instances_previous, s_instance_buffers_gpu[GpuInstanceIndex(true)]!);
    }

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
            cmd.DrawIndexed(draw.index_count, draw.index_offset, (int)draw.vertex_offset, draw.first_instance, draw.instance_count);
        }
    }
}
