// Port of SpartanEngine source/rhi/RHI_Viewport.h @ 4c07baa
namespace Phalanx.Rendering.RHI;

internal readonly record struct RHI_Viewport(float x = 0.0f, float y = 0.0f, float width = 0.0f, float height = 0.0f, float depth_min = 0.0f, float depth_max = 1.0f)
{
    public float GetAspectRatio() => width / height;
}
