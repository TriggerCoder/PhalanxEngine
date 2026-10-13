using System.Numerics;
using Phalanx;
using Xunit;

namespace Phalanx.Tests;

public class WeldAndOptimizeTests
{
    private static RHI_Vertex_PosTexNorTan V(float x, float y, float z, float u, float v) =>
        new(new Vector3(x, y, z), new Vector2(u, v), Vector3.UnitY, Vector3.UnitX);

    // n x n grid, every triangle owns its 3 vertices; bumpy so the mesh has volume
    private static (List<RHI_Vertex_PosTexNorTan> v, List<uint> i) Soup(int n, bool bumpy = false)
    {
        var vertices = new List<RHI_Vertex_PosTexNorTan>();
        var indices  = new List<uint>();
        float H(int x, int z) => bumpy ? MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.5f) : 0.0f;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                foreach (var (px, pz) in new[] { (x, z), (x, z + 1), (x + 1, z), (x + 1, z), (x, z + 1), (x + 1, z + 1) })
                {
                    indices.Add((uint)vertices.Count);
                    vertices.Add(V(px, H(px, pz), pz, px / (float)n, pz / (float)n));
                }
        return (vertices, indices);
    }

    private static List<(Vector3, Vector3, Vector3)> Triangles(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        var t = new List<(Vector3, Vector3, Vector3)>();
        for (int k = 0; k < i.Count; k += 3)
        {
            // rotate so the smallest vertex comes first: same triangle, same winding, any start vertex
            var a = new[] { v[(int)i[k]].pos, v[(int)i[k + 1]].pos, v[(int)i[k + 2]].pos };
            int m = 0;
            for (int j = 1; j < 3; j++)
                if (string.CompareOrdinal(a[j].ToString(), a[m].ToString()) < 0) m = j;
            t.Add((a[m], a[(m + 1) % 3], a[(m + 2) % 3]));
        }
        t.Sort((p, q) => string.CompareOrdinal(p.ToString(), q.ToString()));
        return t;
    }

    private static void AssertValid(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        Assert.Equal(0, i.Count % 3);
        Assert.All(i, idx => Assert.True(idx < v.Count));
        // vertex fetch leaves no unreferenced vertices
        Assert.Equal(v.Count, i.Distinct().Count());
    }

    [Fact]
    public void Welds_Duplicates_InPlace()
    {
        var (v, i) = Soup(8);
        Assert.Equal(8 * 8 * 6, v.Count);

        int returned = GeometryProcessing.WeldAndOptimize(v, i);

        Assert.Equal(9 * 9, v.Count);
        Assert.Equal(v.Count, returned);
        AssertValid(v, i);
    }

    [Fact]
    public void Never_Simplifies_DenseMesh_AndKeepsExactTriangles()
    {
        // 80x80 bumpy grid = 38400 indices, above Optimize()'s 30000 density-simplify threshold
        var (v, i) = Soup(80, bumpy: true);
        var before = Triangles(v, i);

        GeometryProcessing.WeldAndOptimize(v, i);

        Assert.Equal(38400, i.Count);
        Assert.Equal(before, Triangles(v, i)); // same triangles, same winding, only reordered
        AssertValid(v, i);

        // contrast: Optimize() reduces the same input
        var (v2, i2) = Soup(80, bumpy: true);
        GeometryProcessing.Optimize(v2, i2);
        Assert.True(i2.Count < 38400);
    }

    [Fact]
    public void Degenerate_Triangles_AreKept()
    {
        // Spartan's weld_and_optimize doesn't strip degenerates (meshopt doesn't either), so the count is exact
        var v = new List<RHI_Vertex_PosTexNorTan> { V(0, 0, 0, 0, 0), V(1, 0, 0, 1, 0), V(0, 0, 1, 0, 1), V(0, 0, 0, 0, 0) };
        var i = new List<uint> { 0, 2, 1, 0, 3, 1 }; // second triangle collapses to an edge once 0 and 3 weld

        GeometryProcessing.WeldAndOptimize(v, i);

        Assert.Equal(6, i.Count);
        Assert.Equal(3, v.Count);
        AssertValid(v, i);
    }

    [Fact]
    public void Uv_Seam_IsNotWelded()
    {
        // two quads share the edge x=1 by position, but with different uvs (a texture seam)
        var v = new List<RHI_Vertex_PosTexNorTan>
        {
            V(0, 0, 0, 0.0f, 0), V(0, 0, 1, 0.0f, 1), V(1, 0, 0, 1.0f, 0), V(1, 0, 1, 1.0f, 1),
            V(1, 0, 0, 0.0f, 0), V(1, 0, 1, 0.0f, 1), V(2, 0, 0, 1.0f, 0), V(2, 0, 1, 1.0f, 1),
        };
        var i = new List<uint> { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 };

        GeometryProcessing.WeldAndOptimize(v, i);

        Assert.Equal(8, v.Count); // bytewise weld: same position, different uv -> kept apart
        Assert.Equal(12, i.Count);
        AssertValid(v, i);
        Assert.Equal(2, v.Count(x => x.pos == new Vector3(1, 0, 0)));
    }

    [Fact]
    public void Is_Idempotent()
    {
        var (v, i) = Soup(12, bumpy: true);
        GeometryProcessing.WeldAndOptimize(v, i);
        int vertex_count = v.Count;
        var triangles    = Triangles(v, i);
        var vertex_set   = v.ToHashSet();

        // a second pass is a geometric fixed point: nothing left to weld, same triangles and vertices
        // (meshopt's cache/overdraw passes may still permute the order, so the lists aren't compared verbatim)
        int returned = GeometryProcessing.WeldAndOptimize(v, i);

        Assert.Equal(vertex_count, returned);
        Assert.Equal(triangles, Triangles(v, i));
        Assert.True(vertex_set.SetEquals(v));
        AssertValid(v, i);
    }

    [Fact]
    public void Empty_Input_IsNoOp()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        Assert.Equal(0, GeometryProcessing.WeldAndOptimize(v, i));
    }

    [Fact]
    public void Mesh_PreserveLod0_UsesWeldAndOptimize()
    {
        using var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessOptimize);
        mesh.SetFlag(MeshFlags.PostProcessPreserveLod0);
        var (v, i) = Soup(80, bumpy: true);

        int sub = mesh.AddGeometry(v, i, generate_lods: false);

        var li = new List<uint>();
        var lv = new List<RHI_Vertex_PosTexNorTan>();
        mesh.GetGeometry(sub, li, lv);
        Assert.Equal(38400, li.Count); // not density-simplified
        Assert.Equal(81 * 81, lv.Count);
    }
}
