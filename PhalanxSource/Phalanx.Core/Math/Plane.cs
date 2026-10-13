using System.Numerics;
using System.Runtime.CompilerServices;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.Math;
public static class PlaneExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Plane Normalize(this Plane p) { return Plane.Normalize(p); }
}