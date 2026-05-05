using System;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Phalanx;
public static class Engine
{
    private static uint flags = 0;
    public enum EngineMode : uint
    {
        EditorVisible = 1 << 0,
        Playing       = 1 << 1,
        Paused        = 1 << 2
    }

    public static void Initialize()
    {
        SetFlag(EngineMode.EditorVisible, true);
        SetFlag(EngineMode.Playing, true);
    }

    public static void SetFlag(EngineMode flag, bool enabled)
    {
        if (enabled)
            flags |= (uint)flag;
        else
            flags &= ~(uint)flag;
    }
}