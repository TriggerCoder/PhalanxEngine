// Port of SpartanEngine source/rendering/GeometryBuffer.cpp @ 4c07baa, the vertex, index and instance streams: the
// shared buffers every mesh appends to (AppendVertices :257, AppendIndices :268, AppendInstances :348), grown and
// uploaded once per frame by BuildIfDirty (:480-714) and released by Shutdown (:788).
// The gpu buffers hold the only full copy; the cpu keeps the tail appended since the last upload, a grown buffer
// takes the committed range over gpu side (adopt -> RHI_CommandList.CopyBufferContents).
// Deviations: the meshlet streams, the impostor texels, the staged deformable writes, the reserve hints and the
// out-of-memory bookkeeping are not ported yet (no meshlet, impostor or skinning passes).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering.RHI;

namespace Phalanx.Rendering;

public static class GeometryBuffer
{
    private const double growth_factor = 1.25;
    private const ulong max_slack_bytes = 64ul * 1024ul * 1024ul;

    private sealed class Stream<T> where T : unmanaged
    {
        public readonly List<T> tail = new(); // elements [committed, size)
        public uint committed;                // elements already resident on the gpu
        public uint size() => committed + (uint)tail.Count;
        public void reset() { tail.Clear(); tail.TrimExcess(); committed = 0; }
    }

    private static readonly object s_mutex = new();
    private static readonly Stream<RHI_Vertex_PosTexNorTan> s_vertices = new();
    private static readonly Stream<uint> s_indices = new();
    private static readonly Stream<PackedInstance> s_instances = new();
    private static RHI_Buffer? s_vertex_buffer, s_index_buffer, s_instance_buffer;
    private static uint s_vertex_capacity, s_index_capacity, s_instance_capacity;
    private static bool s_dirty;

    public static uint AppendVertices(ReadOnlySpan<RHI_Vertex_PosTexNorTan> data)
    {
        lock (s_mutex)
        {
            uint base_offset = s_vertices.size();
            foreach (var v in data) s_vertices.tail.Add(v);
            s_dirty = true;
            return base_offset;
        }
    }

    public static uint AppendIndices(ReadOnlySpan<uint> data)
    {
        lock (s_mutex)
        {
            uint base_offset = s_indices.size();
            foreach (uint i in data) s_indices.tail.Add(i);
            s_dirty = true;
            return base_offset;
        }
    }

    public static uint AppendInstances(ReadOnlySpan<PackedInstance> data)
    {
        lock (s_mutex)
        {
            // seed slot 0 with an identity instance so non-instanced draws can read identity at offset 0
            if (s_instances.size() == 0)
            {
                s_instances.tail.Add(PackedInstance.GetIdentity());
                s_dirty = true;
            }
            uint base_offset = s_instances.size();
            if (data.Length > 0)
            {
                foreach (var i in data) s_instances.tail.Add(i);
                s_dirty = true;
            }
            return base_offset;
        }
    }

    public static void BuildIfDirty()
    {
        lock (s_mutex)
        {
            if (!s_dirty || s_vertices.size() == 0 || s_indices.size() == 0)
                return;

            // ensure slot 0 always has an identity entry so non-instanced indirect draws read identity
            if (s_instances.size() == 0)
                s_instances.tail.Add(PackedInstance.GetIdentity());

            uint vertex_count   = s_vertices.size();
            uint index_count    = s_indices.size();
            uint instance_count = s_instances.size();

            if (s_vertex_buffer == null || s_index_buffer == null || s_instance_buffer == null ||
                vertex_count > s_vertex_capacity || index_count > s_index_capacity || instance_count > s_instance_capacity)
            {
                uint new_vertex_capacity   = System.Math.Max(add_headroom(vertex_count, (ulong)Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>()), s_vertex_capacity);
                uint new_index_capacity    = System.Math.Max(add_headroom(index_count, sizeof(uint)), s_index_capacity);
                uint new_instance_capacity = System.Math.Max(add_headroom(instance_count, (ulong)Unsafe.SizeOf<PackedInstance>()), System.Math.Max(s_instance_capacity, 1u));

                if (s_vertex_buffer == null || vertex_count > s_vertex_capacity)
                {
                    adopt(ref s_vertex_buffer, new RHI_Buffer(RHI_Buffer_Type.Vertex, (ulong)Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>(), new_vertex_capacity,
                                                              ReadOnlySpan<byte>.Empty, mappable: false, "geometry_buffer_vertex"),
                          (ulong)s_vertices.committed * (ulong)Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>());
                    s_vertex_capacity = new_vertex_capacity;
                }
                if (s_index_buffer == null || index_count > s_index_capacity)
                {
                    adopt(ref s_index_buffer, new RHI_Buffer(RHI_Buffer_Type.Index, sizeof(uint), new_index_capacity,
                                                             ReadOnlySpan<byte>.Empty, mappable: false, "geometry_buffer_index"),
                          (ulong)s_indices.committed * sizeof(uint));
                    s_index_capacity = new_index_capacity;
                }
                if (s_instance_buffer == null || instance_count > s_instance_capacity)
                {
                    adopt(ref s_instance_buffer, new RHI_Buffer(RHI_Buffer_Type.Instance, (ulong)Unsafe.SizeOf<PackedInstance>(), new_instance_capacity,
                                                                ReadOnlySpan<byte>.Empty, mappable: false, "geometry_buffer_instances"),
                          (ulong)s_instances.committed * (ulong)Unsafe.SizeOf<PackedInstance>());
                    s_instance_capacity = new_instance_capacity;
                }

                Log.LogInfo($"Global geometry buffer built: {vertex_count} vertices, {index_count} indices, {instance_count} instances, capacity: {s_vertex_capacity}/{s_index_capacity}/{s_instance_capacity}");
            }

            upload_tail(s_vertices, s_vertex_buffer!);
            upload_tail(s_indices, s_index_buffer!);
            upload_tail(s_instances, s_instance_buffer!);
            s_dirty = false;
        }
    }

    public static void Shutdown()
    {
        lock (s_mutex)
        {
            s_vertex_buffer?.Dispose();   s_vertex_buffer   = null;
            s_index_buffer?.Dispose();    s_index_buffer    = null;
            s_instance_buffer?.Dispose(); s_instance_buffer = null;
            s_vertices.reset(); s_indices.reset(); s_instances.reset();
            s_vertex_capacity = s_index_capacity = s_instance_capacity = 0;
            s_dirty = false;
        }
    }

    internal static RHI_Buffer? GetVertexBuffer()   { lock (s_mutex) return s_vertex_buffer; }
    internal static RHI_Buffer? GetIndexBuffer()    { lock (s_mutex) return s_index_buffer; }
    internal static RHI_Buffer? GetInstanceBuffer() { lock (s_mutex) return s_instance_buffer; }
    public static uint GetVertexCount()   { lock (s_mutex) return s_vertices.size(); }
    public static uint GetIndexCount()    { lock (s_mutex) return s_indices.size(); }
    public static uint GetInstanceCount() { lock (s_mutex) return s_instances.size(); }

    // a flat 25% headroom on a multi-gb buffer wastes hundreds of mb, clamp the slack to 64 mb
    internal static uint add_headroom(ulong count, ulong stride)
    {
        ulong default_grown_count = (ulong)(count * growth_factor);
        ulong default_slack_bytes = (default_grown_count - count) * stride;
        if (default_slack_bytes > max_slack_bytes)
            return (uint)(count + max_slack_bytes / System.Math.Max(stride, 1ul));
        return (uint)default_grown_count;
    }

    private static void upload_tail<T>(Stream<T> stream, RHI_Buffer buffer) where T : unmanaged
    {
        if (stream.tail.Count > 0)
        {
            ulong offset = (ulong)stream.committed * (ulong)Unsafe.SizeOf<T>();
            buffer.UploadSubRegion(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(stream.tail)), offset);
        }
        stream.committed = stream.size();
        stream.tail.Clear();
        if (stream.tail.Capacity * Unsafe.SizeOf<T>() > 4 * 1024 * 1024)
            stream.tail.TrimExcess();
    }

    // growth keeps offsets and contents, the committed range moves gpu side so the cpu never needs it back
    private static void adopt(ref RHI_Buffer? current, RHI_Buffer replacement, ulong committed_bytes)
    {
        if (current != null && committed_bytes > 0)
            RHI_CommandList.CopyBufferContents(current, replacement, committed_bytes);
        current?.Dispose();
        current = replacement;
    }
}
