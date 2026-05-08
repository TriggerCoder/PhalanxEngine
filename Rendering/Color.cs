using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Phalanx;

[StructLayout(LayoutKind.Sequential)]
public struct Color : IEquatable<Color>
{
    public float R;
    public float G;
    public float B;
    public float A;
    public Color(float r, float g, float b, float a = 1.0f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }
    public Color(float temperature_kelvin, float a = 1.0f)
    {
        TemperatureToColor(temperature_kelvin);
        A = a;
    }

    public static implicit operator Vector4(Color c) => new Vector4(c.R, c.G, c.B, c.A);
    public static implicit operator Color(Vector4 v) => new Color(v.X, v.Y, v.Z, v.W);

    public static bool operator ==(Color l, Color R) { return l.Equals(R); }
    public static bool operator !=(Color l, Color R) { return !l.Equals(R); }
    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        if (obj is Color other)
            return Equals(other);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(Color other) { return (R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A)); }
    public override readonly int GetHashCode() { return HashCode.Combine(R, G, B, A); }

    // Most of the color/temperature values are derived from: https://physicallybased.info/ 
    // Might get inaccurate over 40000 K (which is really the limit that you should be using)
    void TemperatureToColor(float temperature_kelvin)
    {
        // Constants for color temperature to RGB conversion
        const float A_R = 329.698727446f;
        const float B_R = -0.1332047592f;
        const float A_G = 288.1221695283f;
        const float B_G = -0.0755148492f;

        // Ensure temperature is above absolute zero
        if (temperature_kelvin < 0)
        {
            // Handle error
        }

        float temp = temperature_kelvin / 100.0f;

        R = 0.0f;
        G = 0.0f;
        B = 0.0f;

        if (temp <= 66)
        {
            R = 255;
            G = temp;
            G = 99.4708025861f * MathF.Log(G) - 161.1195681661f;

            if (temp <= 19)
            {
                B = 0;
            }
            else
            {
                B = temp - 10.0f;
                B = 138.5177312231f * MathF.Log(B) - 305.0447927307f;
            }
        }
        else
        {
            R = temp - 60.0f;
            R = A_R * MathF.Pow(R, B_R);
            G = temp - 60.0f;
            G = A_G * MathF.Pow(G, B_G);
            B = 255;
        }

        // clamp rgb values to [0, 1]
        R = System.Math.Clamp(R / 255.0f, 0.0f, 1.0f);
        G = System.Math.Clamp(G / 255.0f, 0.0f, 1.0f);
        B = System.Math.Clamp(B / 255.0f, 0.0f, 1.0f);
    }

    // standard
    private static readonly Color standard_black = new Color(0.0f, 0.0f, 0.0f, 1.0f);
    private static readonly Color standard_white = new Color(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Color standard_transparent = new Color(0.0f, 0.0f, 0.0f, 0.0f);
    private static readonly Color standard_red = new Color(1.0f, 0.0f, 0.0f, 1.0f);
    private static readonly Color standard_green = new Color(0.0f, 1.0f, 0.0f, 1.0f);
    private static readonly Color standard_blue = new Color(0.0f, 0.0f, 1.0f, 1.0f);
    private static readonly Color standard_yellow = new Color(1.0f, 1.0f, 0.0f, 1.0f);
    private static readonly Color standard_cornflower_blue = new Color(0.396f, 0.611f, 0.937f, 1.0f);
    private static readonly Color standard_renderer_lines = new Color(0.41f, 0.86f, 1.0f, 1.0f);

    // materials
    private static readonly Color material_aluminum = new Color(0.912f, 0.914f, 0.920f);        // metallic: 1.0
    private static readonly Color material_blood = new Color(0.644f, 0.003f, 0.005f);
    private static readonly Color material_bone = new Color(0.793f, 0.793f, 0.664f);
    private static readonly Color material_brass = new Color(0.887f, 0.789f, 0.434f);
    private static readonly Color material_brick = new Color(0.262f, 0.095f, 0.061f);
    private static readonly Color material_charcoal = new Color(0.020f, 0.020f, 0.020f);
    private static readonly Color material_chocolate = new Color(0.162f, 0.091f, 0.060f);
    private static readonly Color material_chromium = new Color(0.550f, 0.556f, 0.554f);        // metallic: 1.0
    private static readonly Color material_cobalt = new Color(0.662f, 0.655f, 0.634f);
    private static readonly Color material_concrete = new Color(0.510f, 0.510f, 0.510f);
    private static readonly Color material_cooking_oil = new Color(0.738f, 0.687f, 0.091f);
    private static readonly Color material_copper = new Color(0.926f, 0.721f, 0.504f);
    private static readonly Color material_diamond = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_egg_shell = new Color(0.610f, 0.624f, 0.631f);
    private static readonly Color material_eye_cornea = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_eye_lens = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_eye_sclera = new Color(0.680f, 0.490f, 0.370f);
    private static readonly Color material_glass = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_gold = new Color(0.944f, 0.776f, 0.373f);
    private static readonly Color material_gray_card = new Color(0.180f, 0.180f, 0.180f);
    private static readonly Color material_honey = new Color(0.831f, 0.397f, 0.038f);
    private static readonly Color material_ice = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_iron = new Color(0.531f, 0.512f, 0.496f);            // metallic: 1.0
    private static readonly Color material_ketchup = new Color(0.164f, 0.006f, 0.002f);
    private static readonly Color material_lead = new Color(0.632f, 0.626f, 0.641f);
    private static readonly Color material_mercury = new Color(0.781f, 0.779f, 0.779f);
    private static readonly Color material_milk = new Color(0.604f, 0.584f, 0.497f);
    private static readonly Color material_nickel = new Color(0.649f, 0.610f, 0.541f);
    private static readonly Color material_office_paper = new Color(0.738f, 0.768f, 1.000f);
    private static readonly Color material_plastic_pc = new Color(1.000f, 1.000f, 1.000f);      // specular: 0.640
    private static readonly Color material_plastic_pet = new Color(1.000f, 1.000f, 1.000f);     // specular: 0.623
    private static readonly Color material_plastic_acrylic = new Color(1.000f, 1.000f, 1.000f); // specular: 0.462
    private static readonly Color material_plastic_pp = new Color(1.000f, 1.000f, 1.000f);      // specular: 0.487
    private static readonly Color material_plastic_pvc = new Color(1.000f, 1.000f, 1.000f);     // specular: 0.550
    private static readonly Color material_platinum = new Color(0.679f, 0.642f, 0.588f);
    private static readonly Color material_salt = new Color(0.800f, 0.800f, 0.800f);
    private static readonly Color material_sand = new Color(0.440f, 0.386f, 0.231f);
    private static readonly Color material_sapphire = new Color(0.670f, 0.764f, 0.855f);
    private static readonly Color material_silver = new Color(0.962f, 0.949f, 0.922f);
    private static readonly Color material_skin_1 = new Color(0.847f, 0.638f, 0.552f);
    private static readonly Color material_skin_2 = new Color(0.799f, 0.485f, 0.347f);
    private static readonly Color material_skin_3 = new Color(0.600f, 0.310f, 0.220f);
    private static readonly Color material_skin_4 = new Color(0.430f, 0.200f, 0.130f);
    private static readonly Color material_skin_5 = new Color(0.360f, 0.160f, 0.080f);
    private static readonly Color material_skin_6 = new Color(0.090f, 0.050f, 0.020f);
    private static readonly Color material_snow = new Color(0.810f, 0.810f, 0.810f);
    private static readonly Color material_tire = new Color(0.023f, 0.023f, 0.023f);            // metallic: 0.0, specular 0.5
    private static readonly Color material_titanium = new Color(0.616f, 0.582f, 0.544f);
    private static readonly Color material_tungsten = new Color(0.925f, 0.835f, 0.757f);
    private static readonly Color material_vanadium = new Color(0.945f, 0.894f, 0.780f);
    private static readonly Color material_water = new Color(1.000f, 1.000f, 1.000f);
    private static readonly Color material_zinc = new Color(0.875f, 0.867f, 0.855f);

    // lights
    private static readonly Color light_sky_clear = new Color(15000);                           // intensity: 20000  lx
    private static readonly Color light_sky_daylight_overcast = new Color(6500);                // intensity: 2000   lx
    private static readonly Color light_sky_moonlight = new Color(4000);                        // intensity: 0.1    lx
    private static readonly Color light_sky_sunrise = new Color(2000);
    private static readonly Color light_candle_flame = new Color(1850);                         // intensity: 13     lm
    private static readonly Color light_direct_sunlight = new Color(5778);                      // intensity: 120000 lx
    private static readonly Color light_digital_display = new Color(6500);                      // intensity: 200    cd/m2
    private static readonly Color light_fluorescent_tube_light = new Color(5000);               // intensity: 1000   lm 
    private static readonly Color light_kerosene_lamp = new Color(1850);                        // intensity: 50     lm
    private static readonly Color light_light_bulb = new Color(2700);                           // intensity: 800    lm
    private static readonly Color light_photo_flash = new Color(5500);                          // intensity: 20000  lm

    // Standard
    public static Color StandardBlack => standard_black;
    public static Color StandardWhite => standard_white;
    public static Color StandardTransparent => standard_transparent;
    public static Color StandardRed => standard_red;
    public static Color StandardGreen => standard_green;
    public static Color StandardBlue => standard_blue;
    public static Color StandardYellow => standard_yellow;
    public static Color StandardCornflowerBlue => standard_cornflower_blue;
    public static Color StandardRendererLines => standard_renderer_lines;

    // materials
    public static Color MaterialAluminum => material_aluminum;
    public static Color MaterialBlood => material_blood;
    public static Color MaterialBone => material_bone;
    public static Color MaterialBrass => material_brass;
    public static Color MaterialBrick => material_brick;
    public static Color MaterialCharcoal => material_charcoal;
    public static Color MaterialChocolate => material_chocolate;
    public static Color MaterialChromium => material_chromium;
    public static Color MaterialCobalt => material_cobalt;
    public static Color MaterialConcrete => material_concrete;
    public static Color MaterialCookingOil => material_cooking_oil;
    public static Color MaterialCopper => material_copper;
    public static Color MaterialDiamond => material_diamond;
    public static Color MaterialEggShell => material_egg_shell;
    public static Color MaterialEyeCornea => material_eye_cornea;
    public static Color MaterialEyeLens => material_eye_lens;
    public static Color MaterialEyeSclera => material_eye_sclera;
    public static Color MaterialGlass => material_glass;
    public static Color MaterialGold => material_gold;
    public static Color MaterialGrayCard => material_gray_card;
    public static Color MaterialHoney => material_honey;
    public static Color MaterialIce => material_ice;
    public static Color MaterialIron => material_iron;
    public static Color MaterialKetchup => material_ketchup;
    public static Color MaterialLead => material_lead;
    public static Color MaterialMercury => material_mercury;
    public static Color MaterialMilk => material_milk;
    public static Color MaterialNickel => material_nickel;
    public static Color MaterialOfficePaper => material_office_paper;
    public static Color MaterialPlasticPc => material_plastic_pc;
    public static Color MaterialPlasticPet => material_plastic_pet;
    public static Color MaterialPlasticAcrylic => material_plastic_acrylic;
    public static Color MaterialPlasticPp => material_plastic_pp;
    public static Color MaterialPlasticPvc => material_plastic_pvc;
    public static Color MaterialPlatinum => material_platinum;
    public static Color MaterialSalt => material_salt;
    public static Color MaterialSand => material_sand;
    public static Color MaterialSapphire => material_sapphire;
    public static Color MaterialSilver => material_silver;
    public static Color MaterialSkin1 => material_skin_1;
    public static Color MaterialSkin2 => material_skin_2;
    public static Color MaterialSkin3 => material_skin_3;
    public static Color MaterialSkin4 => material_skin_4;
    public static Color MaterialSkin5 => material_skin_5;
    public static Color MaterialSkin6 => material_skin_6;
    public static Color MaterialSnow => material_snow;
    public static Color MaterialTire => material_tire;
    public static Color MaterialTitanium => material_titanium;
    public static Color MaterialTungsten => material_tungsten;
    public static Color MaterialVanadium => material_vanadium;
    public static Color MaterialWater => material_water;
    public static Color MaterialZinc => material_zinc;

    // Lights
    public static Color LightSkyClear => light_sky_clear;
    public static Color LightSkyDaylightOvercast => light_sky_daylight_overcast;
    public static Color LightSkyMoonlight => light_sky_moonlight;
    public static Color LightSkySunrise => light_sky_sunrise;
    public static Color LightCandleFlame => light_candle_flame;
    public static Color LightDirectSunlight => light_direct_sunlight;
    public static Color LightDigitalDisplay => light_digital_display;
    public static Color LightFluorescentTubeLight => light_fluorescent_tube_light;
    public static Color LightKeroseneLamp => light_kerosene_lamp;
    public static Color LightLightBulb => light_light_bulb;
    public static Color LightPhotoFlash => light_photo_flash;
}