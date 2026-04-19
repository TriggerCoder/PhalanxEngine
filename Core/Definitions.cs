using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Phalanx;
public static class Helpers
{
    public static void PLXAssert(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        try
        {
            if (!(condition))
                new ArgumentException(expression);
        }
        catch (Exception ex)
        {
            StackTrace st = new StackTrace(ex, true);
            StackFrame frame = st.GetFrame(1); //last call
            int lineNumber = frame.GetFileLineNumber();
            Log.SetLogToFile(true);
            Debug.Fail("Assertion failed: "+ expression);
        }
    }
}