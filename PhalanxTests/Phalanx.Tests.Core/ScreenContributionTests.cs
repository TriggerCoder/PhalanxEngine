using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Phalanx.Math;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// the views' contribution cull: the projected size of a bounding sphere (Phalanx.Math.ScreenContribution, spartan's
// sphere_project_ndc_vp / sphere_contributes), its per entity dither, and the store's vector cull of it
// (EntityStore.Culling.cs)
[Collection("World")]
public class ScreenContributionTests
{
    private static readonly EntityArchetype Drawn = EntityArchetype.Empty.Renderable();
    private static readonly Vector2 Resolution = new(1920, 1080);

    // a reverse-z camera at the origin looking down +z, like Camera's
    private static Matrix4x4 Perspective(float fov_y, float aspect = 1920.0f / 1080.0f) =>
        Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY) * Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(fov_y, aspect, 10000.0f, 0.1f);

    [Fact]
    public void A_sphere_on_the_axis_of_a_perspective_view_measures_its_conservative_pixel_size()
    {
        float fov = MathF.PI / 3.0f, f = 1.0f / MathF.Tan(fov * 0.5f);
        Matrix4x4 vp = Perspective(fov);
        foreach ((float d, float r) in new[] { (10.0f, 0.5f), (100.0f, 0.5f), (1000.0f, 0.866f), (3.0f, 1.0f) })
        {
            // the y side is r f / (d - r) on each side of the center, in ndc: r f H / (d - r) pixels (x matches: W / aspect = H)
            float expected = r * f * Resolution.Y / (d - r);
            float pixels = ScreenContribution.SpherePixels(vp, new Vector3(0, 0, d), r, Resolution);
            Assert.True(MathF.Abs(pixels - expected) <= 1e-3f * expected, $"{d} {r}: {pixels} vs {expected}");
        }
        // twice as far, about half as large; and the size grows off the axis (the perspective stretch), never shrinks
        float near = ScreenContribution.SpherePixels(vp, new Vector3(0, 0, 200), 0.5f, Resolution);
        float far = ScreenContribution.SpherePixels(vp, new Vector3(0, 0, 400), 0.5f, Resolution);
        Assert.InRange(far / near, 0.49f, 0.51f);
        float edge = ScreenContribution.SpherePixels(vp, new Vector3(300, 100, 400), 0.5f, Resolution);
        Assert.True(edge >= far, $"{edge} {far}");
    }

    [Fact]
    public void An_orthographic_view_measures_the_same_size_at_every_distance()
    {
        // 20 x 10 world units over 1920 x 1080: 96 and 108 pixels per unit, the larger side counts
        Matrix4x4 vp = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY) * Matrix4x4Extensions.CreateOrthographicLH(20, 10, 0, 1000);
        foreach (float d in new[] { 0.0f, 1.0f, 50.0f, 900.0f, -5.0f })
        {
            float pixels = ScreenContribution.SpherePixels(vp, new Vector3(3, -2, d), 0.25f, Resolution);
            Assert.True(MathF.Abs(pixels - 0.5f * 108.0f) < 1e-3f, $"{d}: {pixels}");
        }
        Assert.False(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, 10), 0.005f, Resolution, 2.0f)); // 1.08 px
        Assert.True(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, 10), 0.01f, Resolution, 2.0f));   // 2.16 px
    }

    [Fact]
    public void Across_the_near_plane_a_sphere_contributes_and_behind_the_camera_it_does_not()
    {
        Matrix4x4 vp = Perspective(MathF.PI / 3.0f);
        Assert.Equal(ScreenContribution.Projection.Straddle, ScreenContribution.ProjectSphereNdc(vp, new Vector3(0, 0, 0.2f), 0.5f, out _, out _));
        Assert.Equal(ScreenContribution.Projection.Straddle, ScreenContribution.ProjectSphereNdc(vp, new Vector3(0, 0, -0.3f), 0.5f, out _, out _));
        Assert.True(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, 0.2f), 0.5f, Resolution, 1000.0f));
        Assert.Equal(ScreenContribution.Projection.Behind, ScreenContribution.ProjectSphereNdc(vp, new Vector3(0, 0, -10), 0.5f, out _, out _));
        Assert.False(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, -10), 0.5f, Resolution, 0.001f));
        Assert.True(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, -10), 0.5f, Resolution, 0.0f)); // 0: off
    }

    [Fact]
    public void A_very_wide_field_of_view_gives_finite_sizes_that_grow_toward_the_edges()
    {
        foreach (float degrees in new[] { 120.0f, 170.0f, 179.0f })
        {
            Matrix4x4 vp = Perspective(degrees * MathF.PI / 180.0f, 1.0f);
            float center = ScreenContribution.SpherePixels(vp, new Vector3(0, 0, 50), 0.5f, new Vector2(1080));
            float side = ScreenContribution.SpherePixels(vp, new Vector3(0, 400, 50), 0.5f, new Vector2(1080));
            Assert.True(float.IsFinite(center) && center > 0.0f, $"{degrees}: {center}");
            Assert.True(float.IsFinite(side) && side > center, $"{degrees}: {side} {center}");
        }
    }

    [Fact]
    public void The_threshold_is_inclusive_and_the_dither_spreads_it_over_half_to_all_of_it()
    {
        Matrix4x4 vp = Perspective(MathF.PI / 3.0f);
        float pixels = ScreenContribution.SpherePixels(vp, new Vector3(0, 0, 100), 0.5f, Resolution);
        Assert.True(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, 100), 0.5f, Resolution, pixels));
        Assert.False(ScreenContribution.SphereContributes(vp, new Vector3(0, 0, 100), 0.5f, Resolution, pixels * 1.001f));

        var scales = Enumerable.Range(0, 100000).Select(s => ScreenContribution.DitherScale((uint)s)).ToArray();
        Assert.All(scales, s => Assert.InRange(s, 1.0f - ScreenContribution.DitherRange, 1.0f));
        Assert.InRange(scales.Average(), 0.745f, 0.755f);
        // uniform over the band, and neighbouring slots (spawned together, so usually neighbours in space) far apart
        for (int bin = 0; bin < 10; bin++)
            Assert.InRange(scales.Count(s => s >= 0.5f + 0.05f * bin && s < 0.55f + 0.05f * bin), 9500, 10500);
        Assert.True(Enumerable.Range(0, 1000).Average(s => MathF.Abs(scales[s] - scales[s + 1])) > 0.1f);
        Assert.Equal(ScreenContribution.DitherScale(12345), ScreenContribution.DitherScale(12345));
    }

    [Fact]
    public void The_store_cull_keeps_what_the_scalar_formula_keeps_and_ignores_rotation()
    {
        EntityStoreTests.Run(() =>
        {
            EntityStore store = World.World.Store;
            RenderBatch unit = RenderBatches.GetForBounds(new BoundingBox(new Vector3(-0.5f), new Vector3(0.5f)));
            var random = new Random(11);
            var ids = new List<EntityId>();
            for (int i = 0; i < 20000; i++)
            {
                var p = new Vector3(random.NextSingle() * 400 - 200, random.NextSingle() * 40 - 20, random.NextSingle() * 1500 - 100);
                var rotation = Quaternion.Normalize(new Quaternion(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle()));
                ids.Add(store.Spawn(Drawn, new EntitySpawn(p, unit) { Rotation = rotation, Scale = new Vector3(0.2f + random.NextSingle(), 1.0f, 0.5f) }));
            }
            store.SyncTransforms();

            Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(new Vector3(0, 5, -20), new Vector3(0, 0, 100), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(MathF.PI / 3.0f, 1920.0f / 1080.0f, 10000.0f, 0.1f);
            var request = new InstanceCullRequest();
            request.SetFrustum(new Frustum(view, projection));
            request.SetContribution(view * projection, Resolution, 2.0f);
            HashSet<uint> kept = Cull(store, request);

            var frustum_only = new InstanceCullRequest();
            frustum_only.SetFrustum(new Frustum(view, projection));
            HashSet<uint> in_frustum = Cull(store, frustum_only);
            var expected = new HashSet<uint>();
            int skipped = 0;
            foreach (EntityId id in ids)
            {
                uint slot = store.GetSlot(id);
                if (!in_frustum.Contains(slot))
                    continue;
                Sphere sphere = store.GetCullSphere(id);
                // the radius is the mesh's |extent| times the largest scale, whatever the rotation
                Assert.True(MathF.Abs(sphere.Radius - MathF.Sqrt(0.75f) * MathF.Max(1.0f, store.GetLocalScale(id).X)) < 1e-4f);
                if (ScreenContribution.SphereContributes(view * projection, sphere.Center, sphere.Radius, Resolution, 2.0f * ScreenContribution.DitherScale(slot)))
                    expected.Add(slot);
                else
                    skipped++;
            }
            Assert.True(skipped > 2000 && expected.Count > 2000, $"{skipped} {expected.Count}");
            Assert.True(expected.SetEquals(kept));
            Assert.Equal(skipped, request.RowsSkippedSmall);
            // nothing of 2 pixels or more goes, nothing under 1 stays
            foreach (EntityId id in ids)
            {
                uint slot = store.GetSlot(id);
                if (!in_frustum.Contains(slot))
                    continue;
                Sphere sphere = store.GetCullSphere(id);
                float pixels = ScreenContribution.SpherePixels(view * projection, sphere.Center, sphere.Radius, Resolution);
                if (pixels >= 2.0f) Assert.Contains(slot, kept);
                if (pixels < 1.0f) Assert.DoesNotContain(slot, kept);
            }

            // spinning an entity doesn't change what it measures (its world aabb does)
            EntityId spun = ids[0];
            float radius = store.GetCullSphere(spun).Radius;
            store.SetLocalRotation(spun, Quaternion.CreateFromYawPitchRoll(0.7f, 0.3f, 1.1f));
            store.SyncTransforms();
            Assert.True(MathF.Abs(store.GetCullSphere(spun).Radius - radius) < 1e-5f);

            // 0 turns it off: the frustum's result
            request.SetFrustum(new Frustum(view, projection));
            request.SetContribution(view * projection, Resolution, 0.0f);
            Assert.True(in_frustum.SetEquals(Cull(store, request)));
            Assert.Equal(0, request.RowsSkippedSmall);
        });
    }

    private static HashSet<uint> Cull(EntityStore store, InstanceCullRequest request)
    {
        store.CullInstances(new[] { request });
        store.FinishCull(request);
        return request.Slots.Take(request.Total).ToHashSet();
    }
}
