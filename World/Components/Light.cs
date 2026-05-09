using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
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
    // directional matrix parameters
    const float cascade_near_extent = 20.0f;
    const float cascade_far_extent = 300.0f;
    const float cascade_depth = 1000.0f;
    const float cascade_far_max_extent = float.MaxValue;
    private LightIntensity m_intensity = LightIntensity.bulb_500_watt;
    private float m_intensity_lumens_lux = 2600.0f;
    private LightFlags m_flags = 0;
    private LightType m_light_type = LightType.Max;
    private Color m_color_rgb = Color.StandardBlack;
    private float m_temperature_kelvin = 0.0f;
    private LightPreset m_preset = LightPreset.custom;
    private float m_range = 32.0f;
    private float m_angle_rad = Math.DegToRad * 30.0f;
    private float m_area_width = 1.0f;  // area light width in meters
    private float m_area_height = 1.0f;  // area light height in meters
    private uint m_index = 0;
    private BoundingBox m_bounding_box = BoundingBox.Zero;
    private Vector3 m_far_cascade_min = Vector3.Zero;
    private Vector3 m_far_cascade_max = Vector3.Zero;
    private bool m_is_active_previous_frame = false;
    private float m_draw_distance = 512.0f; // max distance at which light will affect objects (meters)

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
        SetRange(GetSensibleRange(m_light_type));
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
        SetRange(GetSensibleRange(type));   // TODO: port this helper the same way as GetSensibleColor
        UpdateMatrices();
    }
    float GetSensibleRange(LightType type)
    {
        switch (type)
        {
            case LightType.Directional:
                return float.MaxValue;
            case LightType.Point:
            case LightType.Spot:
                return 15.0f;
            case LightType.Area:
                return 20.0f;
            default:
                return 0.0f;
        }
    }
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
    private void UpdateMatrices()
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

                // both cascades follow the camera
                Vector3 cameraPos = camera.GetEntity().GetPosition();
                Vector3 lightForward = GetEntity().GetForward();
                position = cameraPos - lightForward * cascade_depth * 0.5f;
                m_matrix_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, cameraPos, Math.Vector3.Up);
                m_matrix_view[1] = m_matrix_view[0];

                // move the light in world units per texel to avoid shimmering
                {
                    // compute shadow extents (both fixed sizes)
                    float[] extents = { cascade_near_extent, cascade_far_extent };
                    float atlasWidth = (float)Renderer.GetRenderTarget(RendererRenderTarget.ShadowAtlas).GetWidth();

                    for (int i = 0; i < 2; i++)
                    {
                        float rectWidth = m_atlas_rectangles[i].Width;                   // cascade rectangle width in atlas
                        float atlasScale = rectWidth / atlasWidth;                      // proportion of atlas used by cascade
                        float effectiveRes = atlasWidth * atlasScale;                   // effective resolution for cascade
                        float texelSizeWorld = (2.0f * extents[i]) / effectiveRes;      // world units per texel
                        m_matrix_view[i].M41 = Math.Round(m_matrix_view[i].M41 / texelSizeWorld) * texelSizeWorld; // snap X
                        m_matrix_view[i].M42 = Math.Round(m_matrix_view[i].M42 / texelSizeWorld) * texelSizeWorld; // snap Y
                        // z-translation (M43) remains unchanged for orthographic projection
                    }
                }
            }
            break;
            case LightType.Point:
            {
                // +X (right)
                m_matrix_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Right, Math.Vector3.Up);
                // -X (left)
                m_matrix_view[1] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Left, Math.Vector3.Up);
                // +Y (up)
                m_matrix_view[2] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Up, Math.Vector3.Backward);
                // -Y (down)
                m_matrix_view[3] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Down, Math.Vector3.Forward);
                // +Z (forward)
                m_matrix_view[4] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Forward, Math.Vector3.Up);
                // -Z (backward)
                m_matrix_view[5] = Matrix4x4.CreateLookAtLeftHanded(position, position + Math.Vector3.Backward, Math.Vector3.Up);
            }
            break;
            case LightType.Spot:
            case LightType.Area:
            {
                Vector3 forward = GetEntity().GetForward();
                m_matrix_view[0] = Matrix4x4.CreateLookAtLeftHanded(position, position + forward, Math.Vector3.Up);
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
                m_matrix_projection[0] = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
                        -cascade_near_extent, cascade_near_extent,
                        -cascade_near_extent, cascade_near_extent,
                        cascade_depth, 0.0f);

                // far cascade (camera following, fixed size, bigger than near cascade)
                m_matrix_projection[1] = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
                        -cascade_far_extent, cascade_far_extent,
                        -cascade_far_extent, cascade_far_extent,
                        cascade_depth, 0.0f);

                m_frustums[0] = new Frustum(m_matrix_view[0], m_matrix_projection[0]);
                m_frustums[1] = new Frustum(m_matrix_view[1], m_matrix_projection[1]);
            }
            break;
            case LightType.Area:
            {
                // area lights use orthographic projection based on their dimensions
                float halfWidth = m_area_width * 0.5f;
                float halfHeight = m_area_height * 0.5f;

                m_matrix_projection[0] = Matrix4x4.CreateOrthographicOffCenterLeftHanded(
                        -halfWidth, halfWidth,
                        -halfHeight, halfHeight,
                        m_range, 0.05f);

                m_frustums[0] = new Frustum(m_matrix_view[0], m_matrix_projection[0]);
            }
            break;
            default:  // spot/point
            {
                const float aspectRatio = 1.0f;
                float fovYRadians = m_light_type == LightType.Spot ? m_angle_rad * 2.0f : Math.Pi / 2f + 0.02f; // small epsilon to hide face seams

                int sliceCount = GetSliceCount();
                for (int i = 0; i < sliceCount; i++)
                {
                    m_matrix_projection[i] = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fovYRadians, aspectRatio, m_range, 0.05f);
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
                float opposite  = m_range * Math.Tan(m_angle_rad);
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

    // intensity
    public void SetIntensity(float lumens_lux)
    {
        m_intensity_lumens_lux = lumens_lux;
        m_intensity = LightIntensity.custom;
    }
    public void SetIntensity(LightIntensity intensity)
    {
        m_intensity = intensity;

        switch (m_intensity)
        {
            case LightIntensity.bulb_stadium:
                m_intensity_lumens_lux = 200000.0f;
            break;
            case LightIntensity.bulb_500_watt:
                m_intensity_lumens_lux = 8500.0f;
            break;
            case LightIntensity.bulb_150_watt:
                m_intensity_lumens_lux = 2600.0f;
            break;
            case LightIntensity.bulb_100_watt:
                m_intensity_lumens_lux = 1600.0f;
            break;
            case LightIntensity.bulb_60_watt:
                m_intensity_lumens_lux = 800.0f;
            break;
            case LightIntensity.bulb_25_watt:
                m_intensity_lumens_lux = 200.0f;
            break;
            case LightIntensity.bulb_flashlight:
                m_intensity_lumens_lux = 100.0f;
            break;
            default: // black hole
                m_intensity_lumens_lux = 0.0f;
            break;
        }
    }
    public float GetIntensityLumens() { return m_intensity_lumens_lux; }
    public LightIntensity GetIntensity() { return m_intensity; }
    public float GetIntensityWatt()
    {
        // ideal luminous efficacy of monochromatic radiation at 555 nm (lm/w).
        // note: for broad spectrum white light, ~250-400 is more accurate,
        // but 683 is the standard "ideal" definition used in engines like ue5/frostbite
        const float luminous_efficacy = 683.0f;

        // 1. convert photometric (lumens/lux) to radiometric (watts)
        float radiant_flux = m_intensity_lumens_lux / luminous_efficacy;

        if (m_light_type == LightType.Directional)
        {
            // directional: input is lux (lm/m^2), output is irradiance (w/m^2)
            // no solid angle conversion needed
            return radiant_flux;
        }
        else
        {
            // point/spot: input is lumens (lm) -> flux (watts)
            // we need radiant intensity (watts/sr)
            // divide by 4pi to distribute flux over the sphere
            return radiant_flux / (4.0f * 3.14159265359f);
        }
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
            float elevation_rad = (time_of_day * 360.0f - 90.0f) * Math.DegToRad;
            Quaternion elevation = QuaternionExtensions.FromAxisAngle(Math.Vector3.Right, elevation_rad);

            // horizontal rotation (yaw)
            Quaternion yaw = QuaternionExtensions.FromAxisAngle(Math.Vector3.Up, yaw_degrees * Math.DegToRad);

            // combine: yaw first, then elevation
            GetEntity().SetRotation(yaw * elevation);
            UpdateMatrices();
        }
    }
    public LightPreset GetPreset() { return m_preset; }

    // range
    public void SetRange(float range)
    {
        range = Math.Clamp(range, 0.0f, float.MaxValue);

        if (range == m_range)
            return;

        m_range = range;
        UpdateMatrices();
    }
    public float GetRange() { return m_range; }

    // angle
    public void SetAngle(float angle)
    {
        angle = Math.Clamp(angle, 0.0f, Math.Pi2);
        if (angle == m_angle_rad)
            return;

        m_angle_rad = angle;
        UpdateMatrices();
    }
    public float GetAngle() { return m_angle_rad; }

    // area light dimensions
    public void SetAreaWidth(float width)
    {
        width = Math.Clamp(width, 0.01f, 100.0f);
        if (width == m_area_width)
            return;

        m_area_width = width;
        UpdateMatrices();
    }
    public float GetAreaWidth() { return m_area_width; }
    public void SetAreaHeight(float height)
    {
        height = Math.Clamp(height, 0.01f, 100.0f);
        if (height == m_area_height)
            return;

        m_area_height = height;
        UpdateMatrices();
    }
    public float GetAreaHeight() { return m_area_height; }

    // matrices
    public Matrix4x4 GetViewProjectionMatrix(uint index) { return m_matrix_view[index] * m_matrix_view[index]; }

    // frustum
    public bool IsInViewFrustum(Render renderable, uint array_index)
    {
        BoundingBox bounding_box = renderable->GetBoundingBox();
        Vector3 center = bounding_box.GetCenter();
        Vector3 extents = bounding_box.GetExtents();
        bool ignore_depth = m_light_type == LightType.Directional; // orthographic

        return m_frustums[array_index].IsVisible(center, extents, ignore_depth);
    }

    // index
    public void SetIndex(uint index) { m_index = index; }
    public uint GetIndex() { return m_index; }

    // screen space shadows slice index
    public void SetScreenSpaceShadowsSliceIndex(uint index) { m_index = index; }
    public uint GetScreenSpaceShadowsSliceIndex() { return m_index; }

    // draw distance
    public void SetDrawDistance(float distance) { m_draw_distance = distance; }
    public float GetDrawDistance() { return m_draw_distance; }

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
        bool intensity_changed = Math.Abs(m_intensity_lumens_lux - last_intensity_lumens_lux) > 0.01f;

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
        float atlas_w = (float)(Renderer.GetRenderTarget(Renderer_RenderTarget.shadow_atlas)->GetWidth());
        float atlas_h = (float)(Renderer.GetRenderTarget(Renderer_RenderTarget.shadow_atlas)->GetHeight());
        m_atlas_offsets[slice] = new Vector2(rectangle.X / atlas_w, rectangle.y / atlas_h);
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