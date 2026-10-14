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
}
