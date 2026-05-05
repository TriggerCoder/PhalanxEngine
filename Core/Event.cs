using System;

namespace Phalanx;
public static class Event
{
    public enum EventType
    {
        // Renderer
        RendererOnInitialized,         // The renderer has been initialized
        RendererOnFirstFrameCompleted, // The renderer has completed the first frame
        RendererOnShutdown,            // The renderer is about to shutdown

        // SDL
        Sdl,                           // An SDL event

        // Window
        WindowResized,                 // The window has been resized
        WindowFullScreenToggled,       // The window has been toggled to full screen

        // Display
        HdrToggled,                    // HDR output has been toggled on or off

        // Max
        Max
    }

    private static readonly Dictionary<ulong, Action<object?>>[] s_eventSubscribers = new Dictionary<ulong, Action<object?>>[(int)EventType.Max];
    private static ulong s_nextSubscriptionId = 1;

    static Event()
    {
        for (int i = 0; i < s_eventSubscribers.Length; i++)
        {
            s_eventSubscribers[i] = new Dictionary<ulong, Action<object?>>();
        }
    }

    public static void Shutdown()
    {
        foreach (var subscribers in s_eventSubscribers)
            subscribers.Clear();
        s_nextSubscriptionId = 1;
    }

    public static ulong Subscribe(EventType eventType, Action<object?> function)
    {
        ulong handle = s_nextSubscriptionId++;
        s_eventSubscribers[(int)eventType][handle] = function;
        return handle;
    }

    public static void Unsubscribe(EventType eventType, ulong handle)
    {
        s_eventSubscribers[(int)eventType].Remove(handle);
    }

    public static void Fire(EventType eventType, object? data = null)
    {
        foreach (var subscriberFunc in s_eventSubscribers[(int)eventType].Values)
            subscriberFunc(data);
    }
}