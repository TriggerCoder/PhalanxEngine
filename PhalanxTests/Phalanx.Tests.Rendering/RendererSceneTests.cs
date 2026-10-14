using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using RHI_Texture = Phalanx.Rendering.RHI.RHI_Texture;
using Xunit;

namespace Phalanx.Tests;

// lavapipe, validation on: camera, geometry buffer, g-buffer, light, composition and tonemap end to end
[Collection("gpu")]
public partial class RendererSceneTests
{
    private const uint Width = 128, Height = 96;

    private static void AssertClean() =>
        Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                    string.Join("\n", RHI_Device.ValidationMessages));

    private sealed class Scene
    {
        public required Camera camera;
        public required Entity cube;
        public required Material material;
    }

    // a unit cube at the origin, the camera 3 m in front of it looking down +z
    private static Scene CreateScene(Vector4 color)
    {
        Entity camera_entity = World.World.CreateEntity();
        Camera camera = camera_entity.AddComponent<Camera>()!; // the camera constructor places its entity at (0, 3, -5)
        camera_entity.SetPosition(new Vector3(0, 0, -3));
        camera_entity.SetRotation(Quaternion.Identity);

        Entity cube = World.World.CreateEntity();
        cube.SetPosition(Vector3.Zero);

        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, color.X);
        material.SetProperty(MaterialProperty.ColorG, color.Y);
        material.SetProperty(MaterialProperty.ColorB, color.Z);
        material.SetProperty(MaterialProperty.ColorA, color.W);
        material.SetProperty(MaterialProperty.Roughness, 0.5f);
        material.SetProperty(MaterialProperty.Metalness, 0.25f);

        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        render.SetMaterial(material);
        World.World.Tick(); // publish the entities (spartan: the world load commits them before the first frame)
        return new Scene { camera = camera, cube = cube, material = material };
    }

    private static void Run(Action body, Renderer_AntiAliasing_Upsampling aa = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear, uint shadow_atlas = 1024)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Renderer.AntiAliasingUpsampling = aa;
        World.World.SetMainThread();
        World.World.ClearScene();
        Renderer.ShadowAtlasResolution = shadow_atlas;
        Renderer.Initialize(Width, Height, validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            AssertClean();
        }
        finally
        {
            Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
            Renderer.SkyFallbackNits     = new Vector3(1500.0f, 2200.0f, 3500.0f);
            Renderer.AmbientFallbackNits = new Vector3(150.0f, 220.0f, 350.0f);
            Renderer.Tonemapping         = Renderer_Tonemapping.GranTurismo7;
            Renderer.ShadowAtlasResolution = 8192;
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }

    // Engine::Tick order: World::Tick then Renderer::Tick
    private static void Frame(Scene scene)
    {
        World.World.Tick();
        Assert.True(Renderer.Tick());
    }

    private static byte[] Read(Renderer_RenderTarget target)
    {
        RHI_Device.QueueWaitAll();
        return RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(target)!, 0, 0);
    }

    private static float Half(byte[] b, int offset) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(b, offset));
    private static int Index(uint x, uint y) => (int)(y * Width + x);

    [SkippableFact]
    public void Camera_matrices_follow_spartan_reverse_z_and_reach_the_frame_buffer()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            Frame(scene);

            Camera camera = scene.camera;
            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix();
            Assert.NotEqual(Matrix4x4.Identity, view);
            Assert.Equal(new Vector3(0, 0, 3), Vector3.Transform(Vector3.Zero, view)); // the cube 3 m ahead

            // reverse-z: near maps to 1, far to 0; the non reverse-z copy the other way round
            Vector4 Ndc(Matrix4x4 p, float z) { Vector4 c = Vector4.Transform(new Vector4(0, 0, z, 1), p); return c / c.W; }
            Assert.Equal(1.0f, Ndc(projection, camera.GetNearPlane()).Z, 4);
            Assert.Equal(0.0f, Ndc(projection, camera.GetFarPlane()).Z, 4);
            Assert.Equal(0.0f, Ndc(camera.GetProjectionMatrixNonReverseZ(), camera.GetNearPlane()).Z, 4);

            ref readonly FrameBufferData frame = ref Renderer.GetFrameConstantBuffer();
            Assert.Equal(view, frame.view);
            Assert.Equal(projection, frame.projection); // no jitter with aa off
            Assert.Equal(view * projection, frame.view_projection);
            Assert.Equal(frame.view_projection, frame.view_projection_unjittered);
            Assert.Equal(Vector2.Zero, frame.taa_jitter_current);
            Assert.Equal(new Vector3(0, 0, -3), frame.camera_position);
            Assert.Equal(camera.GetNearPlane(), frame.camera_near);
            Assert.Equal(camera.GetFarPlane(), frame.camera_far);

            // the previous matrices are last frame's
            scene.camera.GetEntity()!.SetPosition(new Vector3(0, 0, -4));
            Matrix4x4 vp_before = frame.view_projection;
            Frame(scene);
            Assert.Equal(vp_before, Renderer.GetFrameConstantBuffer().view_projection_previous);
            Assert.NotEqual(vp_before, Renderer.GetFrameConstantBuffer().view_projection);
        });
    }

    [SkippableFact]
    public void Taau_jitters_the_projection_with_spartans_halton_sequence()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            static float halton(uint index, uint b) { float r = 0, f = 1; while (index > 0) { f /= b; r += index % b * f; index /= b; } return r; }

            var seen = new HashSet<Vector2>();
            for (uint i = 1; i <= 17; i++)
            {
                Frame(scene);
                ref readonly FrameBufferData frame = ref Renderer.GetFrameConstantBuffer();
                uint phase = i % 16; // spartan pre-increments its phase, the first frame uses phase 1
                var expected = new Vector2(2.0f * (halton(phase + 1, 2) - 0.5f) / Width, -2.0f * (halton(phase + 1, 3) - 0.5f) / Height);
                Assert.Equal(expected, frame.taa_jitter_current);
                Assert.Equal(scene.camera.GetProjectionMatrix() * Matrix4x4.CreateTranslation(expected.X, expected.Y, 0), frame.projection);
                Assert.Equal(frame.view * scene.camera.GetProjectionMatrix(), frame.view_projection_unjittered);
                if (i <= 16)
                    Assert.True(seen.Add(expected));
            }
            Assert.Equal(16, seen.Count);
        }, Renderer_AntiAliasing_Upsampling.AA_Taau_Upscale_Taau);
    }

    [SkippableFact]
    public void The_gbuffer_holds_the_cubes_depth_albedo_normal_and_material()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(1.0f, 0.5f, 0.0f, 1.0f));
            Frame(scene);
            Frame(scene);

            // depth (d32): the front face at z = -0.5, 2.5 m from the camera; nothing at the corners (reverse-z clear 0)
            byte[] depth = Read(Renderer_RenderTarget.gbuffer_depth);
            float D(uint x, uint y) => BitConverter.ToSingle(depth, Index(x, y) * 4);
            Vector4 clip = Vector4.Transform(new Vector4(0, 0, -0.5f, 1), scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix());
            Assert.Equal(clip.Z / clip.W, D(Width / 2, Height / 2), 4);
            Assert.Equal(0.0f, D(0, 0));
            Assert.Equal(0.0f, D(Width - 1, Height - 1));

            // the cube covers a centred square: find its horizontal extent on the middle row and compare with the projection
            Vector4 edge = Vector4.Transform(new Vector4(0.5f, 0, -0.5f, 1), scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix());
            float expected_half_width = edge.X / edge.W * Width / 2;
            int covered = 0;
            for (uint x = 0; x < Width; x++)
                if (D(x, Height / 2) > 0) covered++;
            Assert.InRange(covered, 2 * expected_half_width - 2, 2 * expected_half_width + 2);

            // albedo (rgba8): the material colour
            byte[] albedo = Read(Renderer_RenderTarget.gbuffer_color);
            int c = Index(Width / 2, Height / 2) * 4;
            Assert.Equal(new byte[] { 255, 128, 0, 255 }, albedo[c..(c + 4)]);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, albedo[0..4]);

            // normal (rgba16f): the front face faces the camera (-z), w carries the packed material index 0
            byte[] normal = Read(Renderer_RenderTarget.gbuffer_normal);
            int n = Index(Width / 2, Height / 2) * 8;
            Assert.Equal(0.0f, Half(normal, n), 3);
            Assert.Equal(0.0f, Half(normal, n + 2), 3);
            Assert.Equal(-1.0f, Half(normal, n + 4), 3);
            Assert.Equal((float)BitConverter.UInt16BitsToHalf(0x0400), Half(normal, n + 6));

            // material (rgba8): roughness, metalness, 0, occlusion
            byte[] material = Read(Renderer_RenderTarget.gbuffer_material);
            Assert.Equal(new byte[] { 128, 64, 0, 255 }, material[c..(c + 4)]);

            // velocity: the scene is static
            byte[] velocity = Read(Renderer_RenderTarget.gbuffer_velocity);
            int v = Index(Width / 2, Height / 2) * 8;
            Assert.Equal(0.0f, Half(velocity, v), 3);
            Assert.Equal(0.0f, Half(velocity, v + 2), 3);
        });
    }

    [SkippableFact]
    public void Side_faces_write_their_own_normals_when_the_cube_turns()
    {
        Run(() =>
        {
            Scene scene = CreateScene(Vector4.One);
            // turned 90 degrees about y another face looks at the camera, its normal rotated with the transform
            scene.cube.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
            Frame(scene);
            byte[] normal = Read(Renderer_RenderTarget.gbuffer_normal);
            int n = Index(Width / 2, Height / 2) * 8;
            var world_normal = new Vector3(Half(normal, n), Half(normal, n + 2), Half(normal, n + 4));
            Assert.True(Vector3.Distance(new Vector3(0, 0, -1), world_normal) < 1e-2f, world_normal.ToString()); // whichever face, it faces the camera
        });
    }

    [SkippableFact]
    public void The_composition_lights_the_albedo_and_the_sky_falls_back_to_a_constant()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.5f, 0.25f, 1.0f, 1.0f));
            Renderer.SkyFallbackNits     = new Vector3(100.0f, 200.0f, 300.0f);
            Renderer.AmbientFallbackNits = new Vector3(683.0f, 683.0f, 683.0f);

            // frame_render is radiometric: the sky is nits / 683 and the ambient stand-in albedo * nits / 683
            Frame(scene);
            Vector4 albedo = Rgba8(Read(Renderer_RenderTarget.gbuffer_color), Width / 2, Height / 2);
            Vector4 center = Rgba16(Read(Renderer_RenderTarget.frame_render), Width / 2, Height / 2);
            Vector4 corner = Rgba16(Read(Renderer_RenderTarget.frame_render), 0, 0);
            NearRel(new Vector3(albedo.X, albedo.Y, albedo.Z), new Vector3(center.X, center.Y, center.Z), 0.01f);
            NearRel(new Vector3(100, 200, 300) / 683.0f, new Vector3(corner.X, corner.Y, corner.Z), 0.01f);
            Assert.Equal(0.0f, corner.W); // spartan's sky alpha

            // a sun adds its light: diffuse demodulated (times the albedo here) plus specular
            Renderer.AmbientFallbackNits = Vector3.Zero;
            Entity light_entity = World.World.CreateEntity();
            light_entity.SetPosition(new Vector3(0, 5, 0)); // forward is +z, into the front face
            Light light = light_entity.AddComponent<Light>()!;
            light.SetLightType(LightType.Directional);
            Frame(scene);
            Assert.Equal(0u, light.GetIndex());
            Assert.Equal(1u, Renderer.GetLightCount());
            Vector3 diffuse  = LightTarget(Renderer_RenderTarget.light_diffuse,  Width / 2, Height / 2);
            Vector3 specular = LightTarget(Renderer_RenderTarget.light_specular, Width / 2, Height / 2);
            Assert.True(diffuse.X > 1.0f, diffuse.ToString()); // 120000 lux / 683 * brdf
            center = Rgba16(Read(Renderer_RenderTarget.frame_render), Width / 2, Height / 2);
            NearRel(diffuse * new Vector3(albedo.X, albedo.Y, albedo.Z) + specular, new Vector3(center.X, center.Y, center.Z), 0.02f);

            // the light parameters table carries the light, radiometric with its effective colour
            byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.LightParameters)!, 0, (ulong)Marshal.SizeOf<LightParameters>());
            LightParameters uploaded = MemoryMarshal.Read<LightParameters>(table);
            Assert.Equal(1u, uploaded.flags & 1u);
            Assert.Equal(light_entity.GetForward(), uploaded.direction);
            Assert.Equal(light.GetIntensityRadiometric(), uploaded.intensity);

            // and the material table the material
            byte[] materials = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.MaterialParameters)!, 0, (ulong)Marshal.SizeOf<MaterialParameters>());
            MaterialParameters m = MemoryMarshal.Read<MaterialParameters>(materials);
            Assert.Equal(new Vector4(0.5f, 0.25f, 1.0f, 1.0f), m.color);
            Assert.Equal(0.5f, m.roughness);
        });
    }

    [SkippableFact]
    public void The_renderer_draws_the_worlds_render_entities_and_drops_removed_ones()
    {
        Run(() =>
        {
            Scene scene = CreateScene(new Vector4(0.0f, 1.0f, 0.0f, 1.0f));
            Frame(scene);
            byte[] depth = Read(Renderer_RenderTarget.gbuffer_depth);
            Assert.True(BitConverter.ToSingle(depth, Index(Width / 2, Height / 2) * 4) > 0);

            // a second cube, created through the world, to the left: drawn once the world published it
            Entity left = World.World.CreateEntity();
            left.SetPosition(new Vector3(-1.5f, 0, 0));
            left.AddComponent<Render>()!.SetMesh(MeshType.Cube);
            Frame(scene); // the world publishes it at the end of its tick, the renderer draws it this frame
            Vector4 clip = Vector4.Transform(new Vector4(-1.5f, 0, -0.5f, 1), scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix());
            uint x_left = (uint)((clip.X / clip.W * 0.5f + 0.5f) * Width);
            depth = Read(Renderer_RenderTarget.gbuffer_depth);
            Assert.True(BitConverter.ToSingle(depth, Index(x_left, Height / 2) * 4) > 0);

            // no material: the renderer's standard material, white with the checkerboard (srgb decoded greys)
            byte[] albedo = Read(Renderer_RenderTarget.gbuffer_color);
            int c = Index(x_left, Height / 2) * 4;
            Assert.Contains(albedo[c], new byte[] { 13, 33 });
            Assert.Equal(albedo[c], albedo[c + 1]);
            Assert.Equal(albedo[c], albedo[c + 2]);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, albedo[(Index(Width / 2, Height / 2) * 4)..(Index(Width / 2, Height / 2) * 4 + 4)]);

            // removed: gone from the next frame
            World.World.RemoveEntity(scene.cube);
            Frame(scene);
            depth = Read(Renderer_RenderTarget.gbuffer_depth);
            Assert.Equal(0.0f, BitConverter.ToSingle(depth, Index(Width / 2, Height / 2) * 4));
            Assert.Single(World.World.GetEntitiesRenderables());
        });
    }

    [SkippableFact]
    public void The_gbuffer_shades_exactly_the_prepass_survivors_with_the_equal_depth_test()
    {
        Run(() =>
        {
            // a red cube in front, a blue one behind it and offset to the right: they overlap on screen
            Scene scene = CreateScene(new Vector4(1, 0, 0, 1));
            Entity back = World.World.CreateEntity();
            back.SetPosition(new Vector3(0.6f, 0.3f, 1.0f));
            var blue = new Material();
            blue.SetProperty(MaterialProperty.ColorB, 1.0f);
            blue.SetProperty(MaterialProperty.ColorA, 1.0f);
            Render render = back.AddComponent<Render>()!;
            render.SetMesh(MeshType.Cube);
            render.SetMaterial(blue);
            Frame(scene);
            Frame(scene);

            byte[] depth  = Read(Renderer_RenderTarget.gbuffer_depth);
            byte[] albedo = Read(Renderer_RenderTarget.gbuffer_color);
            int covered = 0, shaded = 0, blue_pixels = 0;
            for (uint y = 0; y < Height; y++)
                for (uint x = 0; x < Width; x++)
                {
                    int i = Index(x, y);
                    bool has_depth = BitConverter.ToSingle(depth, i * 4) > 0;
                    bool has_color = albedo[i * 4 + 3] > 0;
                    covered += has_depth ? 1 : 0;
                    shaded  += has_depth && has_color ? 1 : 0;
                    blue_pixels += albedo[i * 4 + 2] == 255 ? 1 : 0;
                    Assert.False(!has_depth && has_color, $"colour without depth at {x},{y}");
                }
            Assert.Equal(covered, shaded); // the equal test drops nothing the prepass kept (bit identical depth)
            Assert.True(blue_pixels > 0);  // the back cube shows where the front one doesn't cover it

            // the centre belongs to the front cube: red, at the front face's depth
            int c = Index(Width / 2, Height / 2);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, albedo[(c * 4)..(c * 4 + 4)]);
            Vector4 clip = Vector4.Transform(new Vector4(0, 0, -0.5f, 1), scene.camera.GetViewMatrix() * scene.camera.GetProjectionMatrix());
            Assert.Equal(clip.Z / clip.W, BitConverter.ToSingle(depth, c * 4), 4);
        });
    }

    [SkippableFact]
    public void Geometry_buffer_appends_grow_and_keep_the_committed_range()
    {
        Run(() =>
        {
            // the standard cube then the standard quad are already in
            const uint standard_vertices = 24u + 4u, standard_indices = 36u + 6u;
            Assert.Equal(standard_vertices, GeometryBuffer.GetVertexCount());
            Assert.Equal(standard_indices, GeometryBuffer.GetIndexCount());
            Assert.True(Renderer.Tick());
            RHI_Buffer first = GeometryBuffer.GetVertexBuffer()!;
            Assert.Equal(GeometryBuffer.add_headroom(standard_vertices, 24), first.GetElementCount()); // spartan's 25% headroom

            // the identity instance sits in slot 0 of the instance buffer
            byte[] instance = RHI_Device.ReadbackBuffer(GeometryBuffer.GetInstanceBuffer()!, 0, 32);
            Assert.Equal(PackedInstance.GetIdentity(), MemoryMarshal.Read<PackedInstance>(instance));
            Assert.Equal(Matrix4x4.Identity, PackedInstance.GetIdentity().GetMatrix());

            // enough vertices to grow: the base offset follows the committed ones, the old contents move over gpu side
            var more = new RHI_Vertex_PosTexNorTan[100];
            for (int i = 0; i < more.Length; i++)
                more[i] = new RHI_Vertex_PosTexNorTan(new Vector3(i, 0, 0), Vector2.Zero);
            Assert.Equal(standard_vertices, GeometryBuffer.AppendVertices(more));
            Assert.Equal(standard_indices, GeometryBuffer.AppendIndices(new uint[] { 0, 1, 2 }));
            Assert.True(Renderer.Tick());
            RHI_Buffer grown = GeometryBuffer.GetVertexBuffer()!;
            Assert.NotSame(first, grown);
            RHI_Device.QueueWaitAll();

            byte[] bytes = RHI_Device.ReadbackBuffer(grown, 0, (int)((standard_vertices + 100) * 24));
            var gpu = MemoryMarshal.Cast<byte, RHI_Vertex_PosTexNorTan>(bytes);
            var cube_v = new List<RHI_Vertex_PosTexNorTan>(); var cube_i = new List<uint>();
            GeometryGeneration.generate_cube(cube_v, cube_i);
            for (int i = 0; i < 24; i++)
                Assert.Equal(cube_v[i], gpu[i]);
            for (int i = 0; i < 100; i++)
                Assert.Equal(new Vector3(i, 0, 0), gpu[(int)standard_vertices + i].pos);
        });
    }

    [SkippableFact]
    public void Packed_instances_round_trip_a_transform()
    {
        var m = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f) * Matrix4x4.CreateTranslation(5, 6, 7);
        var instance = new PackedInstance();
        instance.SetMatrix(m);
        Matrix4x4 back = instance.GetMatrix();
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(m[r, c] - back[r, c]) < 1e-3f, $"[{r},{c}] {m[r, c]} vs {back[r, c]}"); // snorm16 rotation
    }
}
