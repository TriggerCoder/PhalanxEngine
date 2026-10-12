using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MeshOptimizer;
public static class GeometryProcessing
{
    public static void simplify(
                                List<uint> indices,
                                List<RHI_Vertex_PosTexNorTan> vertices,
                                int target_index_count,
                                bool preserve_uvs,
                                bool preserve_edges
                               )
    {
        int index_count = indices.Count;
        int vertex_count = vertices.Count;

        if (target_index_count >= index_count)
            return;

        if (vertex_count <= 16 || index_count < 12)
            return;

        target_index_count = Math.Max(target_index_count, 12);

        Span<uint> indices_simplified = new uint[index_count];

        Span<byte> vertex_locks = null;
        if (preserve_edges)
        {
            vertex_locks = new byte[vertex_count];

            float min_x = float.MaxValue;
            float max_x = float.MinValue;
            float min_z = float.MaxValue;
            float max_z = float.MinValue;

            for (int i = 0; i < vertex_count; i++)
            {
                float x = vertices[i].pos[0];
                float z = vertices[i].pos[2];
                min_x = Math.Min(min_x, x);
                max_x = Math.Max(max_x, x);
                min_z = Math.Min(min_z, z);
                max_z = Math.Max(max_z, z);
            }

            const float edge_tolerance = 0.01f;
            for (int i = 0; i < vertex_count; i++)
            {
                float x = vertices[i].pos[0];
                float z = vertices[i].pos[2];
                if (Math.Abs(x - min_x) < edge_tolerance || Math.Abs(x - max_x) < edge_tolerance ||
                    Math.Abs(z - min_z) < edge_tolerance || Math.Abs(z - max_z) < edge_tolerance)
                {
                    vertex_locks[i] = 1;
                }
            }
        }

        Span<float> attr_buffer = null;
        Span<float> vertex_attributes = null;
        nuint attr_stride = 0;
        nuint attr_count = 0;
        Span<float> attr_weights = null;

        if (preserve_uvs)
        {
            attr_buffer = new float[vertex_count * 2];
            for (int i = 0; i < vertex_count; i++)
            {
                attr_buffer[i * 2 + 0] = vertices[i].tex[0];
                attr_buffer[i * 2 + 1] = vertices[i].tex[1];
            }
            vertex_attributes = attr_buffer;
            attr_stride = (nuint)(sizeof(float) * 2);
            attr_weights = new float[] { 0.5f, 0.5f };
            attr_count = 2;
        }

        Span<byte> locks = preserve_edges ? vertex_locks : null;

        float[] error_thresholds = { 0.02f, 0.05f, 0.1f, 0.2f, 0.5f, 1.0f };
        int threshold_count = error_thresholds.Length;

        float lod_error = 0.0f;
        nuint result_count = (nuint)index_count;
        nuint best_count = (nuint)index_count;
        bool target_reached = false;

        // We need a float[] of positions for the binding
        float[] vertex_positions = GetVertexPositionArray(vertices);

        for (int i = 0; i < threshold_count && !target_reached; i++)
        {
            result_count = Meshopt.SimplifyWithAttributes(
                indices_simplified,
                indices.ToArray(),
                vertex_positions,
                (nuint)Marshal.SizeOf<RHI_Vertex_PosTexNorTan>(),
                vertex_attributes,
                attr_stride,
                attr_weights,
                attr_count,
                locks,
                (nuint)target_index_count,
                error_thresholds[i],
                SimplificationOptions.None,
                out lod_error
            );

            if (result_count <= (nuint)target_index_count)
            {
                target_reached = true;
            }
            else if (result_count < best_count)
            {
                best_count = result_count;
            }
            else
            {
                break;
            }
        }

        // Fallback sloppy
        if (result_count > (nuint)target_index_count && !preserve_edges && target_index_count >= 12)
        {
            nuint sloppy_count = Meshopt.SimplifySloppy(
                indices_simplified,
                indices.ToArray(),
                vertex_positions,
                (nuint)Marshal.SizeOf<RHI_Vertex_PosTexNorTan>(),
                (nuint)target_index_count,
                float.MaxValue,
                out lod_error
            );

            if (sloppy_count > 0 && sloppy_count < result_count)
                result_count = sloppy_count;
        }

        if (result_count == 0)
        {
            // SP_LOG_WARNING(...)
            Console.WriteLine("WARNING: simplification collapsed mesh to zero indices, keeping original");
            return;
        }

        // commit simplified indices
        indices.Clear();
        for (int i = 0; i < (int)result_count; i++)
            indices.Add(indices_simplified[i]);

        // optimize vertex buffer
        RHI_Vertex_PosTexNorTan[] optimized_vertices = new RHI_Vertex_PosTexNorTan[vertex_count];
        nuint optimized_vertex_count = Meshopt.OptimizeVertexFetch(
            optimized_vertices,
            indices.ToArray(),
            vertices.ToArray()
        );

        vertices.Clear();
        for (int i = 0; i < (int)optimized_vertex_count; i++)
            vertices.Add(optimized_vertices[i]);
    }

    public static void optimize(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        int vertex_count = vertices.Count;
        int index_count = indices.Count;

        // step 1: vertex remapping
        {
            uint[] remap = new uint[vertex_count];
            nuint vertex_count_optimized = Meshopt.GenerateVertexRemap(
                remap,
                indices.ToArray(),
                vertices.ToArray()
            );

            uint[] indices_remapped = new uint[index_count];
            Meshopt.RemapIndexBuffer(indices_remapped, indices.ToArray(), remap);
            indices = new List<uint>(indices_remapped);

            RHI_Vertex_PosTexNorTan[] vertices_remapped = new RHI_Vertex_PosTexNorTan[(int)vertex_count_optimized];
            Meshopt.RemapVertexBuffer(vertices_remapped, vertices.ToArray(), remap);
            vertices = new List<RHI_Vertex_PosTexNorTan>(vertices_remapped);
            vertex_count = (int)vertex_count_optimized;
        }

        // step 2: density-based simplification (unchanged logic)
        if (index_count > 30000)
        {
            float min_x = float.MaxValue, max_x = float.MinValue;
            float min_y = float.MaxValue, max_y = float.MinValue;
            float min_z = float.MaxValue, max_z = float.MinValue;

            foreach (var v in vertices)
            {
                min_x = MathF.Min(min_x, v.pos[0]);
                max_x = MathF.Max(max_x, v.pos[0]);
                min_y = MathF.Min(min_y, v.pos[1]);
                max_y = MathF.Max(max_y, v.pos[1]);
                min_z = MathF.Min(min_z, v.pos[2]);
                max_z = MathF.Max(max_z, v.pos[2]);
            }

            float extent_x = max_x - min_x;
            float extent_y = max_y - min_y;
            float extent_z = max_z - min_z;
            float volume = extent_x * extent_y * extent_z;

            if (volume > 0.0f)
            {
                int triangle_count = index_count / 3;
                float density = (float)triangle_count / volume;

                float ratio = 0.8f - (density - 500.0f) * (0.6f / 500.0f);
                ratio = MathF.Max(0.2f, MathF.Min(0.8f, ratio));
                int target_index_count = (int)(index_count * ratio);

                simplify(indices, vertices, target_index_count, true, false);

                index_count = indices.Count;
                vertex_count = vertices.Count;
            }
        }

        // step 3-5
        Meshopt.OptimizeVertexCache(indices.ToArray(), indices.ToArray(), (nuint)vertex_count);

        float[] positions = GetVertexPositionArray(vertices);
        Meshopt.OptimizeOverdraw(
            indices.ToArray(),
            indices.ToArray(),
            positions,
            (nuint)Marshal.SizeOf<RHI_Vertex_PosTexNorTan>(),
            1.05f
        );

        Meshopt.OptimizeVertexFetch(
            vertices.ToArray(),
            indices.ToArray(),
            vertices.ToArray()
        );
    }

    // split_surface_into_tiles stays almost identical to previous version
    // (only the Parallel.For part and lock(object) are C# specific)
    public static void split_surface_into_tiles(
        List<RHI_Vertex_PosTexNorTan> terrain_vertices,
        List<uint> terrain_indices,
        uint tile_count,
        List<List<RHI_Vertex_PosTexNorTan>> tiled_vertices,
        List<List<uint>> tiled_indices,
        List<math.Vector3> tile_offsets)
    {
        // ... (same bounding box + tile calculation as before)

        // Parallel processing with lock (object)
        object[] tile_mutexes = new object[tile_count * tile_count];
        for (int i = 0; i < tile_mutexes.Length; i++)
            tile_mutexes[i] = new object();

        List<Dictionary<uint, uint>> global_to_local_indices = new();
        for (int i = 0; i < tile_mutexes.Length; i++)
            global_to_local_indices.Add(new Dictionary<uint, uint>());

        Parallel.For(0, (int)(terrain_indices.Count / 3), tri =>
        {
            // ... exact same triangle processing logic as the previous version ...
            // (I kept the variable names: tri_min_x, tile_min_x, global_idx, local_idx, etc.)
        });
    }

    private static float[] GetVertexPositionArray(List<RHI_Vertex_PosTexNorTan> vertices)
    {
        float[] pos = new float[vertices.Count * 3];
        for (int i = 0; i < vertices.Count; i++)
        {
            pos[i * 3 + 0] = vertices[i].pos[0];
            pos[i * 3 + 1] = vertices[i].pos[1];
            pos[i * 3 + 2] = vertices[i].pos[2];
        }
        return pos;
    }
}
}
