using System.Numerics;
using Phalanx;
using Xunit;

namespace Phalanx.Tests;

public class ProjectionTests
{
    private const float Eps = 1e-5f;

    private static void AssertEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < Eps, $"m[{r},{c}] expected {expected[r, c]} got {actual[r, c]}");
    }

    private static float Depth(Matrix4x4 projection, float view_z)
    {
        Vector4 clip = Vector4.Transform(new Vector4(0.0f, 0.0f, view_z, 1.0f), projection);
        return clip.Z / clip.W;
    }

    [Fact]
    public void Perspective_matches_System_Numerics_for_forward_z()
    {
        float fov = MathF.PI / 3.0f;
        AssertEqual(Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(fov, 16.0f / 9.0f, 0.1f, 1000.0f),
                    Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(fov, 16.0f / 9.0f, 0.1f, 1000.0f));
    }

    [Fact]
    public void Perspective_reverse_z_does_not_throw_and_maps_near_to_1_far_to_0()
    {
        // Spartan: Camera.cpp ComputeProjection(m_far_plane, m_near_plane), Light.cpp (range, 0.05f)
        Matrix4x4 p = Matrix4x4Extensions.CreatePerspectiveFieldOfViewLH(MathF.PI / 2.0f, 1.0f, 1000.0f, 0.1f);
        Assert.Equal(1.0f, Depth(p, 0.1f), 4);
        Assert.Equal(0.0f, Depth(p, 1000.0f), 4);
        Assert.Equal(1.0f, p.M34); // w = view z (row-vector, left-handed)
    }

    [Fact]
    public void Orthographic_matches_System_Numerics_and_supports_reverse_z()
    {
        AssertEqual(Matrix4x4.CreateOrthographicLeftHanded(1920, 1080, 0.1f, 100.0f),
                    Matrix4x4Extensions.CreateOrthographicLH(1920, 1080, 0.1f, 100.0f));
        Matrix4x4 rz = Matrix4x4Extensions.CreateOrthographicLH(1920, 1080, 100.0f, 0.1f);
        Assert.Equal(1.0f, Depth(rz, 0.1f), 4);
        Assert.Equal(0.0f, Depth(rz, 100.0f), 4);
    }

    [Fact]
    public void OrthoOffCenter_matches_System_Numerics_and_supports_reverse_z()
    {
        AssertEqual(Matrix4x4.CreateOrthographicOffCenterLeftHanded(-10, 30, -5, 15, 0.5f, 200.0f),
                    Matrix4x4Extensions.CreateOrthoOffCenterLH(-10, 30, -5, 15, 0.5f, 200.0f));
        Matrix4x4 rz = Matrix4x4Extensions.CreateOrthoOffCenterLH(-10, 30, -5, 15, 200.0f, 0.5f);
        Assert.Equal(1.0f, Depth(rz, 0.5f), 4);
        Assert.Equal(0.0f, Depth(rz, 200.0f), 4);
    }

    [Fact]
    public void Transposed_returns_transpose()
    {
        Matrix4x4 m = new Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        Assert.Equal(Matrix4x4.Transpose(m), m.Transposed());
    }
}
