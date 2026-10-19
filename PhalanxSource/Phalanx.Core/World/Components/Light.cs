using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx.Math;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.World;
public enum LightType
{
    Directional,
    Point,
    Spot,
    Area,
    Max
}
public enum LightIntensity
{
    bulb_stadium,    // intense light used in stadiums for sports events, comparable to sunlight
    bulb_500_watt,   // a very bright domestic bulb or small industrial light
    bulb_150_watt,   // a bright domestic bulb, equivalent to an old-school incandescent bulb
    bulb_100_watt,   // a typical bright domestic bulb
    bulb_60_watt,    // a medium intensity domestic bulb
    bulb_25_watt,    // a low intensity domestic bulb, used for mood lighting or as a night light
    bulb_flashlight, // light emitted by an average flashlight, portable and less intense
    black_hole,      // no light emitted
    custom           // custom intensity
}
public enum LightIntensityUnit
{
    Lux,
    Lumens
}
public enum LightPreset
{
    dawn,        // sunrise - early morning with warm orange glow
    day,         // bright midday sun - direct sunlight at peak intensity
    dusk,        // sunset - evening with warm golden tones
    night,       // nighttime with soft moonlight
    david_lynch, // dreamy late afternoon - low sun, soft eerie light
    custom       // custom settings
}
public enum LightFlags : uint
{
    Shadows = 1U << 0,
    ShadowsScreenSpace = 1U << 1,
    Volumetric = 1U << 2,
    DayNightCycle = 1U << 3, // rotates the light according to the time of day (game time)
    RealTimeCycle = 1U << 4  // derives the time of day from the real world time
}

[Serializable]
public class Light : Component
{
    // directional matrix parameters (Light.cpp:39-44)
    const float cascade_near_extent = 64.0f;
    const float cascade_far_extent = 2000.0f;
    const float cascade_depth_behind = 2000.0f;
    const float cascade_depth_ahead = 8000.0f;
    const float cascade_depth = cascade_depth_behind + cascade_depth_ahead;
    const float cascade_far_max_extent = float.MaxValue;
    private LightIntensity m_intensity = LightIntensity.bulb_500_watt;
    private float m_intensity_lumens_lux = 2600.0f;
    private LightFlags m_flags = 0;
    private LightType m_light_type = LightType.Max;
    private Color m_color_rgb = Color.StandardBlack;
    private float m_temperature_kelvin = 0.0f;
    private LightPreset m_preset = LightPreset.custom;
    private float m_range = 32.0f;
    private float m_angle_rad = MathEx.DegToRad * 30.0f;
    private float m_area_width = 1.0f;  // area light width in meters
    private float m_area_height = 1.0f;  // area light height in meters
    private uint m_index = 0;
    private uint m_screen_space_shadows_slice_index = 0; // Light.h:247
    private BoundingBox m_bounding_box = BoundingBox.Zero;
    private Vector3 m_far_cascade_min = Vector3.Zero;
    private Vector3 m_far_cascade_max = Vector3.Zero;
    private bool m_is_active_previous_frame = false;
    private float m_draw_distance = 512.0f; // max distance at which light will affect objects (meters)
    private float m_distance_shadows = 64.0f; // max distance at which shadow maps are rendered (meters, Light.h:253)

    // matrices/frustums per slice/face/cascade
    private Frustum[] m_frustums = new Frustum[6];
    private Matrix4x4[] m_matrix_view = new Matrix4x4[6];
    private Matrix4x4[] m_matrix_projection = new Matrix4x4[6];

    // atlas entries per slice/face/cascade
    private Rectangle[] m_atlas_rectangles = new Rectangle[6];
    private Vector2[] m_atlas_offsets = new Vector2[6];
    private Vector2[] m_atlas_scales = new Vector2[6];

    public Light(Entity entity) : base(entity)
    {
        RegisterAttribute(() => m_flags, v => m_flags = v);
        RegisterAttribute(() => m_range, v => m_range = v);
        RegisterAttribute(() => m_intensity_lumens_lux, v => m_intensity_lumens_lux = v);
        RegisterAttribute(() => m_angle_rad, v => m_angle_rad = v);
        RegisterAttribute(() => m_color_rgb, v => m_color_rgb = v);
        RegisterAttribute(() => m_temperature_kelvin, v => m_temperature_kelvin = v);
        RegisterAttribute(() => m_draw_distance, v => m_draw_distance = v);
        RegisterAttribute(() => m_distance_shadows, v => m_distance_shadows = v);
        RegisterAttribute(() => m_bounding_box, v => m_bounding_box = v);
        RegisterAttribute(() => m_far_cascade_min, v => m_far_cascade_min = v);
        RegisterAttribute(() => m_far_cascade_max, v => m_far_cascade_max = v);
        RegisterAttribute(() => m_is_active_previous_frame, v => m_is_active_previous_frame = v);
        RegisterAttribute(() => m_index, v => m_index = v);
        RegisterAttribute(() => m_area_width, v => m_area_width = v);
        RegisterAttribute(() => m_area_height, v => m_area_height = v);
        RegisterAttribute(GetLightType, SetLightType);

        Array.Fill(m_matrix_view,Matrix4x4.Identity);
        Array.Fill(m_matrix_projection,Matrix4x4.Identity);

        SetColor(GetSensibleColor(m_light_type));
        SetIntensity(LightIntensity.bulb_500_watt);
        SetRange(GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad));
        SetFlag(LightFlags.Shadows);
        SetFlag(LightFlags.ShadowsScreenSpace);
    }
    // flags
    public LightFlags GetFlags() { return m_flags; }
    public bool GetFlag(LightFlags flag) { return (m_flags & flag) != 0; }
    public void SetFlag(LightFlags flag, bool enable = true)
    {
        bool currentlySet = (m_flags & flag) != 0;

        if (enable == currentlySet)
            return; // no change, nothing to do

        if (enable)
        {
            m_flags |= flag;
        }
        else
        {
            m_flags &= ~flag;

            // if the shadows have been disabled, disable properties which rely on them
            if ((flag & LightFlags.Shadows) != 0)
            {
                m_flags &= ~LightFlags.ShadowsScreenSpace;
                m_flags &= ~LightFlags.Volumetric;
            }
        }
    }

    // type
    public LightType GetLightType() { return m_light_type; }
    public void SetLightType(LightType type)
    {
        if (m_light_type == type)
            return;

        m_light_type = type;
        SetColor(GetSensibleColor(type));
        SetRange(GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad));

        if (m_light_type == LightType.Directional)
            SetIntensity(SunIlluminanceLux);
        else
        {
            m_flags &= ~LightFlags.ShadowsScreenSpace;
            m_screen_space_shadows_slice_index = 0; // Light.cpp:441
        }

        UpdateMatrices();
    }
    // Light.cpp:35, the directional default
    public const float SunIlluminanceLux = 120000.0f;

    // Light.cpp:46-74: the range at which the light has faded to a quarter lux
    static float GetSensibleRange(LightType type, float photometric_intensity, float angle_rad)
    {
        const float illuminance_cutoff_lux = 0.25f;
        const float min_range              = 0.5f;
        photometric_intensity = MathF.Max(photometric_intensity, 0.0f);
        switch (type)
        {
            case LightType.Directional:
                return float.MaxValue;
            case LightType.Point:
                return MathF.Max(min_range, MathF.Sqrt(photometric_intensity / (4.0f * MathF.PI * illuminance_cutoff_lux)));
            case LightType.Spot:
                return MathF.Max(min_range, MathF.Sqrt(photometric_intensity / Lighting.SpotSolidAngle(angle_rad) / illuminance_cutoff_lux));
            case LightType.Area:
                // on axis illuminance of a lambertian emitter falls off as flux / (pi * d^2)
                return MathF.Max(min_range, MathF.Sqrt(photometric_intensity / (MathF.PI * illuminance_cutoff_lux)));
            default:
                return 0.0f;
        }
    }
    // Light.cpp:76-86: whether the range still follows the intensity (it wasn't edited)
    static bool IsSensibleRange(float range, LightType type, float photometric_intensity, float angle_rad)
    {
        if (type == LightType.Directional)
            return false;
        float sensible  = GetSensibleRange(type, photometric_intensity, angle_rad);
        float tolerance = MathF.Max(0.01f, sensible * 0.001f);
        return MathF.Abs(range - sensible) <= tolerance;
    }
    public float GetRangeSensible() => GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad);
    Color GetSensibleColor(LightType type)
    {
        switch (type)
        {
            case LightType.Directional:
                return Color.LightSkyClear;
            case LightType.Point:
            case LightType.Spot:
            case LightType.Area:
                return Color.LightLightBulb;
            default:
                return Color.LightDirectSunlight;
        }
    }
    // Light::Tick (Light.cpp:142-180) updates the matrices when the light or the camera moved; here every tick, the
    // renderer also refreshes them after it packs the atlas (the directional snapping reads the atlas rectangles)
    public override void Tick()
    {
        UpdateMatrices();
    }

    public void UpdateMatrices()
    {
        UpdateViewMatrix();
        UpdateProjectionMatrix();
        UpdateBoundingBox();
    }
    private void UpdateViewMatrix()
    {
        Vector3 position = GetEntity().GetPosition(); // light's base position (arbitrary for directional)

        switch (m_light_type)
        {
            case LightType.Directional:
            {
                Camera? camera = World.GetCamera();
                if (camera is null)
                    return;

                // both cascades follow the camera, most of the depth sits ahead so the ground stays in range when flying
                Vector3 cameraPos = camera.GetEntity().GetPosition();
                Vector3 lightForward = GetEntity().GetForward();
                position = cameraPos - lightForward * cascade_depth_behind;
                // spartan uses world up unconditionally, a sun pointing straight up or down collapses the basis
                // (phalanx: the entity's up then, see the renderer-6 notes)
                Vector3 up = MathF.Abs(Vector3.Dot(lightForward, MathEx.Vector3.Up)) > 0.9999f ? GetEntity().GetUp() : MathEx.Vector3.Up;
                m_matrix_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, cameraPos, up);
                m_matrix_view[1] = m_matrix_view[0];

                // move the light in world units per texel to avoid shimmering
                {
                    // compute shadow extents (both fixed sizes)
                    Span<float> extents = stackalloc float[2] { cascade_near_extent, cascade_far_extent };
                    float atlasWidth = (float)RenderView.GetRenderTarget(Renderer_RenderTarget.shadow_atlas).GetWidth();

                    for (int i = 0; i < 2; i++)
                    {
                        float rectWidth = m_atlas_rectangles[i].Width;                   // cascade rectangle width in atlas
                        if (rectWidth <= 0.0f)
                            continue; // not packed yet: spartan divides by zero here and snaps to nan
                        float atlasScale = rectWidth / atlasWidth;                      // proportion of atlas used by cascade
                        float effectiveRes = atlasWidth * atlasScale;                   // effective resolution for cascade
                        float texelSizeWorld = (2.0f * extents[i]) / effectiveRes;      // world units per texel
                        m_matrix_view[i].M41 = MathEx.Round(m_matrix_view[i].M41 / texelSizeWorld) * texelSizeWorld; // snap X
                        m_matrix_view[i].M42 = MathEx.Round(m_matrix_view[i].M42 / texelSizeWorld) * texelSizeWorld; // snap Y
                        // z-translation (M43) remains unchanged for orthographic projection
                    }
                }
            }
            break;
            case LightType.Point:
            {
                // +X (right)
                m_matrix_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Right, MathEx.Vector3.Up);
                // -X (left)
                m_matrix_view[1] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Left, MathEx.Vector3.Up);
                // +Y (up)
                m_matrix_view[2] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Up, MathEx.Vector3.Backward);
                // -Y (down)
                m_matrix_view[3] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Down, MathEx.Vector3.Forward);
                // +Z (forward)
                m_matrix_view[4] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Forward, MathEx.Vector3.Up);
                // -Z (backward)
                m_matrix_view[5] = Matrix4x4.CreateLookAtLeftHanded(position, position + MathEx.Vector3.Backward, MathEx.Vector3.Up);
            }
            break;
            case LightType.Spot:
            case LightType.Area:
            {
                // the entity's own up so a forward of world up or down does not collapse the cross product
                m_matrix_view[0] = Matrix4x4.CreateLookToLeftHanded(position, GetEntity().GetForward(), GetEntity().GetUp());
            }
            break;
            default:
            break;
        }
    }
    private void UpdateProjectionMatrix()
    {
        switch (m_light_type)
        {
            case LightType.Directional:
            {
                // near cascade (tight, camera following)
                m_matrix_projection[0] = Matrix4x4Extensions.CreateOrthoOffCenterLH(
                        -cascade_near_extent, cascade_near_extent,
                        -cascade_near_extent, cascade_near_extent,
                        cascade_depth, 0.0f);

                // far cascade (camera following, fixed size, bigger than near cascade)
                m_matrix_projection[1] = Matrix4x4Extensions.CreateOrthoOffCenterLH(
                        -cascade_far_extent, cascade_far_extent,
                        -cascade_far_extent, cascade_far_extent,
                        cascade_depth, 0.0f);

                m_frustums[0] = new Frustum(m_matrix_view[0], m_matrix_projection[0]);
                m_frustums[1] = new Frustum(m_matrix_view[1], m_matrix_projection[1]);
            }
            break;
            case LightType.Area:
            {
                // wide perspective from the rectangle center (Light.cpp:1022-1029)
                m_matrix_projection[0] = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(MathEx.DegToRad * 120.0f, 1.0f, m_range, 0.05f);
                m_frustums[0] = new Frustum(m_matrix_view[0], m_matrix_projection[0]);
            }
            break;
            default:  // spot/point
            {
                const float aspectRatio = 1.0f;
                float fovYRadians = m_light_type == LightType.Spot ? m_angle_rad * 2.0f : MathEx.Pi / 2f + 0.02f; // small epsilon to hide face seams

                int sliceCount = GetSliceCount();
                for (int i = 0; i < sliceCount; i++)
                {
                    m_matrix_projection[i] = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(fovYRadians, aspectRatio, m_range, 0.05f);
                    m_frustums[i] = new Frustum(m_matrix_view[i], m_matrix_projection[i]);
                }
            }
            break;
        }
    }

    private void UpdateBoundingBox()
    {
        Entity entity = GetEntity();
        Vector3 position = entity.GetPosition();

        switch (m_light_type)
        {
            case LightType.Point:
            {
                float radius = m_range;
                m_bounding_box = new BoundingBox(
                    position - new Vector3(radius),
                    position + new Vector3(radius));
            }
            break;
            case LightType.Spot:
            {
                float opposite  = m_range * MathEx.Tan(m_angle_rad);
                Vector3 tip     = position;
                Vector3 center  = tip + entity.GetForward()     * m_range;
                Vector3 up      = center + entity.GetUp()       * opposite;
                Vector3 down    = center + entity.GetDown()     * opposite;
                Vector3 right   = center + entity.GetRight()    * opposite;
                Vector3 left    = center + entity.GetLeft()     * opposite;

                Vector3 min = tip;
                Vector3 max = tip;

                void Expand(Vector3 p)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                Expand(center);
                Expand(up);
                Expand(down);
                Expand(right);
                Expand(left);

                m_bounding_box = new BoundingBox(min, max);
            }
            break;
            case LightType.Area:
            {
                float halfWidth     = m_area_width  * 0.5f;
                float halfHeight    = m_area_height * 0.5f;
                Vector3 right       = entity.GetRight();
                Vector3 up          = entity.GetUp();
                Vector3 forward     = entity.GetForward();

                Vector3 min = position;
                Vector3 max = position;

                void Expand(Vector3 p)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                // near end: the area light rectangle itself
                Expand(position + right * halfWidth + up * halfHeight);
                Expand(position - right * halfWidth + up * halfHeight);
                Expand(position + right * halfWidth - up * halfHeight);
                Expand(position - right * halfWidth - up * halfHeight);

                // far end: expand laterally by range to cover the hemispheric spread
                Vector3 farCenter = position + forward * m_range;
                Expand(farCenter + right * m_range + up * m_range);
                Expand(farCenter - right * m_range + up * m_range);
                Expand(farCenter + right * m_range - up * m_range);
                Expand(farCenter - right * m_range - up * m_range);

                m_bounding_box = new BoundingBox(min, max);
            }
            break;
            default: // directional
                m_bounding_box = BoundingBox.Infinite;
            break;
        }
    }

    // color
    public void SetTemperature(float temperature_kelvin)
    {
        m_temperature_kelvin = temperature_kelvin;
        m_color_rgb = new Color(temperature_kelvin);
    }
    public float GetTemperature() { return m_temperature_kelvin; }
    public void SetColor(Color rgb)
    {
        m_color_rgb = rgb;

        m_temperature_kelvin = rgb switch
        {
            _ when rgb == Color.LightSkyClear => 15000.0f,
            _ when rgb == Color.LightSkyDaylightOvercast => 6500.0f,
            _ when rgb == Color.LightSkyMoonlight => 4000.0f,
            _ when rgb == Color.LightSkySunrise => 2000.0f,
            _ when rgb == Color.LightCandleFlame => 1850.0f,
            _ when rgb == Color.LightDirectSunlight => 5778.0f,
            _ when rgb == Color.LightDigitalDisplay => 6500.0f,
            _ when rgb == Color.LightFluorescentTubeLight => 5000.0f,
            _ when rgb == Color.LightKeroseneLamp => 1850.0f,
            _ when rgb == Color.LightLightBulb => 2700.0f,
            _ when rgb == Color.LightPhotoFlash => 5500.0f,
            _ => m_temperature_kelvin // unknown color – keep previous temperature
        };
    }
    public Color GetColor() { return m_color_rgb; }

    // intensity: lux for directional lights, lumens (flux) for the rest (Light.cpp:566-660)
    public void SetIntensity(float photometric_intensity)
    {
        bool update_range = IsSensibleRange(m_range, m_light_type, m_intensity_lumens_lux, m_angle_rad);
        m_intensity_lumens_lux = photometric_intensity;
        m_intensity = LightIntensity.custom;
        if (update_range)
            SetRange(GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad));
    }
    public void SetIntensity(LightIntensity intensity)
    {
        if (intensity == LightIntensity.custom)
        {
            m_intensity = intensity;
            return;
        }
        bool update_range = IsSensibleRange(m_range, m_light_type, m_intensity_lumens_lux, m_angle_rad);
        m_intensity = intensity;
        m_intensity_lumens_lux = intensity switch
        {
            LightIntensity.bulb_stadium    => 200000.0f,
            LightIntensity.bulb_500_watt   => 8500.0f,
            LightIntensity.bulb_150_watt   => 2600.0f,
            LightIntensity.bulb_100_watt   => 1600.0f,
            LightIntensity.bulb_60_watt    => 800.0f,
            LightIntensity.bulb_25_watt    => 200.0f,
            LightIntensity.bulb_flashlight => 100.0f,
            _                              => 0.0f, // black hole
        };
        if (update_range)
            SetRange(GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad));
    }
    public float GetIntensityPhotometric() { return m_intensity_lumens_lux; }
    public float GetIntensityLumens() { return m_intensity_lumens_lux; } // legacy alias
    public LightIntensity GetIntensity() { return m_intensity; }
    public LightIntensityUnit GetIntensityUnit() => m_light_type == LightType.Directional ? LightIntensityUnit.Lux : LightIntensityUnit.Lumens;

    // on axis illuminance in lux at a distance in meters, what a surface facing the light receives (Light.cpp:632)
    public float GetIlluminanceAt(float distance)
    {
        float flux        = MathF.Max(m_intensity_lumens_lux, 0.0f);
        float distance_sq = MathF.Max(distance * distance, 0.0001f);
        return m_light_type switch
        {
            LightType.Point => flux / (4.0f * MathF.PI * distance_sq),
            LightType.Spot  => flux / Lighting.SpotSolidAngle(m_angle_rad) / distance_sq,
            LightType.Area  => flux / (MathF.PI * distance_sq),
            _               => m_intensity_lumens_lux,
        };
    }

    // the gpu scale (Light.cpp:701): lux/683 for directional, candela/683 for point/spot, nits/683 for area
    public float GetIntensityRadiometric()
    {
        float photometric = MathF.Max(m_intensity_lumens_lux, 0.0f);
        float radiant     = photometric / Lighting.LuminousEfficacy;
        return m_light_type switch
        {
            LightType.Directional => radiant,                                    // illuminance in lux
            LightType.Point       => radiant / (4.0f * MathF.PI),                // isotropic over the sphere
            LightType.Spot        => radiant / Lighting.SpotSolidAngle(m_angle_rad), // the whole beam with its falloff
            LightType.Area        => radiant / (MathF.PI * MathF.Max(m_area_width * m_area_height, 0.0001f)),
            _                     => radiant / (4.0f * MathF.PI),
        };
    }
    public float GetIntensityWatt() { return GetIntensityRadiometric(); } // legacy alias, not watts

    // Light.cpp:503: local lights use the colour's chromaticity at unit luminance (intensity owns the lumens); the
    // sun's colour is the atmosphere's transmittance towards it from the camera's altitude
    public Color GetColorEffective()
    {
        if (m_light_type != LightType.Directional)
        {
            float r = MathF.Max(m_color_rgb.R, 0.0f), g = MathF.Max(m_color_rgb.G, 0.0f), b = MathF.Max(m_color_rgb.B, 0.0f);
            float luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            return luminance > 0.0f ? new Color(r / luminance, g / luminance, b / luminance, 1.0f) : Color.StandardBlack;
        }

        // atmosphere constants mirrored from skysphere.hlsl, the atmosphere is the single ground truth for sun colour
        const float earth_radius       = 6360e3f;
        const float atmosphere_radius  = 6460e3f;
        var rayleigh_scatter           = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f);
        const float rayleigh_height    = 8000.0f;
        const float mie_extinction     = 4.4e-6f;
        const float mie_height         = 1200.0f;
        var ozone_absorption           = new Vector3(0.65e-6f, 1.881e-6f, 0.085e-6f);
        const float ozone_center       = 25000.0f;
        const float ozone_width        = 15000.0f;
        const int sample_count         = 32;

        float altitude = 1.0f;
        if (World.GetCamera() is Camera camera && camera.GetEntity() is Entity camera_entity)
            altitude = MathF.Max(camera_entity.GetPosition().Y, 1.0f);

        Vector3 sun_direction = -GetEntity()!.GetForward();
        var position          = new Vector3(0.0f, earth_radius + altitude, 0.0f);
        float bb      = Vector3.Dot(position, sun_direction);
        float c_earth = Vector3.Dot(position, position) - earth_radius * earth_radius;
        float c_atmo  = Vector3.Dot(position, position) - atmosphere_radius * atmosphere_radius;

        // the planet occludes the sun below the horizon
        float disc_earth = bb * bb - c_earth;
        if (disc_earth > 0.0f && -bb - MathF.Sqrt(disc_earth) > 0.0f)
            return new Color(0.0f, 0.0f, 0.0f, 1.0f);

        float t_max = -bb + MathF.Sqrt(MathF.Max(bb * bb - c_atmo, 0.0f));
        float dt    = t_max / sample_count;
        Vector3 optical_depth = Vector3.Zero;
        for (int i = 0; i < sample_count; i++)
        {
            Vector3 sample_pos     = position + sun_direction * ((i + 0.5f) * dt);
            float height           = sample_pos.Length() - earth_radius;
            float rayleigh_density = MathF.Exp(-height / rayleigh_height);
            float mie_density      = MathF.Exp(-height / mie_height);
            float ozone_density    = MathF.Max(0.0f, 1.0f - MathF.Abs(height - ozone_center) / ozone_width);
            optical_depth += (rayleigh_scatter * rayleigh_density + new Vector3(mie_extinction * mie_density) + ozone_absorption * ozone_density) * dt;
        }
        return new Color(MathF.Exp(-optical_depth.X), MathF.Exp(-optical_depth.Y), MathF.Exp(-optical_depth.Z), 1.0f);
    }

    // preset
    public void SetPreset(LightPreset preset)
    {
        m_preset = preset;

        float time_of_day = 0.0f;
        float temperature = 0.0f;
        float intensity = 0.0f;
        float yaw_degrees = 0.0f; // horizontal rotation around Y axis

        switch (preset)
        {
            case LightPreset.dawn:
                // sunrise - early morning with warm orange glow
                time_of_day = 0.25f; // 6:00 AM
                temperature = 2500.0f; // warm sunrise orange
                intensity = 500.0f; // lux - dawn light
            break;
            case LightPreset.day:
                // bright midday sun - direct sunlight at peak intensity
                time_of_day = 0.5f; // 12:00 PM
                temperature = 5778.0f; // sun color temperature
                intensity = 100000.0f; // lux - direct sunlight
            break;
            case LightPreset.dusk:
                // sunset - evening with warm golden tones
                time_of_day = 0.69f; // 4:30 PM
                temperature = 3200.0f; // warm golden
                intensity = 10000.0f; // lux - golden hour light
            break;
            case LightPreset.night:
                // nighttime with soft moonlight
                time_of_day = 0.875f; // 9:00 PM
                temperature = 4100.0f; // moonlight color
                intensity = 0.3f; // lux - full moon
            break;
            case LightPreset.david_lynch:
                // dreamy sunset - that david lynch/twin peaks vibe
                // warm orange/pink colors, sun near horizon, dreamlike beauty
                time_of_day = 0.74f; // sun near horizon for actual sunset colors
                temperature = 2200.0f; // deep warm orange/pink
                intensity = 5000.0f; // lux - soft sunset light
                yaw_degrees = 125.0f; // rotate to avoid mountain
            break;
            case LightPreset.custom:
                // do nothing, keep current settings
                return;
        }

        // set time of day
        World.SetTimeOfDay(time_of_day);

        // set light properties
        SetTemperature(temperature);
        SetIntensity(intensity);

        // set rotation based on time of day (only for directional lights)
        if (m_light_type == LightType.Directional)
        {
            // elevation from time of day
            float elevation_rad = (time_of_day * 360.0f - 90.0f) * MathEx.DegToRad;
            Quaternion elevation = QuaternionExtensions.FromAxisAngle(MathEx.Vector3.Right, elevation_rad);

            // horizontal rotation (yaw)
            Quaternion yaw = QuaternionExtensions.FromAxisAngle(MathEx.Vector3.Up, yaw_degrees * MathEx.DegToRad);

            // combine: yaw first, then elevation
            GetEntity().SetRotation(yaw * elevation);
            UpdateMatrices();
        }
    }
    public LightPreset GetPreset() { return m_preset; }

    // Light.cpp:680-694: moves the sun to a time of day (0 midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset) and sets the
    // world's clock; a manual move drops the preset.
    // Deviation: spartan points the sun along the ephemeris (World::GetEnvironment().sun, astronomy library, not ported)
    // and ignores yaw_degrees; Phalanx uses SetPreset's orbit, elevation (time_of_day * 360 - 90) degrees about the
    // right axis, so the sun rises towards -z, culminates overhead and sets towards +z, then turns it by yaw_degrees about up.
    public void SetTimeOfDay(float time_of_day, float yaw_degrees = 0.0f)
    {
        m_preset = LightPreset.custom;

        World.SetTimeOfDay(time_of_day);

        if (m_light_type == LightType.Directional)
        {
            float elevation_rad  = (World.GetTimeOfDay(false) * 360.0f - 90.0f) * MathEx.DegToRad;
            Quaternion elevation = QuaternionExtensions.FromAxisAngle(MathEx.Vector3.Right, elevation_rad);
            Quaternion yaw       = QuaternionExtensions.FromAxisAngle(MathEx.Vector3.Up, yaw_degrees * MathEx.DegToRad);
            GetEntity()!.SetRotation(yaw * elevation);
            UpdateMatrices();
        }
    }

    // range
    public void SetRange(float range)
    {
        range = MathEx.Clamp(range, 0.0f, float.MaxValue);

        if (range == m_range)
            return;

        m_range = range;
        UpdateMatrices();
    }
    public float GetRange() { return m_range; }

    // angle
    public void SetAngle(float angle)
    {
        angle = Lighting.SpotHalfAngle(angle);
        if (angle == m_angle_rad)
            return;

        bool update_range = IsSensibleRange(m_range, m_light_type, m_intensity_lumens_lux, m_angle_rad);
        m_angle_rad = angle;
        if (update_range)
            m_range = GetSensibleRange(m_light_type, m_intensity_lumens_lux, m_angle_rad);
        UpdateMatrices();
    }
    public float GetAngle() { return m_angle_rad; }

    // area light dimensions
    public void SetAreaWidth(float width)
    {
        width = MathEx.Clamp(width, 0.01f, 100.0f);
        if (width == m_area_width)
            return;

        m_area_width = width;
        UpdateMatrices();
    }
    public float GetAreaWidth() { return m_area_width; }
    public void SetAreaHeight(float height)
    {
        height = MathEx.Clamp(height, 0.01f, 100.0f);
        if (height == m_area_height)
            return;

        m_area_height = height;
        UpdateMatrices();
    }
    public float GetAreaHeight() { return m_area_height; }

    // matrices
    public Matrix4x4 GetViewProjectionMatrix(uint index) { return m_matrix_view[index] * m_matrix_projection[index]; } // Light.h:183

    // frustum
    public bool IsInViewFrustum(Render renderable, uint array_index)
    {
        BoundingBox bounding_box = renderable.GetBoundingBox();
        Vector3 center = bounding_box.GetCenter();
        Vector3 extents = bounding_box.GetExtents();
        bool ignore_depth = m_light_type == LightType.Directional; // orthographic

        return m_frustums[array_index].IsVisible(center, extents, ignore_depth);
    }

    // index
    public void SetIndex(uint index) { m_index = index; }
    public uint GetIndex() { return m_index; }

    // screen space shadows slice index
    // (Light.h:194-195) its own field, it used to alias m_index, so writing the slice overwrote the light's table index
    public void SetScreenSpaceShadowsSliceIndex(uint index) { m_screen_space_shadows_slice_index = index; }
    public uint GetScreenSpaceShadowsSliceIndex() { return m_screen_space_shadows_slice_index; }

    // draw distance
    public void SetDrawDistance(float distance) { m_draw_distance = distance; }
    public float GetDrawDistance() { return m_draw_distance; }

    // shadow distance (Light.h:202-203) and IsShadowEffective (Light.cpp:1158, is_within_distance :1133)
    public void SetShadowDistance(float distance) { m_distance_shadows = distance; }
    public float GetShadowDistance() { return m_distance_shadows; }
    public bool IsShadowEffective()
    {
        if (!GetFlag(LightFlags.Shadows))
            return false;
        if (m_light_type == LightType.Directional)
            return true;
        Camera? camera = World.GetCamera();
        if (camera is null)
            return false;
        return Vector3.DistanceSquared(GetEntity().GetPosition(), camera.GetEntity().GetPosition()) <= m_distance_shadows * m_distance_shadows;
    }

    // misc
    public bool NeedsSkysphereUpdate()
    {
        if (m_light_type != LightType.Directional)
            return false;

        Quaternion last_rotation = Quaternion.Identity;
        Color last_color_rgb = Color.StandardBlack;
        float last_intensity_lumens_lux = float.MaxValue;

        Quaternion current_rotation = GetEntity() != null ? GetEntity().GetRotation() : Quaternion.Identity;

        bool rotation_changed = current_rotation != last_rotation;
        bool color_changed = m_color_rgb != last_color_rgb;
        bool intensity_changed = MathEx.Abs(m_intensity_lumens_lux - last_intensity_lumens_lux) > 0.01f;

        if (rotation_changed || color_changed || intensity_changed)
        {
            last_rotation = current_rotation;
            last_color_rgb = m_color_rgb;
            last_intensity_lumens_lux = m_intensity_lumens_lux;
            return true;
        }

        return false;
    }
    public int GetSliceCount()
    {
        if (m_light_type == LightType.Directional)
            return 2;
        if (m_light_type == LightType.Point)
            return 6;
        return 1; // spot and area lights use a single slice
    }

    // atlas
    public Vector2 GetAtlasOffset(uint slice) { return m_atlas_offsets[slice]; }
    public Vector2 GetAtlasScale(uint slice) { return m_atlas_scales[slice]; }
    public Rectangle GetAtlasRectangle(uint slice) { return m_atlas_rectangles[slice]; }
    public void SetAtlasRectangle(uint slice, Rectangle rectangle)
    {
        m_atlas_rectangles[slice] = rectangle;
        float atlas_w = (float)RenderView.GetRenderTarget(Renderer_RenderTarget.shadow_atlas).GetWidth();
        float atlas_h = (float)RenderView.GetRenderTarget(Renderer_RenderTarget.shadow_atlas).GetHeight();
        m_atlas_offsets[slice] = new Vector2(rectangle.X / atlas_w, rectangle.Y / atlas_h);
        m_atlas_scales[slice] = new Vector2(rectangle.Width / atlas_w, rectangle.Height / atlas_h);
    }
    public void ClearAtlasRectangles()
    {
        Array.Fill(m_atlas_rectangles, Rectangle.Zero);
        Array.Fill(m_atlas_offsets, Vector2.Zero);
        Array.Fill(m_atlas_scales, Vector2.Zero);
    }
    public BoundingBox GetBoundingBox() { return m_bounding_box; }
}