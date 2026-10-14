using System.Collections.Generic;
using System.Numerics;
using Phalanx.Geometry;
using Phalanx.Math;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// a two-lod IMesh, standing in for Phalanx.Rendering's Mesh
internal sealed class FakeMesh : IMesh
{
    public readonly MeshLod[] lods =
    {
        new MeshLod { vertex_offset = 0, vertex_count = 24, index_offset = 0,  index_count = 36, aabb = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)) },
        new MeshLod { vertex_offset = 24, vertex_count = 8, index_offset = 36, index_count = 12, aabb = new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)) },
    };
    public string GetObjectName() => "fake";
    public MeshType GetMeshType() => MeshType.Cube;
    public int GetSubMeshCount() => 1;
    public int GetLodCount(int sub_mesh_index) => lods.Length;
    public MeshLod GetLod(int sub_mesh_index, int lod_index) => lods[lod_index];
    public uint GetGlobalVertexOffset() => 100;
    public uint GetGlobalIndexOffset() => 1000;
    public void GetGeometry(int sub_mesh_index, List<uint>? indices, List<RHI_Vertex_PosTexNorTan>? vertices) { indices?.Add(7); }
}

[Collection("World")]
public class RenderComponentTests
{
    [Fact]
    public void Lod_offsets_add_the_mesh_global_offsets()
    {
        var render = new Render(new Entity());
        render.SetMesh(new FakeMesh());
        Assert.Equal(2, render.GetLodCount());
        Assert.Equal(1036u, render.GetIndexOffset(1));
        Assert.Equal(12, render.GetIndexCount(1));
        Assert.Equal(124u, render.GetVertexOffset(1));
        Assert.Equal(36, render.GetIndexCount());
        Assert.Equal(0, render.GetIndexCount(5)); // out of range lod
        var indices = new List<uint>();
        render.GetGeometry(indices, new List<RHI_Vertex_PosTexNorTan>());
        Assert.Equal(new uint[] { 7 }, indices);
    }

    [Fact]
    public void Bounding_box_follows_the_entity_transform()
    {
        var entity = new Entity();
        entity.SetPosition(new Vector3(10, 0, 0));
        entity.SetScale(new Vector3(2, 1, 1));
        var render = new Render(entity);
        render.SetMesh(new FakeMesh());
        Assert.Equal(new Vector3(9, -0.5f, -0.5f), render.GetBoundingBox().GetMin());
        Assert.Equal(new Vector3(11, 0.5f, 0.5f), render.GetBoundingBox().GetMax());
        entity.SetPosition(new Vector3(0, 5, 0));
        render.Tick();
        Assert.Equal(new Vector3(0, 5, 0), render.GetBoundingBox().GetCenter());
    }

    [Fact]
    public void Default_material_is_deferred_until_the_renderer_publishes_it()
    {
        RenderView.SetStandardMaterial(null);
        var render = new Render(new Entity());
        render.SetDefaultMaterial();
        Assert.Null(render.GetMaterial());
        var standard = new Material();
        RenderView.SetStandardMaterial(standard);
        render.Tick();
        Assert.Same(standard, render.GetMaterial());
        Assert.True(render.IsUsingDefaultMaterial());
        RenderView.SetStandardMaterial(null);
    }

    [Fact]
    public void Set_mesh_by_type_uses_the_published_standard_mesh()
    {
        var mesh = new FakeMesh();
        RenderView.SetStandardMesh(MeshType.Cube, mesh);
        var render = new Render(new Entity());
        render.SetMesh(MeshType.Cube);
        Assert.Same(mesh, render.GetMesh());
        RenderView.SetStandardMesh(MeshType.Cube, null);
    }

    [Fact]
    public void Bounding_box_transform_rotates_extents()
    {
        var box = new BoundingBox(new Vector3(-1, -2, -3), new Vector3(1, 2, 3));
        BoundingBox r = box * Matrix4x4.CreateRotationZ(MathF.PI / 2);
        Assert.Equal(2.0f, r.GetMax().X, 4);
        Assert.Equal(1.0f, r.GetMax().Y, 4);
        Assert.Equal(3.0f, r.GetMax().Z, 4);
    }
}

[Collection("World")]
public class WorldTickTests
{
    [Fact]
    public void Tick_publishes_entities_and_resolves_camera_light_and_renderables()
    {
        World.World.SetMainThread();
        World.World.ClearScene();
        Entity cam = World.World.CreateEntity(); cam.AddComponent<Camera>();
        Entity sun = World.World.CreateEntity(); sun.AddComponent<Light>()!.SetLightType(LightType.Directional);
        Entity box = World.World.CreateEntity(); box.AddComponent<Render>()!.SetMesh(new FakeMesh());
        Assert.Empty(World.World.GetEntities()); // pending until the tick

        World.World.Tick();
        Assert.Equal(3, World.World.GetEntities().Count);
        Assert.Same(cam.GetComponent<Camera>(), World.World.GetCamera());
        Assert.Same(sun, World.World.GetDirectionalLight());
        Assert.Equal(new[] { sun }, World.World.GetEntitiesLights());
        Assert.Equal(new[] { box }, World.World.GetEntitiesRenderables());

        World.World.RemoveEntity(box);
        World.World.Tick();
        Assert.Empty(World.World.GetEntitiesRenderables());
        Assert.Equal(2, World.World.GetEntities().Count);
        World.World.ClearScene();
        Assert.Empty(World.World.GetEntities());
    }
}
