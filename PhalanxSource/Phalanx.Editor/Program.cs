using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;
using Phalanx.Rendering;
using Phalanx.Assets.Import;

namespace Phalanx.Editor;

// The stock editor executable. A user's game exe replaces it (architecture §3, §8).
// TODO(port): roadmap steps C and I (GameHost, ImGui editor shell). For now it only starts the engine flags.
public static class Program
{
    public static int Main(string[] args)
    {
        Engine.Initialize();
        Log.LogInfo("Phalanx editor: nothing to show yet (RHI pending)");
        return 0;
    }
}
