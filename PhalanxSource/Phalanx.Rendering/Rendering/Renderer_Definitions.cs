// Port of the renderer definitions SpartanEngine source/rendering/Renderer_Definitions.h @ 4c07baa needs so far: the
// constants of the frame loop (:61, :82), Renderer_AntiAliasing_Upsampling (:180), Renderer_BindingsCb (:199),
// Renderer_BindingsSrv (:204, the g-buffer slots), Renderer_Sampler (:643), Renderer_Buffer (:657) and
// Renderer_StandardTexture (:723), complete so the indices match spartan's. Renderer_RenderTarget lives in Phalanx.Core
// (World components read the target sizes) and holds the targets created so far, in spartan's relative order.
namespace Phalanx.Rendering;

internal static class Renderer_Definitions
{
    // matches command list pool size, avoids cpu-gpu memcpy races (Renderer_Definitions.h:82)
    public const uint renderer_draw_data_buffer_count = 4;
    // Renderer_Definitions.h:61
    public const uint renderer_max_draw_calls = 32768;
    // Renderer.cpp:83
    public const byte swap_chain_buffer_count = 2;
    // Renderer::Initialize, lower than output so taau works well (Renderer.cpp:607)
    public const uint renderer_resolution_render_width  = 1920;
    public const uint renderer_resolution_render_height = 1080;
    // Renderer::Tick, nothing renders below this (Renderer.cpp:746)
    public const uint renderer_min_render_dimension = 64;

    // Renderer_Definitions.h:151, total shadow atlas, packed by row of square slices
    public const uint renderer_resolution_shadow_atlas = 8192;
}

internal enum Renderer_BindingsCb : uint { frame }

internal enum Renderer_BindingsUav : uint { tex = 0, tex2 = 1 }

internal enum Renderer_BindingsSrv : uint
{
    // g-buffer
    gbuffer_color = 0, gbuffer_normal = 1, gbuffer_material = 2, gbuffer_velocity = 3, gbuffer_depth = 4,
    tex = 7, tex2 = 8, tex3 = 9, tex4 = 10,
    // spartan binds it by name ("tex_emissive", register t33)
    gbuffer_emissive = 33,
}

// Renderer_Definitions.h:170, r.tonemapping
public enum Renderer_Tonemapping : uint
{
    Aces,
    AgX,
    Reinhard,
    AcesNautilus,
    GranTurismo7,
    Max,
}

public enum Renderer_AntiAliasing_Upsampling : uint
{
    AA_Off_Upscale_Linear,
    AA_Fxaa_Upscale_Linear,
    AA_Taau_Upscale_Taau,
    AA_Xess_Upscale_Xess,
    AA_Dlss_Upscale_Dlss
}

internal enum Renderer_Sampler
{
    Compare_depth, Point_clamp_edge, Point_clamp_border,
    Point_wrap, Bilinear_clamp_edge, Bilinear_clamp_border,
    Bilinear_wrap, Trilinear_clamp, Anisotropic_wrap,
    Max
}

internal enum Renderer_Buffer
{
    ConstantFrame, SpdCounter, SpdCounterCompute, MaterialParameters,
    LightParameters, DummyInstance, AABBs, GeometryInfo,
    IndirectDrawArgs, CpuIndirectDrawArgs, IndirectDrawData, MeshletInstances,
    VisibleTriangles, TriangleDispatchArgs, CullTasks, TreeWindCache,
    SurvivingInstances, InstanceDispatchArgs, Decals, RainOcclusion,
    CarRainDrops, CarRainTexels, DrawData, ClusterLightGrid,
    ClusterLightIndices, ClusterStats, VolumetricLightIndices, OceanHeights,
    OceanWaveBounds, OceanHeightsReadback0, OceanHeightsReadback1, OceanHeightsReadback2,
    OceanHeightsReadback3, EmissiveTriangles, RestirPairing, RadianceCache,
    ParticleBufferA, ParticleCounter, ParticleEmitter, ParticleVolumeDensity,
    ParticleVolumeColor, GrassInstances, GrassLodParameters, GrassCount,
    GrassIndirectArgs, ShadowCasters, ShadowTasks, ShadowSlices,
    ShadowCounts, ShadowArgs, ShadowVisible, DrawLods,
    CullPhase, InstanceVisibility, TessellatedDraws, TessellatedLods,
    TessellatedArgs, Max
}

internal enum Renderer_StandardTexture
{
    Noise_perlin, Noise_blue, Checkerboard, Gizmo_light_directional,
    Gizmo_light_point, Gizmo_light_spot, Gizmo_audio_source, Gizmo_camera,
    Gizmo_particle, Gizmo_physics, Gizmo_volume, Gizmo_script,
    Gizmo_spline, Gizmo_spline_follower, Gizmo_terrain, Gizmo_skid_marks,
    Gizmo_water, Gizmo_traffic, Gizmo_pedestrians, Gizmo_spawn_point,
    Gizmo_car_reset, Gizmo_text_3d, Gizmo_animator, Gizmo_ragdoll,
    Sky_stars, Sky_star_grid, Black, White,
    Max
}
