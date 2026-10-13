using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx;

// thrown by PLXAssert / PLXAssertMsg; mirrors SP_ASSERT, which aborts in Spartan
public sealed class PhalanxAssertException : Exception
{
    public PhalanxAssertException(string message) : base(message) { }
}

public static class Helpers
{
    public static void PLXAssertMsg(bool condition, string expression,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
            Fail(expression, file, line);
    }

    public static void PLXAssert(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!condition)
            Fail(expression ?? "assertion", file, line);
    }

    private static void Fail(string expression, string file, int line)
    {
        string message = $"Assertion failed: {expression} ({System.IO.Path.GetFileName(file)}:{line})";
        Log.SetLogToFile(true);
        Log.LogError(message);
        if (Debugger.IsAttached)
            Debugger.Break();
        throw new PhalanxAssertException(message);
    }
}
