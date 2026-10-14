using System.Numerics;
using Phalanx;
using Xunit;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Tests;

// the type shares its name with the Phalanx.World namespace, which wins name lookup inside Phalanx.*
using World = Phalanx.World.World;

[Collection("World")] // the hierarchy tests set the main thread, so they share the World collection
public class QuaternionTests
{
    // scalar transcription of Spartan's SIMD Quaternion::Multiply (source/math/Quaternion.h):
    // result = a.w*b + a.x*(b.w, b.z,-b.y,-b.x)... written out lane by lane from the shuffles/sign masks
    private static Quaternion SpartanMultiply(Quaternion a, Quaternion b)
    {
        // b_wzyx = ( b.w, -b.z,  b.y, -b.x)
        // b_zwxy = ( b.z,  b.w, -b.x, -b.y)
        // b_yxwz = (-b.y,  b.x,  b.w, -b.z)
        // r = a.x*b_wzyx + a.w*b + a.z*b_yxwz + a.y*b_zwxy
        return new Quaternion(
            a.X * b.W    + a.W * b.X + a.Z * -b.Y + a.Y * b.Z,
            a.X * -b.Z   + a.W * b.Y + a.Z * b.X  + a.Y * b.W,
            a.X * b.Y    + a.W * b.Z + a.Z * b.W  + a.Y * -b.X,
            a.X * -b.X   + a.W * b.W + a.Z * -b.Z + a.Y * -b.Y);
    }

    private static void AssertClose(Quaternion e, Quaternion a, float eps = 1e-5f)
    {
        Assert.True(MathF.Abs(e.X - a.X) < eps && MathF.Abs(e.Y - a.Y) < eps && MathF.Abs(e.Z - a.Z) < eps && MathF.Abs(e.W - a.W) < eps,
            $"expected {e} got {a}");
    }

    [Fact]
    public void System_Numerics_operator_multiply_matches_Spartan_Multiply_order()
    {
        Quaternion a = Quaternion.CreateFromYawPitchRoll(0.3f, -1.1f, 0.7f);
        Quaternion b = Quaternion.CreateFromYawPitchRoll(-2.0f, 0.4f, 1.9f);
        AssertClose(SpartanMultiply(a, b), a * b);
        // and it is not commutative for these inputs, so the order actually matters
        Quaternion d = (a * b) - (b * a);
        Assert.True(d.Length() > 1e-3f);
    }

    [Fact]
    public void Hierarchy_rotation_is_root_times_parent_times_local()
    {
        World.SetMainThread();
        Quaternion qr = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f);
        Quaternion qp = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f);
        Quaternion qc = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.4f);

        Entity root = new Entity(), parent = new Entity(), child = new Entity();
        parent.SetParent(root);
        child.SetParent(parent);
        root.SetRotationLocal(qr);
        parent.SetRotationLocal(qp);
        child.SetRotationLocal(qc);

        AssertClose(qr * qp * qc, child.GetHierarchyRotation());

        // SetRotation(world) must invert the same composition
        Quaternion world = Quaternion.CreateFromYawPitchRoll(1.2f, 0.2f, -0.5f);
        child.SetRotation(world);
        AssertClose(world, child.GetHierarchyRotation(), 1e-4f);
    }
}
