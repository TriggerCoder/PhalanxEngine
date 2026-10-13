using System.Numerics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class PlaneExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Plane Normalize(this Plane p) { return Plane.Normalize(p); }
}