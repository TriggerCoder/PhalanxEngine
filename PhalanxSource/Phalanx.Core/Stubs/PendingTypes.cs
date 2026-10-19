// TODO(port): minimal placeholders so the solution compiles while the real modules are ported.
// Each type mirrors only the members already referenced by ported code. Replace with the real
// port of the named SpartanEngine file and delete the stub.
using System.Numerics;
using System.Runtime.InteropServices;
using Phalanx.Math;
using Phalanx.Geometry;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.World;

// TODO(port): source/rhi/RHI_Viewport.h
public struct RHI_Viewport
{
    public float X, Y, Width, Height, DepthMin, DepthMax;
    public RHI_Viewport(float x, float y, float width, float height, float depth_min = 0.0f, float depth_max = 1.0f)
    {
        X = x; Y = y; Width = width; Height = height; DepthMin = depth_min; DepthMax = depth_max;
    }
    public readonly float GetAspectRatio() { return Height != 0.0f ? Width / Height : 1.0f; }
    public static readonly RHI_Viewport Undefined = new RHI_Viewport(0, 0, 0, 0, 0, 0);
}

// TODO(port): source/rendering/Renderer_Definitions.h (only the targets referenced so far)
// the render targets ported so far, in the relative order of spartan's Renderer_RenderTarget (Renderer_Definitions.h:505)
// the targets created so far, in spartan's order (Renderer_Definitions.h:505); the ones in between come with their passes
public enum Renderer_RenderTarget
{
    gbuffer_color, gbuffer_normal, gbuffer_normal_previous, gbuffer_material, gbuffer_emissive, gbuffer_velocity,
    gbuffer_velocity_previous, gbuffer_depth, gbuffer_depth_previous, lut_brdf_specular, lut_atmosphere_transmittance,
    lut_atmosphere_multiscatter, lut_sky_view, frame_render, frame_output, frame_output_2, taau_history,
    dlss_reactivity, light_diffuse, light_specular, sss, skysphere, sky_sh, shadow_atlas, max
}

// TODO(port): source/rhi/RHI_Definitions.h (RHI_CullMode)
public enum RHI_CullMode { Back, Front, None, Max }

// TODO(port): source/rhi/RHI_AccelerationStructure.h
public class RHI_AccelerationStructure : IDisposable { public void Dispose() { } }

// TODO(port): source/animation/Skeleton.h, SkeletalMeshBinding.h, AnimationClip.h
public class Skeleton { }
public class SkeletalMeshBinding { }
public class AnimationClip { }

// TODO(port): the remaining components, see source/world/components/*.h
public class AudioSource    : Component { public AudioSource(Entity e) : base(e) { } }
public class Physics        : Component { public Physics(Entity e) : base(e) { } }
public class SplineFollower : Component { public SplineFollower(Entity e) : base(e) { } }
public class Terrain        : Component { public Terrain(Entity e) : base(e) { } }
public class Volume         : Component { public Volume(Entity e) : base(e) { } }
public class ParticleSystem : Component { public ParticleSystem(Entity e) : base(e) { } }
