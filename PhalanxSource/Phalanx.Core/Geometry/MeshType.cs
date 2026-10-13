namespace Phalanx;

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
