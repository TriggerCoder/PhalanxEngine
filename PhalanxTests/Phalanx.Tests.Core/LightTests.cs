using System.Numerics;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

public class LightTests
{
    // Light.h:183: GetViewProjectionMatrix is view * projection (it was view * view)
    [Fact]
    public void Spot_light_view_projection_maps_its_axis_to_the_centre_with_reverse_z_depth()
    {
        var entity = new Entity();
        entity.SetPosition(new Vector3(1, 2, 3)); // forward stays +z
        var light = new Light(entity);
        light.SetLightType(LightType.Spot);
        light.SetAngle(0.5f);
        light.SetRange(10.0f);

        Vector4 Ndc(Vector3 p) { Vector4 c = Vector4.Transform(new Vector4(p, 1), light.GetViewProjectionMatrix(0)); return c / c.W; }

        Vector4 on_axis = Ndc(new Vector3(1, 2, 8));
        Assert.Equal(0.0f, on_axis.X, 4);
        Assert.Equal(0.0f, on_axis.Y, 4);
        Assert.InRange(on_axis.Z, 0.0f, 1.0f);

        // reverse-z: near (0.05) maps to 1, the range to 0
        Assert.Equal(1.0f, Ndc(new Vector3(1, 2, 3.05f)).Z, 3);
        Assert.Equal(0.0f, Ndc(new Vector3(1, 2, 13.0f)).Z, 3);

        // off axis to the right lands right of centre
        Assert.True(Ndc(new Vector3(2, 2, 8)).X > 0.0f);
    }

    // Light.h:194-195, :247: the contact shadow slice has its own field, it used to alias the light table index
    [Fact]
    public void The_screen_space_shadow_slice_is_its_own_field_and_resets_off_directional()
    {
        var light = new Light(new Entity());
        light.SetLightType(LightType.Directional);
        Assert.True(light.GetFlag(LightFlags.ShadowsScreenSpace)); // on by default, like spartan's light
        light.SetIndex(7);
        light.SetScreenSpaceShadowsSliceIndex(2);
        Assert.Equal(7u, light.GetIndex());
        Assert.Equal(2u, light.GetScreenSpaceShadowsSliceIndex());

        // Light.cpp:438-442: only directional lights keep the flag and the slice
        light.SetLightType(LightType.Point);
        Assert.False(light.GetFlag(LightFlags.ShadowsScreenSpace));
        Assert.Equal(0u, light.GetScreenSpaceShadowsSliceIndex());
        Assert.Equal(7u, light.GetIndex());

        // switching the shadows off takes the contact shadows with them (Light.cpp SetFlag)
        light.SetLightType(LightType.Directional);
        light.SetFlag(LightFlags.ShadowsScreenSpace);
        light.SetFlag(LightFlags.Shadows, false);
        Assert.False(light.GetFlag(LightFlags.ShadowsScreenSpace));
    }
}
