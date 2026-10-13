using System.Numerics;
using Phalanx;
using Xunit;

namespace Phalanx.Tests;

public class ThinFoliageCardsTests
{
    // `count` disconnected leaf cards (one quad = 2 triangles, 4 vertices each), deterministic placement
    private static void AddCards(List<RHI_Vertex_PosTexNorTan> v, List<uint> i, int count, int seed = 1234)
    {
        var rng = new Random(seed);
        for (int c = 0; c < count; c++)
        {
            var o = new Vector3(rng.NextSingle() * 20, rng.NextSingle() * 20, rng.NextSingle() * 20);
            uint b = (uint)v.Count;
            v.Add(new(o + new Vector3(-0.5f, -0.5f, 0), new Vector2(0, 0), Vector3.UnitZ));
            v.Add(new(o + new Vector3( 0.5f, -0.5f, 0), new Vector2(1, 0), Vector3.UnitZ));
            v.Add(new(o + new Vector3(-0.5f,  0.5f, 0), new Vector2(0, 1), Vector3.UnitZ));
            v.Add(new(o + new Vector3( 0.5f,  0.5f, 0), new Vector2(1, 1), Vector3.UnitZ));
            i.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
    }

    // one connected n x n sheet (2n^2 triangles, > card_triangles_max when n >= 6)
    private static void AddSheet(List<RHI_Vertex_PosTexNorTan> v, List<uint> i, int n, Vector3 o)
    {
        uint b = (uint)v.Count;
        for (int z = 0; z <= n; z++)
            for (int x = 0; x <= n; x++)
                v.Add(new(o + new Vector3(x, 0, z), new Vector2(x, z), Vector3.UnitY));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                uint a = b + (uint)(z * (n + 1) + x), c = a + (uint)(n + 1);
                i.AddRange(new[] { a, c, a + 1, a + 1, c, c + 1 });
            }
    }

    // Spartan's arithmetic for the number of small cards that survive
    private static int ExpectedKept(int small_cards, int small_indices, int large_indices, int target)
    {
        float keep = System.Math.Clamp((float)((double)target - large_indices) / small_indices, 0.02f, 1.0f);
        return keep >= 1.0f ? small_cards : System.Math.Max(1, (int)(small_cards * keep));
    }

    private static void AssertValid(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        Assert.Equal(0, i.Count % 3);
        Assert.All(i, x => Assert.True(x < v.Count));
        Assert.Equal(v.Count, i.Distinct().Count()); // vertex fetch compacted away the dropped cards
    }

    private static HashSet<Vector3> CardCentres(List<RHI_Vertex_PosTexNorTan> v, List<uint> i)
    {
        var set = new HashSet<Vector3>();
        for (int t = 0; t < i.Count; t += 6) // cards stay contiguous: 2 triangles each, in original order
        {
            Vector3 s = Vector3.Zero;
            foreach (uint x in i.Skip(t).Take(6).Distinct()) s += v[(int)x].pos;
            set.Add(new Vector3(MathF.Round(s.X / 4, 3), MathF.Round(s.Y / 4, 3), MathF.Round(s.Z / 4, 3)));
        }
        return set;
    }

    [Fact]
    public void Thins_WholeCards_ToTarget_AndScalesSurvivors()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddCards(v, i, 200);

        GeometryProcessing.ThinFoliageCards(i, v, 600);

        int kept = ExpectedKept(200, 1200, 0, 600);
        Assert.Equal(100, kept);
        Assert.Equal(kept * 6, i.Count);
        Assert.Equal(kept * 4, v.Count);
        AssertValid(v, i);

        // survivors grow about their centre by clamp(0.92 / sqrt(keep), 1, 1.6)
        float scale = System.Math.Clamp(0.92f / MathF.Sqrt(0.5f), 1.0f, 1.6f);
        float width = MathF.Abs(v[(int)i[2]].pos.X - v[(int)i[1]].pos.X);
        Assert.Equal(scale, width, 4);
    }

    [Fact]
    public void LargeConnectedPieces_PassThrough_Untouched()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddSheet(v, i, 10, new Vector3(100, 0, 0)); // 200 triangles = 600 indices, one piece
        var sheet = v.Select(x => x.pos).ToHashSet();
        AddCards(v, i, 100);                        // 600 indices of cards

        GeometryProcessing.ThinFoliageCards(i, v, 900);

        int kept = ExpectedKept(100, 600, 600, 900);
        Assert.Equal(600 + kept * 6, i.Count);
        AssertValid(v, i);
        Assert.Subset(v.Select(x => x.pos).ToHashSet(), sheet); // sheet vertices present and unscaled
    }

    [Fact]
    public void NonFoliage_OnlyLargePieces_IsNoOp()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddSheet(v, i, 12, Vector3.Zero);
        var v0 = v.ToList();
        var i0 = i.ToList();

        GeometryProcessing.ThinFoliageCards(i, v, 64);

        Assert.Equal(i0, i);
        Assert.Equal(v0, v);
    }

    [Fact]
    public void AtOrAboveTarget_OrTiny_IsNoOp()
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddCards(v, i, 10);
        var i0 = i.ToList();
        GeometryProcessing.ThinFoliageCards(i, v, i.Count);
        Assert.Equal(i0, i);

        var v2 = new List<RHI_Vertex_PosTexNorTan>();
        var i2 = new List<uint>();
        AddCards(v2, i2, 1); // 6 indices < 12
        GeometryProcessing.ThinFoliageCards(i2, v2, 3);
        Assert.Equal(6, i2.Count);
    }

    [Fact]
    public void IsDeterministic()
    {
        var va = new List<RHI_Vertex_PosTexNorTan>(); var ia = new List<uint>();
        var vb = new List<RHI_Vertex_PosTexNorTan>(); var ib = new List<uint>();
        AddCards(va, ia, 300);
        AddCards(vb, ib, 300);

        GeometryProcessing.ThinFoliageCards(ia, va, 500);
        GeometryProcessing.ThinFoliageCards(ib, vb, 500);

        Assert.Equal(ia, ib);
        Assert.Equal(va, vb);
    }

    [Fact]
    public void Lods_KeepNestedSubsets()
    {
        var v1 = new List<RHI_Vertex_PosTexNorTan>(); var i1 = new List<uint>();
        AddCards(v1, i1, 200);
        GeometryProcessing.ThinFoliageCards(i1, v1, 600);
        var v2 = v1.ToList(); var i2 = i1.ToList();
        GeometryProcessing.ThinFoliageCards(i2, v2, 300);

        Assert.Subset(CardCentres(v1, i1), CardCentres(v2, i2));
        Assert.True(i2.Count < i1.Count);
    }

    [Fact]
    public void Mesh_FoliageLods_FollowSpartanTargets()
    {
        using var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessGenerateLods);
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        AddCards(v, i, 200); // 1200 indices

        int sub = mesh.AddGeometry(v, i, generate_lods: true, foliage_cards: true);

        // targets are relative to lod0: 1200 * {0.5, 0.24, 0.12, 0.06}, each lod thins the previous one
        float[] ratio = { 0.025f / 0.05f, 0.012f / 0.05f, 0.006f / 0.05f, 0.003f / 0.05f };
        var expected = new List<int> { 1200 };
        int cards = 200;
        foreach (float r in ratio)
        {
            int prev = cards * 6;
            if (prev <= 64) break;
            int target = System.Math.Max(64, (int)(1200 * r));
            int next = ExpectedKept(cards, prev, 0, target);
            if (next * 6 >= (int)(prev * Mesh.MeshLodMinReduction)) break;
            cards = next;
            expected.Add(cards * 6);
        }

        Assert.Equal(expected.Count, mesh.GetLodCount(sub));
        var li = new List<uint>();
        var lv = new List<RHI_Vertex_PosTexNorTan>();
        for (int l = 0; l < expected.Count; l++)
        {
            Assert.True(mesh.GetGeometryLod(sub, l, li, lv));
            Assert.Equal(expected[l], li.Count);
            AssertValid(lv, li);
        }
    }
}
