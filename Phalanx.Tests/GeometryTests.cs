using System.Numerics;
using Phalanx;
using Xunit;

namespace Phalanx.Tests;

public class GeometryTests
{
    // flat n x n quad grid on the xz plane, unindexed style (every triangle has its own 3 vertices)
    // so Optimize() has duplicates to weld
    private static (List<RHI_Vertex_PosTexNorTan> v, List<uint> i) Grid(int n, bool weld)
    {
        var vertices = new List<RHI_Vertex_PosTexNorTan>();
        var indices  = new List<uint>();
        RHI_Vertex_PosTexNorTan V(int x, int z) => new(new Vector3(x, 0, z), new Vector2(x / (float)n, z / (float)n), Vector3.UnitY, Vector3.UnitX);

        if (weld)
        {
            for (int z = 0; z <= n; z++)
                for (int x = 0; x <= n; x++)
                    vertices.Add(V(x, z));
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    uint a = (uint)(z * (n + 1) + x), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
                    indices.AddRange(new[] { a, c, b, b, c, d });
                }
        }
        else
        {
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    foreach (var (px, pz) in new[] { (x, z), (x, z + 1), (x + 1, z), (x + 1, z), (x, z + 1), (x + 1, z + 1) })
                    {
                        indices.Add((uint)vertices.Count);
                        vertices.Add(V(px, pz));
                    }
        }
        return (vertices, indices);
    }

    private static void AssertValid(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        Assert.True(i.Count % 3 == 0);
        Assert.All(i, idx => Assert.True(idx < v.Count));
    }

    private static float Area(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        float area = 0;
        for (int t = 0; t < i.Count; t += 3)
            area += Vector3.Cross(v[(int)i[t + 1]].pos - v[(int)i[t]].pos, v[(int)i[t + 2]].pos - v[(int)i[t]].pos).Length() * 0.5f;
        return area;
    }

    [Fact]
    public void Optimize_WeldsDuplicates_InPlace_AndKeepsSurface()
    {
        var (v, i) = Grid(16, weld: false);
        Assert.Equal(16 * 16 * 6, v.Count);

        int returned = GeometryProcessing.Optimize(v, i);

        Assert.Equal(17 * 17, v.Count);       // welded, caller's list was resized
        Assert.Equal(v.Count, returned);
        Assert.Equal(16 * 16 * 6, i.Count);   // below the 30000 index simplify threshold
        AssertValid(v, i);
        Assert.Equal(256f, Area(v, i), 3);
    }

    [Fact]
    public void Simplify_ReducesFlatGrid_WithValidIndices()
    {
        var (v, i) = Grid(32, weld: true);
        int original = i.Count;

        GeometryProcessing.Simplify(i, v, original / 4, preserve_uvs: true, preserve_edges: false);

        Assert.True(i.Count <= original / 4, $"{i.Count} > {original / 4}");
        Assert.True(i.Count >= 6);
        Assert.True(v.Count < 33 * 33);       // unreferenced vertices trimmed by vertex fetch
        AssertValid(v, i);
        Assert.Equal(32f * 32f, Area(v, i), 1); // planar, the surface must survive
    }

    [Fact]
    public void Simplify_PreserveEdges_KeepsBorderVertices()
    {
        var (v, i) = Grid(32, weld: true);
        var border = v.Where(x => x.pos.X == 0 || x.pos.X == 32 || x.pos.Z == 0 || x.pos.Z == 32).Select(x => x.pos).ToHashSet();

        GeometryProcessing.Simplify(i, v, 200, preserve_uvs: false, preserve_edges: true);

        AssertValid(v, i);
        var used = i.Select(idx => v[(int)idx].pos).ToHashSet();
        Assert.Subset(used, border);
    }

    [Fact]
    public void Simplify_BelowTarget_IsNoOp()
    {
        var (v, i) = Grid(4, weld: true);
        var before = i.ToList();
        GeometryProcessing.Simplify(i, v, i.Count + 10, true, false);
        Assert.Equal(before, i);
    }

    [Fact]
    public void Mesh_GetGeometry_ReturnsEachSubMesh()
    {
        using var mesh = new Mesh();
        var (v0, i0) = Grid(2, weld: true);
        var (v1, i1) = Grid(3, weld: true);
        for (int k = 0; k < v1.Count; k++) { var t = v1[k]; t.pos += new Vector3(100, 0, 0); v1[k] = t; }

        Assert.Equal(0, mesh.AddGeometry(v0, i0, generate_lods: false));
        Assert.Equal(1, mesh.AddGeometry(v1, i1, generate_lods: false));

        var oi = new List<uint> { 999 };
        var ov = new List<RHI_Vertex_PosTexNorTan>();
        mesh.GetGeometry(1, oi, ov);
        Assert.Equal(i1, oi);
        Assert.Equal(v1, ov);

        mesh.GetGeometry(0, oi, null);
        Assert.Equal(i0, oi);
        Assert.Equal(v0.Count + v1.Count, mesh.GetVertexCount());
    }

    [Fact]
    public void Mesh_GenerateLods_ProducesDecreasingLods()
    {
        using var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessGenerateLods);
        var (v, i) = Grid(48, weld: true);

        int sub = mesh.AddGeometry(v, i, generate_lods: true);

        int lods = mesh.GetLodCount(sub);
        Assert.True(lods > 1, $"lods={lods}");
        int prev = int.MaxValue;
        var li = new List<uint>();
        var lv = new List<RHI_Vertex_PosTexNorTan>();
        for (int l = 0; l < lods; l++)
        {
            Assert.True(mesh.GetGeometryLod(sub, l, li, lv));
            AssertValid(lv, li);
            Assert.True(li.Count < prev);
            prev = li.Count;
        }
    }

    [Fact]
    public void Assert_Fires()
    {
        Assert.Throws<PhalanxAssertException>(() => Helpers.PLXAssert(1 + 1 == 3));
        Helpers.PLXAssert(true);
    }
}

public class BoundingBoxTests
{
    [Fact]
    public void Default_IsEmpty_AndMergeAdopts()
    {
        var empty = new BoundingBox();
        Assert.True(empty.GetMin().X > empty.GetMax().X);
        var b = new BoundingBox(new Vector3(-1), new Vector3(2));
        empty.Merge(b);
        Assert.Equal(b, empty);
    }

    [Fact]
    public void FromVertices_ComputesExtents()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>
        {
            new(new Vector3(1, -2, 3), Vector2.Zero),
            new(new Vector3(-4, 5, 0), Vector2.Zero),
            new(new Vector3(0, 0, -6), Vector2.Zero),
        };
        var box = new BoundingBox(v);
        Assert.Equal(new Vector3(-4, -2, -6), box.GetMin());
        Assert.Equal(new Vector3(1, 5, 3), box.GetMax());
        Assert.Equal(new Vector3(-1.5f, 1.5f, -1.5f), box.GetCenter());
        Assert.Equal(box, new BoundingBox(v.Select(x => x.pos).ToArray(), 3));
    }

    [Fact]
    public void Intersects_And_Contains()
    {
        var a = new BoundingBox(Vector3.Zero, new Vector3(10));
        Assert.Equal(Math.Intersection.Inside,     a.Intersects(new BoundingBox(new Vector3(1), new Vector3(2))));
        Assert.Equal(Math.Intersection.Intersects, a.Intersects(new BoundingBox(new Vector3(5), new Vector3(15))));
        Assert.Equal(Math.Intersection.Outside,    a.Intersects(new BoundingBox(new Vector3(11), new Vector3(12))));
        Assert.Equal(Math.Intersection.Outside,    a.Intersects(new Vector3(-0.1f, 5, 5)));
        Assert.True(a.Contains(new Vector3(10, 0, 5)));
        Assert.Equal(new Vector3(10, 0, 5), a.GetClosestPoint(new Vector3(20, -3, 5)));
    }
}
