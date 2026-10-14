using System.Numerics;
using Phalanx.Math;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// Light's photometric units and colours (Light.cpp:46-86, :503-660, :701; Color.cpp:30-86; shared_lighting.h)
[Collection("World")]
public class LightUnitsTests
{
    private static Light CreateLight(LightType type)
    {
        World.World.SetMainThread();
        Entity entity = World.World.CreateEntity();
        Light light = entity.AddComponent<Light>()!;
        light.SetLightType(type);
        return light;
    }

    private static float Luminance(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    [Fact]
    public void Temperature_colours_are_linear_with_unit_luminance()
    {
        foreach (float kelvin in new[] { 1850.0f, 2700.0f, 5778.0f, 6500.0f, 15000.0f })
            Assert.Equal(1.0f, Luminance(new Color(kelvin)), 4);
        Color warm = new Color(2700.0f), cold = new Color(15000.0f);
        Assert.True(warm.R > warm.B && cold.B > cold.R);
        // srgb decoded: 6500 K is almost white, its encoded blue would be ~0.98, linear (unit luminance) ~0.96..1.05
        Color d65 = new Color(6500.0f);
        Assert.InRange(d65.B / d65.R, 0.9f, 1.0f);
    }

    [Fact]
    public void Intensities_convert_to_the_gpu_scale_like_spartan()
    {
        try
        {
            Light sun = CreateLight(LightType.Directional);
            Assert.Equal(Light.SunIlluminanceLux, sun.GetIntensityPhotometric());
            Assert.Equal(LightIntensityUnit.Lux, sun.GetIntensityUnit());
            Assert.Equal(120000.0f / 683.0f, sun.GetIntensityRadiometric(), 3);

            Light point = CreateLight(LightType.Point);
            point.SetIntensity(1000.0f);
            Assert.Equal(LightIntensityUnit.Lumens, point.GetIntensityUnit());
            Assert.Equal(1000.0f / 683.0f / (4.0f * MathF.PI), point.GetIntensityRadiometric(), 5);
            Assert.Equal(1000.0f / (4.0f * MathF.PI * 4.0f), point.GetIlluminanceAt(2.0f), 3);
            Assert.Equal(point.GetIntensityRadiometric(), point.GetIntensityWatt());

            Light spot = CreateLight(LightType.Spot);
            spot.SetAngle(0.5f);
            spot.SetIntensity(1000.0f);
            float solid_angle = 2.0f * MathF.PI * ((1.0f - MathF.Cos(0.45f)) + (MathF.Cos(0.45f) - MathF.Cos(0.5f)) / 3.0f);
            Assert.Equal(solid_angle, Lighting.SpotSolidAngle(0.5f), 5);
            Assert.Equal(1000.0f / 683.0f / solid_angle, spot.GetIntensityRadiometric(), 4);
            Assert.Equal(Lighting.SpotHalfAngle(10.0f), Lighting.SpotHalfAngle(1.57f), 3); // clamped below 90 degrees

            // the local light colour is its chromaticity at unit luminance
            point.SetColor(new Color(2.0f, 1.0f, 0.5f, 1.0f));
            Assert.Equal(1.0f, Luminance(point.GetColorEffective()), 4);
        }
        finally { World.World.ClearScene(); }
    }

    [Fact]
    public void The_range_follows_the_intensity_until_it_is_edited()
    {
        try
        {
            Light point = CreateLight(LightType.Point);
            point.SetIntensity(800.0f);
            Assert.Equal(MathF.Sqrt(800.0f / (4.0f * MathF.PI * 0.25f)), point.GetRange(), 3); // a quarter lux at the range
            point.SetIntensity(3200.0f);
            Assert.Equal(MathF.Sqrt(3200.0f / (4.0f * MathF.PI * 0.25f)), point.GetRange(), 3);
            point.SetRange(5.0f);
            point.SetIntensity(100.0f);
            Assert.Equal(5.0f, point.GetRange());
        }
        finally { World.World.ClearScene(); }
    }

    [Fact]
    public void The_sun_colour_is_the_atmosphere_transmittance()
    {
        try
        {
            Light sun = CreateLight(LightType.Directional);
            Entity entity = sun.GetEntity()!;
            // straight down: barely attenuated, blue scattered most
            entity.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
            Color zenith = sun.GetColorEffective();
            Assert.True(zenith.R > 0.85f && zenith.B < zenith.R, $"{zenith.R} {zenith.G} {zenith.B}");
            // grazing: much redder and dimmer
            entity.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.03f));
            Color low = sun.GetColorEffective();
            Assert.True(low.R < zenith.R && low.B / low.R < zenith.B / zenith.R, $"{low.R} {low.G} {low.B}");
            // below the horizon: black
            entity.SetRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f));
            Assert.Equal(0.0f, sun.GetColorEffective().R);
        }
        finally { World.World.ClearScene(); }
    }
}
