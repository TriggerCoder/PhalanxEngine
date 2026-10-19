using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Geometry;
using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Phalanx.World;
using Xunit;
using Failure = Phalanx.Rendering.Renderer.MaterialFailure;

namespace Phalanx.Tests;

// the default and illegal materials (Renderer.MaterialFallback.cs) on lavapipe, validation on
[Collection("gpu")]
public class RendererMaterialFallbackTests
{
    private const uint Width = 64, Height = 64;

    private static void Run(Action body)
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Renderer.AntiAliasingUpsampling = Renderer_AntiAliasing_Upsampling.AA_Off_Upscale_Linear;
        World.World.SetMainThread();
        World.World.ClearScene();
        Renderer.ShadowAtlasResolution = 1024;
        Renderer.Initialize(Width, Height, validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal) || m.Contains("WarningBit", StringComparison.Ordinal)),
                        string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            Renderer.ShadowAtlasResolution = 8192;
            Renderer.Shutdown();
            World.World.ClearScene();
        }
    }

    private static Material NewMaterial(string name, float r = 1.0f, float g = 1.0f, float b = 1.0f)
    {
        var material = new Material();
        material.SetResourceName(name);
        material.SetProperty(MaterialProperty.ColorR, r);
        material.SetProperty(MaterialProperty.ColorG, g);
        material.SetProperty(MaterialProperty.ColorB, b);
        material.SetProperty(MaterialProperty.ColorA, 1.0f);
        return material;
    }

    // a camera 1.5 m behind the origin looking down +z, a cube at the origin whose front face is the middle 32 pixels
    // (two pixels a texel for the illegal texture, its mip 0)
    private static Render CreateScene(Material? material)
    {
        Entity camera_entity = World.World.CreateEntity();
        camera_entity.AddComponent<Camera>();
        camera_entity.SetPosition(new Vector3(0, 0, -1.5f));
        return AddCube(Vector3.Zero, material);
    }

    private static Render AddCube(Vector3 position, Material? material)
    {
        Entity cube = World.World.CreateEntity();
        cube.SetPosition(position);
        Render render = cube.AddComponent<Render>()!;
        render.SetMesh(MeshType.Cube);
        if (material != null)
            render.SetMaterial(material);
        World.World.Tick();
        return render;
    }

    private static void Frame()
    {
        World.World.Tick();
        Assert.True(Renderer.Tick());
    }

    private static uint DrawMaterial(Render render) => Renderer.FrameDrawCalls.Single(d => d.render == render).material_index;
    private static uint ViewDrawMaterial(Render render) =>
        Renderer.MainView.State.DrawCalls.Concat(Renderer.MainView.State.DrawCallsAlpha).Single(d => d.render == render).material_index;

    private static MaterialParameters TableEntry(uint index)
    {
        ulong size = (ulong)Marshal.SizeOf<MaterialParameters>();
        byte[] table = RHI_Device.ReadbackBuffer(Renderer.GetBuffer(Renderer_Buffer.MaterialParameters)!, 0, (index + 1) * size);
        return MemoryMarshal.Read<MaterialParameters>(table.AsSpan((int)(index * size)));
    }

    private static byte[] Centre(int half_size)
    {
        RHI_Device.QueueWaitAll();
        byte[] all = RHI_Device.ReadbackTexture(Renderer.GetRenderTargetTexture(Renderer_RenderTarget.gbuffer_color)!, 0, 0);
        var region = new List<byte>();
        for (uint y = Height / 2 - (uint)half_size; y < Height / 2 + half_size; y++)
            for (uint x = Width / 2 - (uint)half_size; x < Width / 2 + half_size; x++)
                region.AddRange(all.AsSpan((int)(y * Width + x) * 4, 4).ToArray());
        return region.ToArray();
    }

    // the warnings logged while body runs that mention text
    private static List<string> Warnings(string text, Action body)
    {
        var lines = new List<string>();
        void Listen(LogCmd cmd)
        {
            if (cmd.type == LogType.Warning && cmd.text.Contains(text, StringComparison.Ordinal))
                lock (lines) lines.Add(cmd.text);
        }
        Log.LogEntry += Listen;
        try { body(); }
        finally { Log.LogEntry -= Listen; }
        return lines;
    }

    // the illegal texture's pixels are black and magenta, point sampling keeps them unfiltered
    private static void AssertIllegalTexels(byte[] pixels)
    {
        bool magenta = false, black = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            var p = (pixels[i], pixels[i + 1], pixels[i + 2]);
            Assert.True(p == (255, 0, 255) || p == (0, 0, 0), $"texel {i / 4} is {p}, not a point sampled illegal texel");
            magenta |= p == (255, 0, 255);
            black   |= p == (0, 0, 0);
        }
        Assert.True(magenta && black);
    }

    [SkippableFact]
    public void The_default_and_illegal_materials_hold_indices_0_and_1_and_are_engine_owned()
    {
        Run(() =>
        {
            Material own = NewMaterial("own");
            Render render = CreateScene(own);
            Frame();
            Assert.Same(Renderer.GetStandardMaterial(), Renderer.GetTableMaterial(Renderer.default_material_index));
            Assert.Same(Renderer.GetIllegalMaterial(), Renderer.GetTableMaterial(Renderer.illegal_material_index));
            Assert.Equal(2u, DrawMaterial(render));
            Assert.True(Renderer.IllegalTextureLoadedFromFile); // data/textures/illegal_material.png is copied to the output

            MaterialParameters illegal = TableEntry(Renderer.illegal_material_index);
            Assert.True(illegal.HasTextureAlbedo());
            Assert.NotEqual(0u, illegal.flags & (1u << Renderer.material_flag_point_sampled));
            Assert.Equal(0u, TableEntry(Renderer.default_material_index).flags & (1u << Renderer.material_flag_point_sampled));
            Assert.Equal(0u, TableEntry(2).flags & (1u << Renderer.material_flag_point_sampled));

            foreach (Material engine in new[] { Renderer.GetStandardMaterial()!, Renderer.GetIllegalMaterial()! })
            {
                Assert.True(engine.IsEngineOwned);
                List<string> warnings = Warnings($"'{engine.GetObjectName()}' is owned by the engine", () =>
                {
                    Assert.False(engine.Destroy());
                    engine.SetProperty(MaterialProperty.ColorR, 0.0f);
                    engine.SetProperty(MaterialProperty.ColorG, 0.0f);
                    engine.SetTexture(MaterialTextureType.Color, null);
                });
                Assert.False(engine.IsDestroyed);
                Assert.Equal(1.0f, engine.GetProperty(MaterialProperty.ColorR));
                Assert.NotNull(engine.GetTexture(MaterialTextureType.Color));
                Assert.Equal(2, warnings.Count); // the destroy, then the edits once
            }
            Frame();
            Assert.Equal(new Vector4(1.0f), TableEntry(Renderer.illegal_material_index).color);
        });
    }

    [SkippableFact]
    public void A_render_without_a_material_draws_the_default_material()
    {
        Run(() =>
        {
            Render render = CreateScene(null);
            Frame();
            Assert.Equal(Renderer.default_material_index, DrawMaterial(render));
            Assert.Equal(Renderer.default_material_index, ViewDrawMaterial(render));
            Assert.Equal(Renderer.default_material_index, Renderer.GetMaterialIndex(null));
        });
    }

    [SkippableFact]
    public void A_destroyed_material_draws_the_point_sampled_illegal_texture_and_logs_once()
    {
        Run(() =>
        {
            Material doomed = NewMaterial("doomed", 0.0f, 1.0f, 0.0f);
            Render render = CreateScene(doomed);
            Frame();
            Assert.Equal(2u, DrawMaterial(render));
            Assert.True(doomed.Destroy());
            List<string> warnings = Warnings("'doomed' is destroyed", () =>
            {
                for (int i = 0; i < 3; i++)
                    Frame();
            });
            Assert.Single(warnings);
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(render));
            Assert.Equal(Renderer.illegal_material_index, ViewDrawMaterial(render));
            Assert.True(Renderer.TryGetMaterialFailure(doomed, out Failure failure));
            Assert.Equal(Failure.Destroyed, failure);
            AssertIllegalTexels(Centre(8));
        });
    }

    [SkippableFact]
    public void A_material_that_fails_to_prepare_draws_the_illegal_material_until_it_is_fixed()
    {
        Run(() =>
        {
            Material broken = NewMaterial("broken");
            broken.SetTexture(MaterialTextureType.Roughness, Renderer.CreateTexture("roughness", 4, 4, new byte[4 * 4 * 4]));
            bool fail = true;
            Material.PrepareFaultForTests = m => fail && ReferenceEquals(m, broken);
            try
            {
                Render render = CreateScene(broken); // Render.SetMaterial prepares it, the failure doesn't escape
                Assert.True(broken.CreationFailed);
                Frame();
                Assert.Equal(Renderer.illegal_material_index, DrawMaterial(render));
                Assert.True(Renderer.TryGetMaterialFailure(broken, out Failure failure));
                Assert.Equal(Failure.CreationFailed, failure);

                // the next edit prepares it again, it draws itself once that works
                fail = false;
                broken.SetTexture(MaterialTextureType.Metalness, Renderer.CreateTexture("metalness", 4, 4, new byte[4 * 4 * 4]));
                Assert.False(broken.CreationFailed);
                Frame();
                Assert.Equal(2u, DrawMaterial(render));
            }
            finally
            {
                Material.PrepareFaultForTests = null;
            }
        });
    }

    [SkippableFact]
    public void A_material_with_a_property_that_is_not_finite_draws_the_illegal_material()
    {
        Run(() =>
        {
            Material nan = NewMaterial("nan");
            Render render = CreateScene(nan);
            nan.SetProperty(MaterialProperty.Roughness, float.NaN);
            Frame();
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(render));
            Assert.True(Renderer.TryGetMaterialFailure(nan, out Failure failure));
            Assert.Equal(Failure.InvalidProperty, failure);
            nan.SetProperty(MaterialProperty.Roughness, 0.5f);
            Frame();
            Assert.Equal(2u, DrawMaterial(render));
        });
    }

    [SkippableFact]
    public void Materials_past_the_table_capacity_draw_the_illegal_material_and_log_once_each()
    {
        Run(() =>
        {
            Render first  = CreateScene(NewMaterial("first"));
            Render second = AddCube(new Vector3(2, 0, 0), NewMaterial("over_a"));
            Render third  = AddCube(new Vector3(-2, 0, 0), NewMaterial("over_b"));
            Renderer.s_material_capacity = 3; // default, illegal, one more
            List<string> warnings = Warnings("doesn't fit the material table", () =>
            {
                for (int i = 0; i < 3; i++)
                    Frame();
            });
            Assert.Equal(2u, DrawMaterial(first));
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(second));
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(third));
            Assert.Equal(2, warnings.Count); // once per material, not per frame
            Assert.Contains(warnings, w => w.Contains("'over_a'"));
            Assert.Contains(warnings, w => w.Contains("'over_b'"));

            // the reserved materials always fit, even when nothing else does
            Renderer.s_material_capacity = 2;
            Frame();
            Assert.Same(Renderer.GetIllegalMaterial(), Renderer.GetTableMaterial(Renderer.illegal_material_index));
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(first));
        });
    }

    [SkippableFact]
    public void A_material_whose_textures_do_not_fit_the_bindless_table_draws_the_illegal_material()
    {
        Run(() =>
        {
            Material textured = NewMaterial("textured");
            textured.SetTexture(MaterialTextureType.Color, Renderer.CreateTexture("albedo", 4, 4, new byte[4 * 4 * 4]));
            Render render = CreateScene(textured);
            Render plain  = AddCube(new Vector3(2, 0, 0), NewMaterial("plain"));
            Renderer.s_texture_capacity = 2; // the checkerboard and the illegal texture
            List<string> warnings = Warnings("bindless texture table", () =>
            {
                for (int i = 0; i < 3; i++)
                    Frame();
            });
            Assert.Single(warnings);
            Assert.Equal(Renderer.illegal_material_index, DrawMaterial(render));
            Assert.True(Renderer.TryGetMaterialFailure(textured, out Failure failure));
            Assert.Equal(Failure.TexturesFull, failure);
            Assert.NotEqual(Renderer.illegal_material_index, DrawMaterial(plain)); // no textures, it fits
            AssertIllegalTexels(Centre(8)); // the reserved illegal texture keeps its slot
        });
    }

    [SkippableFact]
    public void A_missing_texture_falls_back_on_its_own_and_the_material_keeps_drawing()
    {
        Run(() =>
        {
            Material material = NewMaterial("missing", 0.0f, 1.0f, 1.0f);
            var empty = new Phalanx.World.RHI_Texture(4, 4); // a size only texture, no pixels
            material.SetTexture(MaterialTextureType.Color, empty);
            material.SetTexture(MaterialTextureType.Normal, new Phalanx.World.RHI_Texture(4, 4));
            material.SetProperty(MaterialProperty.Normal, 1.0f);
            Render render = CreateScene(material);
            List<string> warnings = Warnings("has no pixels", () =>
            {
                for (int i = 0; i < 3; i++)
                    Frame();
            });
            Assert.Equal(2, warnings.Count); // the colour and the normal texture, once each
            Assert.Equal(2u, DrawMaterial(render));
            MaterialParameters p = TableEntry(2);
            Assert.False(p.HasTextureAlbedo());
            Assert.Equal(0u, p.flags & (1u << 1)); // no normal map: the vertex normal
            // the constant colour, the albedo of a white texture
            byte[] centre = Centre(1);
            Assert.Equal(new byte[] { 0, 255, 255, 255 }, centre[0..4]);
        });
    }

    private sealed class OverrideMaterial : Behaviour, IPreDraw
    {
        public Material? Material;
        public void OnPreDraw(in ViewContext view) => view.SetMaterial(Material);
    }

    [SkippableFact]
    public void An_invalid_OnPreDraw_override_draws_the_illegal_material_in_that_view_only()
    {
        Run(() =>
        {
            Material own = NewMaterial("own");
            Render render = CreateScene(own);
            Render other  = AddCube(new Vector3(1.5f, 0, 2.0f), own);
            Material destroyed = NewMaterial("destroyed_override");
            destroyed.Destroy();
            render.GetEntity()!.AddBehaviour(new OverrideMaterial { Material = destroyed });
            List<string> warnings = Warnings("'destroyed_override' is destroyed", () =>
            {
                for (int i = 0; i < 3; i++)
                    Frame();
            });
            Assert.Single(warnings);
            Assert.Equal(Renderer.illegal_material_index, ViewDrawMaterial(render));
            Assert.Equal(2u, DrawMaterial(render)); // the frame's (shadow) draws keep the render's own
            Assert.Equal(2u, ViewDrawMaterial(other));
            AssertIllegalTexels(Centre(8));
        });
    }
}

// the illegal texture file, no gpu needed
public class IllegalMaterialTextureTests
{
    [Fact]
    public void The_shipped_texture_is_the_16x16_sad_face()
    {
        Phalanx.World.RHI_Texture texture = Renderer.LoadIllegalTexture(out bool from_file);
        Assert.True(from_file);
        Assert.Equal(16u, texture.GetWidth());
        Assert.Equal(16u, texture.GetHeight());
        byte[] mip0 = texture.GetCpuMips()[0];
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, mip0[0..4]);           // magenta corner
        int eye = (5 * 16 + 6) * 4;
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, mip0[eye..(eye + 4)]);       // the left eye
    }

    [Fact]
    public void A_missing_file_falls_back_to_solid_magenta()
    {
        Phalanx.World.RHI_Texture texture = Renderer.LoadIllegalTexture(out bool from_file, Path.Combine(Path.GetTempPath(), "no_such_dir_phalanx", "illegal_material.png"));
        Assert.False(from_file);
        Assert.Equal(1u, texture.GetWidth());
        Assert.Equal(new byte[] { 255, 0, 255, 255 }, texture.GetCpuMips()[0]);
    }
}
