// The Script slot of an entity (spartan: source/world/components/Script.h, one lua script per component). Phalanx
// keeps the slot and makes it the holder of the entity's C# behaviours: Entity.AddBehaviour creates it, the
// behaviours' callbacks are run by World (World.Behaviours.cs), not by this component's Tick.
namespace Phalanx.World;

public sealed class Script : Component
{
    internal readonly List<Behaviour> m_behaviours = new();

    public Script(Entity entity) : base(entity) { }

    /// <summary>The entity's behaviours in the order they were added.</summary>
    public IReadOnlyList<Behaviour> Behaviours => m_behaviours;
}
