// The opt-in lifecycle callbacks of a Behaviour. Not in SpartanEngine (its scripts are lua components ticked with the
// entity); the set and the order follow Unity's MonoBehaviour messages, but as interfaces: a behaviour implements the
// ones it needs, the world finds them with a type test when the behaviour is enabled and keeps one typed list per
// callback, so there is no per-frame reflection and nothing is called on behaviours that don't implement it.
namespace Phalanx.World;

/// <summary>Once, the first time the behaviour is live (its entity is in the world and active), enabled or not.</summary>
public interface IAwake { void Awake(); }

/// <summary>Every time the behaviour becomes live and enabled (after <see cref="IAwake.Awake"/> the first time).</summary>
public interface IOnEnable { void OnEnable(); }

/// <summary>Once, at the start of the first <see cref="World.Tick()"/> the behaviour is enabled in, before its first update.</summary>
public interface IStart { void Start(); }

/// <summary>Every <see cref="World.FixedTimeStep"/> of simulated time, zero or more times per world tick, before the update.</summary>
public interface IFixedUpdate { void FixedUpdate(); }

/// <summary>Once per world tick.</summary>
public interface IUpdate { void Update(); }

/// <summary>Once per world tick, after every <see cref="IUpdate.Update"/> and before the engine components tick.</summary>
public interface ILateUpdate { void LateUpdate(); }

/// <summary>When the behaviour stops being live and enabled (disabled, entity deactivated or removed, behaviour removed).</summary>
public interface IOnDisable { void OnDisable(); }

/// <summary>Once, when the behaviour or its entity is removed, after <see cref="IOnDisable.OnDisable"/>; only if it was awoken.</summary>
public interface IOnDestroy { void OnDestroy(); }

/// <summary>When the entity's renderable starts or stops being drawn by at least one view (the union of the views of
/// the frame). Called by the renderer on the main thread after the views are culled; world changes are deferred like
/// in <see cref="IPreDraw.OnPreDraw"/>.</summary>
public interface IOnBecameVisible
{
    void OnBecameVisible();
    void OnBecameInvisible();
}

/// <summary>
/// Called on the main thread once per view that is going to draw the entity this frame: after that view culled it
/// (frustum and <see cref="Render.RenderLayers"/> against the view's cull mask) and before anything is drawn. Changes
/// made through <paramref name="view"/> apply to this view only. Transform, hierarchy and activity changes made here
/// are deferred to the start of the next <see cref="World.Tick()"/>.
/// </summary>
public interface IPreDraw { void OnPreDraw(in ViewContext view); }
