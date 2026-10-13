// TODO(port): minimal placeholders so the solution compiles while the real modules are ported.
// Each type mirrors only the members already referenced by ported code. Replace with the real
// port of the named SpartanEngine file and delete the stub.
using System.Numerics;
using System.Runtime.InteropServices;

namespace Phalanx;

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

// TODO(port): source/world/components/Render.h
public class Render : Component
{
    private BoundingBox m_bounding_box;
    private Material? m_material;
    public Render(Entity entity) : base(entity) { }
    public BoundingBox GetBoundingBox() { return m_bounding_box; }
    public int GetIndexCount() { return 0; }
    public int GetVertexCount() { return 0; }
    public void GetGeometry(List<uint> indices, List<RHI_Vertex_PosTexNorTan> vertices) { }
    public Material? GetMaterial() { return m_material; }
    public void SetMaterial(Material? material) { m_material = material; }
    public void SetMesh(MeshType type) { }
    public void SetDefaultMaterial() { }
}

// TODO(port): source/rhi/RHI_Texture.h
public class RHI_Texture
{
    private readonly uint m_width, m_height;
    public RHI_Texture(uint width, uint height) { m_width = width; m_height = height; }
    public uint GetWidth() { return m_width; }
    public uint GetHeight() { return m_height; }
}

// TODO(port): source/rendering/Renderer_Definitions.h (only the targets referenced so far)
public enum Renderer_RenderTarget { shadow_atlas, max }

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
public class Script         : Component { public Script(Entity e) : base(e) { } }
public class ParticleSystem : Component { public ParticleSystem(Entity e) : base(e) { } }
