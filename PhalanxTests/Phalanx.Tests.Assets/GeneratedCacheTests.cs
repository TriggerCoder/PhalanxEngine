using System.Numerics;
using Xunit;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Platform;
using Phalanx.Caching;
using Phalanx.Rendering;
using Phalanx.Assets.Import;

namespace Phalanx.Tests;

[CollectionDefinition("GeneratedCache", DisableParallelization = true)]
public sealed class GeneratedCacheCollection { }

// GeneratedCache.Root and its statistics are process wide, so these run alone
[Collection("GeneratedCache")]
public sealed class GeneratedCacheTests : IDisposable
{
    private readonly string? m_previous_root;
    private readonly string m_root;

    public GeneratedCacheTests()
    {
        m_previous_root = GeneratedCache.Root;
        m_root = Path.Combine(Path.GetTempPath(), "phalanx-cache-" + Guid.NewGuid().ToString("N"));
        GeneratedCache.Root = m_root;
        GeneratedCache.ResetStatistics();
    }

    public void Dispose()
    {
        GeneratedCache.Root = m_previous_root;
        try { Directory.Delete(m_root, true); } catch (IOException) { }
    }

    private static RHI_Vertex_PosTexNorTan V(Vector3 p, Vector2 uv) => new(p, uv, Vector3.UnitZ, Vector3.UnitX);

    private static (List<RHI_Vertex_PosTexNorTan>, List<uint>) Grid(int n, float bump = 0)
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                v.Add(V(new Vector3(x, MathF.Sin(x * 0.7f) * MathF.Cos(y * 0.5f) + bump, y), new Vector2(x / (float)n, y / (float)n)));
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                uint a = (uint)(y * (n + 1) + x), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
                i.AddRange(new[] { a, c, b, b, c, d });
            }
        return (v, i);
    }

    private static (List<RHI_Vertex_PosTexNorTan>, List<uint>) Crown(int cards)
    {
        var v = new List<RHI_Vertex_PosTexNorTan>();
        var i = new List<uint>();
        var rng = new Random(3);
        for (int c = 0; c < cards; c++)
        {
            var o = new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 2;
            uint b = (uint)v.Count;
            v.Add(V(o + new Vector3(-0.1f, -0.1f, 0), new Vector2(0, 0)));
            v.Add(V(o + new Vector3( 0.1f, -0.1f, 0), new Vector2(1, 0)));
            v.Add(V(o + new Vector3(-0.1f,  0.1f, 0), new Vector2(0, 1)));
            v.Add(V(o + new Vector3( 0.1f,  0.1f, 0), new Vector2(1, 1)));
            i.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }
        return (v, i);
    }

    private static Mesh Build(List<RHI_Vertex_PosTexNorTan> v, List<uint> i, bool foliage = false, bool skip_cache = false)
    {
        var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessGenerateLods);
        if (skip_cache) mesh.SetFlag(MeshFlags.PostProcessSkipCache);
        mesh.AddGeometry(new List<RHI_Vertex_PosTexNorTan>(v), new List<uint>(i), generate_lods: true, foliage_cards: foliage, impostor: foliage);
        return mesh;
    }

    private static List<(List<uint>, List<RHI_Vertex_PosTexNorTan>)> Lods(Mesh m)
    {
        var r = new List<(List<uint>, List<RHI_Vertex_PosTexNorTan>)>();
        for (int l = 0; l < m.GetLodCount(0); l++)
        {
            var li = new List<uint>(); var lv = new List<RHI_Vertex_PosTexNorTan>();
            Assert.True(m.GetGeometryLod(0, l, li, lv));
            r.Add((li, lv));
        }
        return r;
    }

    private static void AssertSameLods(Mesh a, Mesh b)
    {
        var la = Lods(a); var lb = Lods(b);
        Assert.Equal(la.Count, lb.Count);
        for (int l = 0; l < la.Count; l++)
        {
            Assert.Equal(la[l].Item1, lb[l].Item1);
            Assert.Equal(la[l].Item2, lb[l].Item2);
        }
    }

    private string[] Files(string category)
    {
        string dir = Path.Combine(m_root, "generated_cache", category);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bin") : Array.Empty<string>();
    }

    // format ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Hash_IsFnv1a64()
    {
        var h = new GeneratedCache.Hash();
        Assert.Equal(14695981039346656037ul, h.value);
        h.Bytes("a"u8);
        Assert.Equal(0xaf63dc4c8601ec8cul, h.value); // FNV-1a 64 test vector
    }

    [Fact]
    public void Path_IsCategoryAndDecimalKey()
    {
        Assert.Equal(Path.Combine(m_root, "generated_cache", "lods", "18446744073709551615.bin"), GeneratedCache.Path("lods", ulong.MaxValue));
        GeneratedCache.Root = null;
        Assert.Null(GeneratedCache.Path("lods", 1));
        Assert.False(GeneratedCache.Load<uint>(null, 1, new List<uint>()));
        GeneratedCache.Save<uint>(null, 1, new uint[] { 1 }); // no-op
    }

    [Fact]
    public void Default_Root_IsLocalAppData()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), GeneratedCache.DefaultRoot());
    }

    [Fact]
    public void Header_MatchesSpartanLayout()
    {
        string path = GeneratedCache.Path("t", 42)!;
        GeneratedCache.Save<uint>(path, 42, new uint[] { 7, 8, 9 });
        byte[] f = File.ReadAllBytes(path);
        Assert.Equal(40 + 8 + 12, f.Length);
        Assert.Equal(42ul, BitConverter.ToUInt64(f, 0));
        Assert.Equal(GeneratedCache.HashBytes(f.AsSpan(40)), BitConverter.ToUInt64(f, 8));
        Assert.Equal(0x3145484341434850ul, BitConverter.ToUInt64(f, 16));
        Assert.Equal(20ul, BitConverter.ToUInt64(f, 24));
        Assert.Equal(2u, BitConverter.ToUInt32(f, 32));
        Assert.Equal(0u, BitConverter.ToUInt32(f, 36));
        Assert.Equal(3ul, BitConverter.ToUInt64(f, 40));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RoundTrip_TwoVectors_AndWrongKeyMisses()
    {
        string path = GeneratedCache.Path("t", 5)!;
        var verts = new[] { V(Vector3.One, Vector2.UnitX), V(Vector3.UnitY, Vector2.UnitY) };
        GeneratedCache.Save<RHI_Vertex_PosTexNorTan, uint>(path, 5, verts, new uint[] { 0, 1, 1 });
        var a = new List<RHI_Vertex_PosTexNorTan>(); var b = new List<uint>();
        Assert.True(GeneratedCache.Load(path, 5, a, b));
        Assert.Equal(verts, a);
        Assert.Equal(new uint[] { 0, 1, 1 }, b);
        Assert.False(GeneratedCache.Load(path, 6, new List<RHI_Vertex_PosTexNorTan>(), new List<uint>()));
        Assert.False(GeneratedCache.Load(GeneratedCache.Path("t", 99), 99, a, b));
    }

    [Fact]
    public void Corrupted_Truncated_Garbage_AreMisses_AndLeaveOutputsUntouched()
    {
        string path = GeneratedCache.Path("t", 11)!;
        GeneratedCache.Save<uint>(path, 11, Enumerable.Range(0, 100).Select(x => (uint)x).ToArray());
        byte[] good = File.ReadAllBytes(path);

        var sentinel = new List<uint> { 123 };
        byte[] flipped = (byte[])good.Clone(); flipped[^5] ^= 0xFF;
        File.WriteAllBytes(path, flipped);
        Assert.False(GeneratedCache.Load(path, 11, sentinel));
        Assert.Equal(new uint[] { 123 }, sentinel);

        foreach (int len in new[] { 0, 3, 39, 40, 48, good.Length - 1 })
        {
            File.WriteAllBytes(path, good.AsSpan(0, len).ToArray());
            Assert.False(GeneratedCache.Load(path, 11, sentinel));
        }
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xAB, 300).ToArray());
        Assert.False(GeneratedCache.Load(path, 11, sentinel));
        Assert.Equal(new uint[] { 123 }, sentinel);

        // a consistent header but a payload with a lying count
        byte[] liar = (byte[])good.Clone();
        BitConverter.GetBytes(1000ul).CopyTo(liar, 40);
        BitConverter.GetBytes(GeneratedCache.HashBytes(liar.AsSpan(40))).CopyTo(liar, 8);
        File.WriteAllBytes(path, liar);
        Assert.False(GeneratedCache.Load(path, 11, sentinel));

        Assert.True(GeneratedCache.GetStatistics("t").invalid >= 8);

        // rewriting heals it
        File.WriteAllBytes(path, good);
        Assert.True(GeneratedCache.Load(path, 11, sentinel));
        Assert.Equal(100, sentinel.Count);
    }

    // mesh lods ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Lods_MissThenHit_RoundTripsExactly()
    {
        var (v, i) = Grid(40);
        using var first = Build(v, i);
        Assert.True(first.GetLodCount(0) > 1);
        var s1 = GeneratedCache.GetStatistics("lods");
        Assert.Equal(0ul, (ulong)s1.hits);
        Assert.True(s1.missing > 0);
        Assert.Equal((int)s1.missing, Files("lods").Length);

        using var second = Build(v, i);
        var s2 = GeneratedCache.GetStatistics("lods");
        Assert.Equal(s1.missing, s2.missing);
        Assert.Equal((long)s1.missing, (long)s2.hits);
        AssertSameLods(first, second);

        using var uncached = Build(v, i, skip_cache: true);
        AssertSameLods(first, uncached);
    }

    [Fact]
    public void Lods_InvalidateOnInputAndFlagChange()
    {
        var (v, i) = Grid(40);
        using (Build(v, i)) { }
        int files = Files("lods").Length;
        var s = GeneratedCache.GetStatistics("lods");

        var (v2, i2) = Grid(40, bump: 0.25f); // different input geometry
        using (Build(v2, i2)) { }
        Assert.Equal(s.hits, GeneratedCache.GetStatistics("lods").hits);
        Assert.True(Files("lods").Length > files);
        files = Files("lods").Length;

        using (Build(v, i, foliage: true)) { } // same geometry, foliage card policy
        Assert.Equal(s.hits, GeneratedCache.GetStatistics("lods").hits);
        Assert.True(Files("lods").Length > files);
    }

    [Fact]
    public void Lods_SkipCacheFlag_NeitherReadsNorWrites()
    {
        var (v, i) = Grid(30);
        using (Build(v, i, skip_cache: true)) { }
        Assert.Empty(Files("lods"));
        Assert.Equal(0, (int)GeneratedCache.GetStatistics("lods").missing);
    }

    [Fact]
    public void Lods_CorruptedFile_Rebuilds()
    {
        var (v, i) = Grid(40);
        using var first = Build(v, i);
        foreach (string f in Files("lods"))
        {
            byte[] b = File.ReadAllBytes(f);
            File.WriteAllBytes(f, b.AsSpan(0, b.Length / 2).ToArray());
        }
        using var second = Build(v, i);
        Assert.True(GeneratedCache.GetStatistics("lods").invalid > 0);
        AssertSameLods(first, second);
        using var third = Build(v, i); // rewritten files are good again
        Assert.True(GeneratedCache.GetStatistics("lods").hits > 0);
    }

    // impostors ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Impostor_MissThenHit_RoundTripsAtlas_AndInvalidates()
    {
        var (v, i) = Crown(150);
        using var first = Build(v, i, foliage: true);
        Assert.Single(Files("impostors"));
        Assert.Equal(1, (int)GeneratedCache.GetStatistics("impostors").missing);

        using var second = Build(v, i, foliage: true);
        Assert.Equal(1, (int)GeneratedCache.GetStatistics("impostors").hits);
        Assert.Equal(first.GetImpostor(0)!.texels, second.GetImpostor(0)!.texels);

        // wrong sized (but valid) payload is rejected and rebaked
        string f = Files("impostors")[0];
        ulong key = ulong.Parse(Path.GetFileNameWithoutExtension(f));
        GeneratedCache.Save<uint>(f, key, new uint[] { 1, 2, 3 });
        using var third = Build(v, i, foliage: true);
        Assert.Equal(first.GetImpostor(0)!.texels, third.GetImpostor(0)!.texels);

        // garbage file
        File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        using var fourth = Build(v, i, foliage: true);
        Assert.Equal(first.GetImpostor(0)!.texels, fourth.GetImpostor(0)!.texels);

        // different crown -> new key
        var (v2, i2) = Crown(151);
        using (Build(v2, i2, foliage: true)) { }
        Assert.Equal(2, Files("impostors").Length);
    }

    [Fact]
    public void Maintain_EvictsOldestOutsideSession()
    {
        string a = GeneratedCache.Path("t", 1)!, b = GeneratedCache.Path("t", 2)!;
        GeneratedCache.Save<uint>(a, 1, new uint[1000]);
        GeneratedCache.Save<uint>(b, 2, new uint[1000]);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-2));
        GeneratedCache.Maintain(5000);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    // importer naming ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tree_foliage", true)]
    [InlineData("Oak_foliage_01", true)]
    [InlineData("_foliage", true)]
    [InlineData("tree_Foliage", false)] // case sensitive, like Spartan's find
    [InlineData("foliage", false)]
    [InlineData("tree-foliage", false)]
    [InlineData("bark", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FoliageName(string? name, bool expected) => Assert.Equal(expected, ModelImporter.IsFoliageMaterialName(name));

    [Theory]
    [InlineData("Glass_Window", true)]
    [InlineData("BOTTLE", true)]
    [InlineData("semiTransparent", true)]
    [InlineData("metal", false)]
    public void TransparentName(string name, bool expected) => Assert.Equal(expected, ModelImporter.IsTransparentMaterialName(name));

    [Fact]
    public void FoliageFlags_PlantPropagatesToWholeScene()
    {
        var names = new[] { "bark", "leaves_foliage", "branch" };
        Assert.Equal((true, true), ModelImporter.GetFoliageFlags(names, 1));
        Assert.Equal((false, true), ModelImporter.GetFoliageFlags(names, 0));
        Assert.Equal((false, true), ModelImporter.GetFoliageFlags(names, 7));
        Assert.Equal((false, false), ModelImporter.GetFoliageFlags(new[] { "bark", "rock" }, 0));
    }

    [Fact]
    public void FoliageMaterial_GetsSpartanProperties()
    {
        var m = new Material();
        ModelImporter.ApplyMaterialNameRules(m, "leaf_foliage");
        Assert.Equal(1.0f, m.GetProperty(MaterialProperty.IsFoliage));
        Assert.Equal(0.35f, m.GetProperty(MaterialProperty.SubsurfaceScattering));
        Assert.Equal((float)RHI_CullMode.None, m.GetProperty(MaterialProperty.CullMode));

        var plain = new Material();
        float cull = plain.GetProperty(MaterialProperty.CullMode);
        ModelImporter.ApplyMaterialNameRules(plain, "bark");
        Assert.Equal(0.0f, plain.GetProperty(MaterialProperty.IsFoliage));
        Assert.Equal(cull, plain.GetProperty(MaterialProperty.CullMode));
    }

    [Fact]
    public void ImporterCallSite_SetsFoliageAndImpostorPaths()
    {
        var (v, i) = Crown(150);
        var (bv, bi) = Grid(10);
        var names = new[] { "bark", "leaves_foliage" };
        using var mesh = new Mesh();
        mesh.SetFlag(MeshFlags.PostProcessGenerateLods);
        ModelImporter.AddMeshGeometry(mesh, bv, bi, 1, names, 0); // trunk: plant, not cards
        ModelImporter.AddMeshGeometry(mesh, v, i, 0, names, 1);   // crown
        Assert.NotNull(mesh.GetImpostor(0));
        Assert.NotNull(mesh.GetImpostor(1));
    }
}
