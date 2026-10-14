// Port of SpartanEngine source/geometry/GeometryGeneration.cpp @ 4c07baa, the standard meshes ported so far:
// generate_cube (:89-153) and generate_quad (:156-173). The other generators come with the meshes that use them.
using System.Numerics;

namespace Phalanx.Geometry;

public static class GeometryGeneration
{
    public static void generate_cube(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        void V(float x, float y, float z, float u, float v, Vector3 n, Vector3 t) => vertices.Add(new RHI_Vertex_PosTexNorTan(new Vector3(x, y, z), new Vector2(u, v), n, t));
        void I(uint a, uint b, uint c) { indices.Add(a); indices.Add(b); indices.Add(c); }

        // front
        V(-0.5f, -0.5f, -0.5f, 0, 1, new(0, 0, -1), new(0, 1, 0));
        V(-0.5f,  0.5f, -0.5f, 0, 0, new(0, 0, -1), new(0, 1, 0));
        V( 0.5f, -0.5f, -0.5f, 1, 1, new(0, 0, -1), new(0, 1, 0));
        V( 0.5f,  0.5f, -0.5f, 1, 0, new(0, 0, -1), new(0, 1, 0));

        // bottom
        V(-0.5f, -0.5f,  0.5f, 0, 1, new(0, -1, 0), new(1, 0, 0));
        V(-0.5f, -0.5f, -0.5f, 0, 0, new(0, -1, 0), new(1, 0, 0));
        V( 0.5f, -0.5f,  0.5f, 1, 1, new(0, -1, 0), new(1, 0, 0));
        V( 0.5f, -0.5f, -0.5f, 1, 0, new(0, -1, 0), new(1, 0, 0));

        // back
        V(-0.5f, -0.5f,  0.5f, 1, 1, new(0, 0, 1), new(0, 1, 0));
        V(-0.5f,  0.5f,  0.5f, 1, 0, new(0, 0, 1), new(0, 1, 0));
        V( 0.5f, -0.5f,  0.5f, 0, 1, new(0, 0, 1), new(0, 1, 0));
        V( 0.5f,  0.5f,  0.5f, 0, 0, new(0, 0, 1), new(0, 1, 0));

        // top
        V(-0.5f,  0.5f,  0.5f, 0, 0, new(0, 1, 0), new(1, 0, 0));
        V(-0.5f,  0.5f, -0.5f, 0, 1, new(0, 1, 0), new(1, 0, 0));
        V( 0.5f,  0.5f,  0.5f, 1, 0, new(0, 1, 0), new(1, 0, 0));
        V( 0.5f,  0.5f, -0.5f, 1, 1, new(0, 1, 0), new(1, 0, 0));

        // left
        V(-0.5f, -0.5f,  0.5f, 0, 1, new(-1, 0, 0), new(0, 1, 0));
        V(-0.5f,  0.5f,  0.5f, 0, 0, new(-1, 0, 0), new(0, 1, 0));
        V(-0.5f, -0.5f, -0.5f, 1, 1, new(-1, 0, 0), new(0, 1, 0));
        V(-0.5f,  0.5f, -0.5f, 1, 0, new(-1, 0, 0), new(0, 1, 0));

        // right
        V( 0.5f, -0.5f,  0.5f, 1, 1, new(1, 0, 0), new(0, 1, 0));
        V( 0.5f,  0.5f,  0.5f, 1, 0, new(1, 0, 0), new(0, 1, 0));
        V( 0.5f, -0.5f, -0.5f, 0, 1, new(1, 0, 0), new(0, 1, 0));
        V( 0.5f,  0.5f, -0.5f, 0, 0, new(1, 0, 0), new(0, 1, 0));

        I(0, 1, 2);    I(2, 1, 3);    // front
        I(4, 5, 6);    I(6, 5, 7);    // bottom
        I(10, 9, 8);   I(11, 9, 10);  // back
        I(14, 13, 12); I(15, 13, 14); // top
        I(16, 17, 18); I(18, 17, 19); // left
        I(22, 21, 20); I(23, 21, 22); // right
    }

    public static void generate_quad(List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices)
    {
        void V(float x, float y, float z, float u, float v) =>
            vertices.Add(new RHI_Vertex_PosTexNorTan(new Vector3(x, y, z), new Vector2(u, v), new Vector3(0, 1, 0), new Vector3(1, 0, 0)));
        V(-0.5f, 0.0f,  0.5f, 0, 0);
        V( 0.5f, 0.0f,  0.5f, 1, 0);
        V(-0.5f, 0.0f, -0.5f, 0, 1);
        V( 0.5f, 0.0f, -0.5f, 1, 1);
        indices.Add(3); indices.Add(2); indices.Add(0);
        indices.Add(3); indices.Add(0); indices.Add(1);
    }
}
