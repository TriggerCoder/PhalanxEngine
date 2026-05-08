using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Phalanx;

public struct ShadowSlice
{
    public Light? light;
    public uint slice_index;
    public uint res;
    public Rectangle rect;
}

public struct PersistentLine
{
    public Vector3 from;
    public Vector3 to;
    public Color color_from;
    public Color color_to;
    public double expire_time;
}
public static class Renderer
{

}