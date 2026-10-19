// A user component: any number per entity, kept by the entity's Script component, with the opt-in callbacks of
// Lifecycle.cs. Spartan's Script component (source/world/components/Script.h) runs one lua file per component; Phalanx
// replaces it with C# behaviours, the Script slot holding the entity's behaviour list.
namespace Phalanx.World;

public abstract class Behaviour
{
    private bool m_enabled = true;

    // lifecycle state, owned by World (World.Behaviours.cs)
    internal BehaviourCallbacks m_callbacks;
    internal bool m_awoken, m_started, m_active, m_destroyed, m_pending_start;

    /// <summary>The entity the behaviour is on; set when it is added (<see cref="Entity.AddBehaviour{T}()"/>).</summary>
    public Entity Entity { get; internal set; } = null!;

    /// <summary>Whether the behaviour wants its callbacks; it gets them while its entity is also in the world and active.</summary>
    public bool Enabled
    {
        get => m_enabled;
        set
        {
            if (m_enabled == value)
                return;
            if (World.DeferIfRendering(this, value, static (b, v) => b.Enabled = v))
                return;
            m_enabled = value;
            if (Entity != null)
                World.RefreshBehaviour(this);
        }
    }

    /// <summary>Enabled, awoken and on an active entity in the world: the behaviour receives its updates.</summary>
    public bool IsActiveAndEnabled => m_active;

    /// <summary>Whether the behaviour or its entity was removed; a destroyed behaviour gets no more callbacks.</summary>
    public bool IsDestroyed => m_destroyed;

    public T? GetComponent<T>() where T : Component => Entity.GetComponent<T>();
    public T? GetBehaviour<T>() where T : class => Entity.GetBehaviour<T>();
}

// which callbacks a behaviour implements, found once when it is added
[Flags]
internal enum BehaviourCallbacks : ushort
{
    None             = 0,
    Awake            = 1 << 0,
    OnEnable         = 1 << 1,
    Start            = 1 << 2,
    FixedUpdate      = 1 << 3,
    Update           = 1 << 4,
    LateUpdate       = 1 << 5,
    OnDisable        = 1 << 6,
    OnDestroy        = 1 << 7,
    OnBecameVisible  = 1 << 8,
    PreDraw          = 1 << 9,
}
