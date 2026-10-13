using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices; // Unsafe.SizeOf -> vertex stride passed to every meshopt call (VertexStride)
using System.Runtime.InteropServices;
using MeshOptimizer; // Alimer.Bindings.MeshOptimizer

namespace Phalanx;

// port of source/geometry/GeometryProcessing.cpp (Spartan 4c07baa)
// all meshoptimizer calls use the pointer overloads on pinned List<T> storage: positions are read straight from
// RHI_Vertex_PosTexNorTan.pos (offset 0) with the full vertex stride, exactly like &vertices[0].pos[0] in Spartan
public static unsafe class GeometryProcessing
{
    // meshopt_SimplifyPrune (1 << 3); the binding's SimplificationOptions enum doesn't name it
    private const SimplificationOptions SimplifyPrune = (SimplificationOptions)(1 << 3);

    // strategic error thresholds - fewer iterations with larger steps (same table as Spartan)
    private static readonly float[] s_error_thresholds = { 0.02f, 0.05f, 0.1f, 0.2f, 0.5f, 1.0f };
    private static readonly float[] s_uv_weights       = { 0.5f, 0.5f };

    private static nuint VertexStride => (nuint)Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>();

    public static void Simplify(
        List<uint> indices,
        List<RHI_Vertex_PosTexNorTan> vertices,
        int target_index_count,
        bool preserve_uvs,          // typically true, false for non-rendered geometry like physics meshes
        bool preserve_edges,        // for terrain tiles where edges must match neighboring tiles
        bool prune_components = false // distance LODs may remove small disconnected details
    )
    {
        int index_count  = indices.Count;
        int vertex_count = vertices.Count;

        // early exit conditions
        if (target_index_count >= index_count)
            return;

        if (vertex_count <= 16 || index_count < 12)
            return;

        target_index_count = System.Math.Max(target_index_count, 12);
        nuint target = (nuint)target_index_count;

        uint[]  indices_simplified = ArrayPool<uint>.Shared.Rent(index_count);
        uint[]? best_indices       = null;
        byte[]? vertex_locks       = null;
        float[]? attr_buffer       = null;

        try
        {
            ReadOnlySpan<RHI_Vertex_PosTexNorTan> verts = CollectionsMarshal.AsSpan(vertices);

            // build vertex lock array for edge preservation (terrain tiles)
            if (preserve_edges)
            {
                vertex_locks = ArrayPool<byte>.Shared.Rent(vertex_count);
                Array.Clear(vertex_locks, 0, vertex_count);

                float min_x = float.MaxValue, max_x = float.MinValue;
                float min_z = float.MaxValue, max_z = float.MinValue;
                foreach (ref readonly RHI_Vertex_PosTexNorTan v in verts)
                {
                    min_x = MathF.Min(min_x, v.pos.X);
                    max_x = MathF.Max(max_x, v.pos.X);
                    min_z = MathF.Min(min_z, v.pos.Z);
                    max_z = MathF.Max(max_z, v.pos.Z);
                }

                // lock vertices on bounding box perimeter
                const float edge_tolerance = 0.01f;
                for (int i = 0; i < vertex_count; i++)
                {
                    float x = verts[i].pos.X;
                    float z = verts[i].pos.Z;
                    if (MathF.Abs(x - min_x) < edge_tolerance || MathF.Abs(x - max_x) < edge_tolerance ||
                        MathF.Abs(z - min_z) < edge_tolerance || MathF.Abs(z - max_z) < edge_tolerance)
                    {
                        vertex_locks[i] = 1;
                    }
                }
            }

            // prepare uv attribute buffer for attribute-aware simplification
            nuint attr_stride = 0;
            nuint attr_count  = 0;
            if (preserve_uvs)
            {
                attr_buffer = ArrayPool<float>.Shared.Rent(vertex_count * 2);
                for (int i = 0; i < vertex_count; i++)
                {
                    attr_buffer[i * 2 + 0] = verts[i].tex.X;
                    attr_buffer[i * 2 + 1] = verts[i].tex.Y;
                }
                attr_stride = sizeof(float) * 2;
                attr_count  = 2;
            }

            float  lod_error      = 0.0f;
            nuint  result_count   = (nuint)index_count;
            bool   target_reached = false;
            SimplificationOptions options = prune_components && !preserve_edges ? SimplifyPrune : SimplificationOptions.None;

            fixed (uint* dst = indices_simplified)
            fixed (uint* src = CollectionsMarshal.AsSpan(indices))
            fixed (RHI_Vertex_PosTexNorTan* vtx = verts)
            fixed (float* attrs = attr_buffer)
            fixed (float* weights = s_uv_weights)
            fixed (byte* locks = vertex_locks)
            {
                float* positions   = (float*)vtx; // pos is the first field
                float* attr_ptr    = preserve_uvs ? attrs : null;
                float* weights_ptr = preserve_uvs ? weights : null;
                byte*  locks_ptr   = preserve_edges ? locks : null;

                for (int i = 0; i < s_error_thresholds.Length && !target_reached; i++)
                {
                    result_count = Meshopt.SimplifyWithAttributes(
                        dst, src, (nuint)index_count,
                        positions, (nuint)vertex_count, VertexStride,
                        attr_ptr, attr_stride, weights_ptr, attr_count,
                        locks_ptr, target, s_error_thresholds[i], options, &lod_error);

                    // pruning may cross several disconnected components at once, refine the
                    // error bound instead of accepting a nearly empty distant canopy
                    if (prune_components && result_count < target * 4 / 5)
                    {
                        best_indices ??= ArrayPool<uint>.Shared.Rent(index_count);
                        Array.Copy(indices_simplified, best_indices, (int)result_count);
                        nuint closest_count = result_count;
                        nuint closest_delta = target - result_count;
                        float lower_error   = i > 0 ? s_error_thresholds[i - 1] : 0.0f;
                        float upper_error   = s_error_thresholds[i];
                        for (int refinement = 0; refinement < 10; refinement++)
                        {
                            float error = (lower_error + upper_error) * 0.5f;
                            nuint count = Meshopt.SimplifyWithAttributes(
                                dst, src, (nuint)index_count,
                                positions, (nuint)vertex_count, VertexStride,
                                attr_ptr, attr_stride, weights_ptr, attr_count,
                                locks_ptr, target, error, preserve_edges ? SimplificationOptions.None : SimplifyPrune, &lod_error);
                            nuint delta = count > target ? count - target : target - count;
                            if (count > 0 && delta < closest_delta)
                            {
                                closest_delta = delta;
                                closest_count = count;
                                Array.Copy(indices_simplified, best_indices, (int)count);
                            }
                            if (count < target) upper_error = error;
                            else lower_error = error;
                        }
                        Array.Copy(best_indices, indices_simplified, (int)closest_count);
                        result_count = closest_count;
                        // close above-target results are preferable to stripping extra detail
                        target_reached = result_count <= target * 6 / 5;
                    }

                    // a plateau at one error bound does not prove a topology limit, later bounds can
                    // permit collapses that the earlier bound rejected, so keep going (no early break)
                    if (result_count <= target)
                        target_reached = true;
                }

                // sloppy reduction ignores uv seams and can join separate leaf cards into long
                // triangles, never use it when the caller requested uv preservation
                if (result_count > target && !preserve_uvs && !preserve_edges && target_index_count >= 12)
                {
                    best_indices ??= ArrayPool<uint>.Shared.Rent(index_count);
                    fixed (uint* sloppy = best_indices)
                    {
                        nuint sloppy_count = Meshopt.SimplifySloppy(
                            sloppy, src, (nuint)index_count,
                            positions, (nuint)vertex_count, VertexStride,
                            null, target, float.MaxValue, &lod_error);

                        if (sloppy_count > 0 && sloppy_count < result_count)
                        {
                            Array.Copy(best_indices, indices_simplified, (int)sloppy_count);
                            result_count = sloppy_count;
                        }
                    }
                }
            }

            // safeguard: if simplification collapsed to zero indices, keep original mesh
            if (result_count == 0)
            {
                Log.LogWarning("simplification collapsed mesh to zero indices, keeping original");
                return;
            }

            // commit simplified indices (in place, no reallocation since the count only shrinks)
            CollectionsMarshal.SetCount(indices, (int)result_count);
            indices_simplified.AsSpan(0, (int)result_count).CopyTo(CollectionsMarshal.AsSpan(indices));

            // optimize vertex buffer: removes unused vertices and reorders for cache efficiency
            OptimizeVertexFetchInPlace(vertices, indices);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(indices_simplified);
            if (best_indices != null) ArrayPool<uint>.Shared.Return(best_indices);
            if (vertex_locks != null) ArrayPool<byte>.Shared.Return(vertex_locks);
            if (attr_buffer != null)  ArrayPool<float>.Shared.Return(attr_buffer);
        }
    }

    // optimizes vertices and indices in place (the lists are resized), returns the new vertex count
    public static int Optimize(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        int vertex_count = vertices.Count;
        int index_count  = indices.Count;
        if (vertex_count == 0 || index_count == 0)
            return vertex_count;

        // step 1: vertex remapping (welds identical vertices)
        vertex_count = WeldInPlace(vertices, indices);

        // step 2: simplify with density-based targeting
        if (index_count > 30000)
        {
            Vector3 min = Math.Vector3.Infinity;
            Vector3 max = Math.Vector3.InfinityNeg;
            foreach (ref readonly RHI_Vertex_PosTexNorTan v in CollectionsMarshal.AsSpan(vertices))
            {
                min = Vector3.Min(min, v.pos);
                max = Vector3.Max(max, v.pos);
            }
            Vector3 extent = max - min;
            float volume   = extent.X * extent.Y * extent.Z;

            if (volume > 0.0f)
            {
                // triangles per unit volume; ratio = 0.8 at density 500, 0.2 at density 1000
                int triangle_count = index_count / 3;
                float density      = triangle_count / volume;
                float ratio        = 0.8f - (density - 500.0f) * (0.6f / 500.0f);
                ratio              = MathF.Max(0.2f, MathF.Min(0.8f, ratio));
                int target_index_count = (int)(index_count * ratio);

                Simplify(indices, vertices, target_index_count, true, false);

                index_count  = indices.Count;
                vertex_count = vertices.Count;
            }
        }

        // steps 3-4: vertex cache + overdraw (in place)
        OptimizeCacheAndOverdrawInPlace(vertices, indices);

        // step 5: vertex fetch optimization, trims unreferenced vertices
        return OptimizeVertexFetchInPlace(vertices, indices);
    }

    // port of geometry_processing::weld_and_optimize: welds bit-identical vertices and reorders for the caches,
    // never simplifies, so the output draws pixel for pixel the same as the input (Mesh uses it for PreserveLod0)
    // works in place (the lists are resized), returns the new vertex count
    public static int WeldAndOptimize(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        if (vertices.Count == 0 || indices.Count == 0)
            return vertices.Count;

        // weld, geometry that was assembled from separate pieces carries a duplicate for every vertex
        // the pieces had in common
        WeldInPlace(vertices, indices);

        OptimizeCacheAndOverdrawInPlace(vertices, indices);
        return OptimizeVertexFetchInPlace(vertices, indices);
    }

    // meshopt_generateVertexRemap compares the whole vertex bytewise (no epsilon), then remaps both buffers
    private static int WeldInPlace(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        int vertex_count = vertices.Count;
        int index_count  = indices.Count;
        uint[] remap = ArrayPool<uint>.Shared.Rent(vertex_count);
        RHI_Vertex_PosTexNorTan[] scratch = ArrayPool<RHI_Vertex_PosTexNorTan>.Shared.Rent(vertex_count);
        try
        {
            nuint welded_count;
            fixed (uint* remap_ptr = remap)
            fixed (uint* idx = CollectionsMarshal.AsSpan(indices))
            fixed (RHI_Vertex_PosTexNorTan* vtx = CollectionsMarshal.AsSpan(vertices))
            fixed (RHI_Vertex_PosTexNorTan* dst = scratch)
            {
                welded_count = Meshopt.GenerateVertexRemap(remap_ptr, idx, (nuint)index_count, vtx, (nuint)vertex_count, VertexStride);
                Meshopt.RemapIndexBuffer(idx, idx, (nuint)index_count, remap_ptr); // in place is supported
                Meshopt.RemapVertexBuffer(dst, vtx, (nuint)vertex_count, VertexStride, remap_ptr);
            }

            int count = (int)welded_count;
            CollectionsMarshal.SetCount(vertices, count);
            scratch.AsSpan(0, count).CopyTo(CollectionsMarshal.AsSpan(vertices));
            return count;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(remap);
            ArrayPool<RHI_Vertex_PosTexNorTan>.Shared.Return(scratch);
        }
    }

    private static void OptimizeCacheAndOverdrawInPlace(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        fixed (uint* idx = CollectionsMarshal.AsSpan(indices))
        fixed (RHI_Vertex_PosTexNorTan* vtx = CollectionsMarshal.AsSpan(vertices))
        {
            Meshopt.OptimizeVertexCache(idx, idx, (nuint)indices.Count, (nuint)vertices.Count);
            Meshopt.OptimizeOverdraw(idx, idx, (nuint)indices.Count, (float*)vtx, (nuint)vertices.Count, VertexStride, 1.05f);
        }
    }

    private static int OptimizeVertexFetchInPlace(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        int vertex_count = vertices.Count;
        RHI_Vertex_PosTexNorTan[] scratch = ArrayPool<RHI_Vertex_PosTexNorTan>.Shared.Rent(vertex_count);
        try
        {
            nuint optimized_vertex_count;
            fixed (uint* idx = CollectionsMarshal.AsSpan(indices))
            fixed (RHI_Vertex_PosTexNorTan* vtx = CollectionsMarshal.AsSpan(vertices))
            fixed (RHI_Vertex_PosTexNorTan* dst = scratch)
            {
                optimized_vertex_count = Meshopt.OptimizeVertexFetch(dst, idx, (nuint)indices.Count, vtx, (nuint)vertex_count, VertexStride);
            }

            int count = (int)optimized_vertex_count;
            CollectionsMarshal.SetCount(vertices, count);
            scratch.AsSpan(0, count).CopyTo(CollectionsMarshal.AsSpan(vertices));
            return count;
        }
        finally
        {
            ArrayPool<RHI_Vertex_PosTexNorTan>.Shared.Return(scratch);
        }
    }

    // NOTE: not present in Spartan 4c07baa's GeometryProcessing (it comes from an older revision); port it
    // from the revision you're following, or drop it
    public static void SplitSurfaceIntoTiles(
        List<RHI_Vertex_PosTexNorTan> terrain_vertices,
        List<uint> terrain_indices,
        uint tile_count,
        List<List<RHI_Vertex_PosTexNorTan>> tiled_vertices,
        List<List<uint>> tiled_indices,
        List<Vector3> tile_offsets)
    {
        throw new NotImplementedException("GeometryProcessing.SplitSurfaceIntoTiles is not ported yet");
    }
}
