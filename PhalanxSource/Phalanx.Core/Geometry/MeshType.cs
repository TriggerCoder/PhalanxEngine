using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Geometry;

// moved out of Phalanx.Rendering/Geometry/Mesh.cs: World components (Render) reference it and Core cannot see Rendering
public enum MeshType
{
    Cube,
    Quad,
    Sphere,
    Cylinder,
    Cone,
    Max
}
