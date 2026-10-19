// The behaviour lifecycle of World: which behaviours are live, the typed callback lists, the fixed step, the deferred
// actions and the render callback scope. Not in SpartanEngine (lua scripts tick inside Entity::Tick); the frame order
// follows Unity's, see the order on World.Tick.
using System.Diagnostics;

namespace Phalanx.World;

public static partial class World
{
    // a callback list that tolerates adds and removes while it is being walked: an add made during the walk is called
    // from the next walk, a remove takes effect at once
    private sealed class DispatchList<T> where T : class
    {
        private readonly List<T?> m_items = new();
        private int m_walking;
        private bool m_has_holes;

        public int Count => m_items.Count;

        public void Add(T item) => m_items.Add(item);

        public void Remove(T item)
        {
            int index = m_items.IndexOf(item);
            if (index < 0)
                return;
            if (m_walking > 0)
            {
                m_items[index] = null;
                m_has_holes    = true;
            }
            else
                m_items.RemoveAt(index);
        }

        public void Invoke(Action<T> call)
        {
            m_walking++;
            try
            {
                int count = m_items.Count;
                for (int i = 0; i < count; i++)
                {
                    if (m_items[i] is T item)
                        Call(item, call);
                }
            }
            finally
            {
                if (--m_walking == 0 && m_has_holes)
                {
                    m_items.RemoveAll(static item => item == null);
                    m_has_holes = false;
                }
            }
        }

        public void Clear()
        {
            m_items.Clear();
            m_has_holes = false;
        }
    }

    private static readonly DispatchList<IFixedUpdate> s_fixed_updates = new();
    private static readonly DispatchList<IUpdate>      s_updates       = new();
    private static readonly DispatchList<ILateUpdate>  s_late_updates  = new();
    private static readonly List<Behaviour> s_pending_start = new();
    private static readonly List<Action> s_deferred = new();
    private static readonly List<Action> s_deferred_running = new();
    private static double s_fixed_accumulator;
    private static int s_render_callback_depth;

    /// <summary>The simulated time of one <see cref="IFixedUpdate.FixedUpdate"/>, in seconds (Unity's default, 1/50).</summary>
    public static double FixedTimeStep { get; set; } = 0.02;

    /// <summary>The most fixed steps one tick runs; the rest of a long frame is dropped so a hitch doesn't snowball.</summary>
    public static int MaxFixedStepsPerTick { get; set; } = 8;

    /// <summary>The time the current tick advanced, in seconds.</summary>
    public static double DeltaTime { get; private set; }

    /// <summary>True while the renderer runs <see cref="IPreDraw"/> or <see cref="IOnBecameVisible"/> callbacks;
    /// transform, hierarchy and activity changes made then are deferred.</summary>
    public static bool IsInRenderCallback => s_render_callback_depth > 0;

    /// <summary>Runs <paramref name="action"/> on the main thread at the start of the next <see cref="Tick()"/>, before
    /// any behaviour callback, in the order deferred.</summary>
    public static void Defer(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (s_deferred)
            s_deferred.Add(action);
    }

    // when inside a render callback, queue the change instead of making it (the closure is only made then)
    internal static bool DeferIfRendering<TTarget, TValue>(TTarget target, TValue value, Action<TTarget, TValue> change)
    {
        if (s_render_callback_depth == 0)
            return false;
        DeferChange(target, value, change);
        return true;
    }
    private static void DeferChange<TTarget, TValue>(TTarget target, TValue value, Action<TTarget, TValue> change) => Defer(() => change(target, value));

    internal static void BeginRenderCallbacks() { AssertMainThread(); s_render_callback_depth++; }
    internal static void EndRenderCallbacks()   { s_render_callback_depth--; }

    private static void Call<T>(T target, Action<T> call) where T : class
    {
        try
        {
            call(target);
        }
        catch (Exception e)
        {
            // a failing behaviour doesn't stop the others (Unity logs and carries on)
            Log.LogError($"{target.GetType().Name}: {e}");
        }
    }

    private static BehaviourCallbacks FindCallbacks(Behaviour b)
    {
        BehaviourCallbacks c = BehaviourCallbacks.None;
        if (b is IAwake)           c |= BehaviourCallbacks.Awake;
        if (b is IOnEnable)        c |= BehaviourCallbacks.OnEnable;
        if (b is IStart)           c |= BehaviourCallbacks.Start;
        if (b is IFixedUpdate)     c |= BehaviourCallbacks.FixedUpdate;
        if (b is IUpdate)          c |= BehaviourCallbacks.Update;
        if (b is ILateUpdate)      c |= BehaviourCallbacks.LateUpdate;
        if (b is IOnDisable)       c |= BehaviourCallbacks.OnDisable;
        if (b is IOnDestroy)       c |= BehaviourCallbacks.OnDestroy;
        if (b is IOnBecameVisible) c |= BehaviourCallbacks.OnBecameVisible;
        if (b is IPreDraw)         c |= BehaviourCallbacks.PreDraw;
        return c;
    }

    // a behaviour was added to an entity
    internal static void AttachBehaviour(Behaviour b)
    {
        b.m_callbacks = FindCallbacks(b);
        RefreshBehaviour(b);
    }

    // brings a behaviour's state in line with its entity and its Enabled: awake the first time it is live, enable or
    // disable when that changes (callbacks may change it again, each step looks at the state it left)
    internal static void RefreshBehaviour(Behaviour b)
    {
        if (b.m_destroyed)
            return;
        Entity entity = b.Entity;
        if (!b.m_awoken && entity.m_in_world && entity.GetActive())
        {
            b.m_awoken = true;
            if (b is IAwake awake)
                Call(awake, static x => x.Awake());
            if (b.m_destroyed)
                return;
        }
        bool active = b.m_awoken && b.Enabled && entity.m_in_world && entity.GetActive();
        if (active == b.m_active)
            return;
        b.m_active = active;
        if (active)
        {
            if (b is IOnEnable on_enable)
                Call(on_enable, static x => x.OnEnable());
            if (!b.m_active)
                return;
            if (b.m_started)
                Register(b);
            else if (!b.m_pending_start)
            {
                b.m_pending_start = true;
                s_pending_start.Add(b);
            }
        }
        else
        {
            Unregister(b);
            if (b is IOnDisable on_disable)
                Call(on_disable, static x => x.OnDisable());
        }
    }

    // the behaviour is removed (or its entity is): disable, then destroy if it was ever awoken
    internal static void DestroyBehaviour(Behaviour b)
    {
        if (b.m_destroyed)
            return;
        b.m_destroyed = true;
        if (b.m_active)
        {
            b.m_active = false;
            Unregister(b);
            if (b is IOnDisable on_disable)
                Call(on_disable, static x => x.OnDisable());
        }
        if (b.m_awoken && b is IOnDestroy on_destroy)
            Call(on_destroy, static x => x.OnDestroy());
    }

    // an entity's activity or world membership changed, which is inherited by its children
    internal static void RefreshBehaviours(Entity entity, bool recursive = true)
    {
        IReadOnlyList<Behaviour> behaviours = entity.Behaviours;
        for (int i = 0; i < behaviours.Count; i++)
            RefreshBehaviour(behaviours[i]);
        if (!recursive)
            return;
        IReadOnlyList<Entity> children = entity.GetChildren();
        for (int i = 0; i < children.Count; i++)
            RefreshBehaviours(children[i]);
    }

    private static void DestroyBehaviours(Entity entity)
    {
        IReadOnlyList<Behaviour> behaviours = entity.Behaviours;
        for (int i = 0; i < behaviours.Count; i++)
            DestroyBehaviour(behaviours[i]);
    }

    private static void Register(Behaviour b)
    {
        BehaviourCallbacks c = b.m_callbacks;
        if ((c & BehaviourCallbacks.FixedUpdate) != 0) s_fixed_updates.Add((IFixedUpdate)b);
        if ((c & BehaviourCallbacks.Update) != 0)      s_updates.Add((IUpdate)b);
        if ((c & BehaviourCallbacks.LateUpdate) != 0)  s_late_updates.Add((ILateUpdate)b);
        if ((c & BehaviourCallbacks.PreDraw) != 0)     b.Entity.m_pre_draw.Add((IPreDraw)b);
        if ((c & BehaviourCallbacks.OnBecameVisible) != 0) b.Entity.m_visibility.Add((IOnBecameVisible)b);
    }

    private static void Unregister(Behaviour b)
    {
        BehaviourCallbacks c = b.m_callbacks;
        if ((c & BehaviourCallbacks.FixedUpdate) != 0) s_fixed_updates.Remove((IFixedUpdate)b);
        if ((c & BehaviourCallbacks.Update) != 0)      s_updates.Remove((IUpdate)b);
        if ((c & BehaviourCallbacks.LateUpdate) != 0)  s_late_updates.Remove((ILateUpdate)b);
        if ((c & BehaviourCallbacks.PreDraw) != 0)     b.Entity.m_pre_draw.Remove((IPreDraw)b);
        if ((c & BehaviourCallbacks.OnBecameVisible) != 0) b.Entity.m_visibility.Remove((IOnBecameVisible)b);
    }

    // the deferred actions of the previous frame, then what they defer runs next tick
    private static void RunDeferred()
    {
        lock (s_deferred)
        {
            if (s_deferred.Count == 0)
                return;
            s_deferred_running.AddRange(s_deferred);
            s_deferred.Clear();
        }
        try
        {
            foreach (Action action in s_deferred_running)
                Call(action, static a => a());
        }
        finally
        {
            s_deferred_running.Clear();
        }
    }

    // Start for the behaviours enabled since the last tick, then they join the update lists
    private static void RunStart()
    {
        for (int i = 0; i < s_pending_start.Count; i++) // Start may enable more, they start in this pass too
        {
            Behaviour b = s_pending_start[i];
            b.m_pending_start = false;
            if (!b.m_active || b.m_started)
                continue;
            b.m_started = true;
            if (b is IStart start)
                Call(start, static x => x.Start());
            if (b.m_active)
                Register(b);
        }
        s_pending_start.Clear();
    }

    private static void RunUpdates(double delta_seconds)
    {
        DeltaTime = delta_seconds;
        RunStart();

        if (FixedTimeStep > 0.0)
        {
            s_fixed_accumulator += System.Math.Max(delta_seconds, 0.0);
            int steps = 0;
            while (s_fixed_accumulator >= FixedTimeStep && steps < MaxFixedStepsPerTick)
            {
                s_fixed_accumulator -= FixedTimeStep;
                steps++;
                s_fixed_updates.Invoke(static x => x.FixedUpdate());
            }
            if (s_fixed_accumulator >= FixedTimeStep)
                s_fixed_accumulator %= FixedTimeStep;
        }

        s_updates.Invoke(static x => x.Update());
        s_late_updates.Invoke(static x => x.LateUpdate());
    }

    private static void ClearBehaviourState()
    {
        s_fixed_updates.Clear();
        s_updates.Clear();
        s_late_updates.Clear();
        s_pending_start.Clear();
        lock (s_deferred)
            s_deferred.Clear();
        s_fixed_accumulator = 0.0;
        s_visible_now.Clear();
        s_visible_before.Clear();
    }

    /// <summary>Calls the entity's enabled <see cref="IPreDraw"/> behaviours for one view (the renderer, after the
    /// view's culling).</summary>
    internal static void InvokePreDraw(Entity entity, in ViewContext view)
    {
        Debug.Assert(s_render_callback_depth > 0);
        // the list can't change during the calls: enabling, disabling and removing behaviours is deferred in them
        List<IPreDraw> list = entity.m_pre_draw;
        for (int i = 0; i < list.Count; i++)
        {
            IPreDraw target = list[i];
            try
            {
                target.OnPreDraw(in view);
            }
            catch (Exception e)
            {
                Log.LogError($"{target.GetType().Name}: {e}");
            }
        }
    }

    // OnBecameVisible / OnBecameInvisible: the renderer marks the entities with listeners that a view draws this frame
    private static HashSet<Entity> s_visible_now = new(), s_visible_before = new();

    internal static void MarkVisible(Entity entity)
    {
        if (entity.m_visibility.Count > 0)
            s_visible_now.Add(entity);
    }

    internal static void CommitVisibility()
    {
        Debug.Assert(s_render_callback_depth > 0);
        foreach (Entity entity in s_visible_now)
            if (!s_visible_before.Contains(entity))
                InvokeVisibility(entity, true);
        foreach (Entity entity in s_visible_before)
            if (!s_visible_now.Contains(entity))
                InvokeVisibility(entity, false);
        (s_visible_before, s_visible_now) = (s_visible_now, s_visible_before);
        s_visible_now.Clear();
    }

    private static void InvokeVisibility(Entity entity, bool visible)
    {
        IOnBecameVisible[] targets = entity.m_visibility.ToArray();
        foreach (IOnBecameVisible target in targets)
        {
            if (visible)
                Call(target, static x => x.OnBecameVisible());
            else
                Call(target, static x => x.OnBecameInvisible());
        }
    }
}
