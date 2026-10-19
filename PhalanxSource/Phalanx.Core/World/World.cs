using System;
using System.Numerics;
using Phalanx.Math;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.World;

public static partial class World
{
    public struct EntitySnapshot
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    public enum EntityChange : byte
    {
        None = 0,
        Active = 1 << 0,
        Components = 1 << 1,
        CullMode = 1 << 2,
        LightType = 1 << 3
    }
    public static class WorldTime
    {
        // simulated time
        public static float time_of_day = 0.25f; // 6 AM
        public static float time_scale = 200.0f; // 200x real time

        // tick simulated time every frame
        public static void tick()
        {
            time_of_day += (float)(Time.GetDeltaTimeSec() * time_scale) / 86400.0f;
            if (time_of_day >= 1.0f)
            {
                time_of_day -= 1.0f;
            }
            else if (time_of_day < 0.0f)
            {
                time_of_day = 0.0f;
            }
        }

        // get current time of day based on boolean
        public static float GetTimeOfDay(bool use_real_world_time)
        {
            if (use_real_world_time)
            {
                DateTime now = DateTime.Now;   // local time (exactly what the C++ localtime did)

                float hours = now.Hour;
                float minutes = now.Minute;
                float seconds = now.Second;

                return (hours + minutes / 60.0f + seconds / 3600.0f) / 24.0f;
            }

            // return simulated time if not using real-world time
            return time_of_day;
        }
    }

    private static List<Entity> entities = new List<Entity>();
    private static List<Entity> entities_lights = new List<Entity>();       // entities subset that contains only lights
    private static List<Entity> entities_renderables = new List<Entity>();  // entities subset that contains only active renderables
    private static string file_path = string.Empty;
    private static string world_name = string.Empty; // cached to avoid per-frame allocation
    private static string world_description = string.Empty;
    private static object entity_access_mutex = new object();

    private static readonly Dictionary<ulong, Entity> entities_by_id = new Dictionary<ulong, Entity>();
    private static List<Entity> pending_add = new List<Entity>();
    private static HashSet<ulong> pending_remove = new HashSet<ulong>();

    private static uint audio_source_count = 0;

    private static bool _resolve = false;
    private static bool resolve
    {
        get => Volatile.Read(ref _resolve);
        set => Volatile.Write(ref _resolve, value);
    }

    private static bool was_in_editor_mode = false;
    private static BoundingBox bounding_box = BoundingBox.Unit;
    private static Entity? camera = null;
    private static Entity? light = null;

    private static Dictionary<ulong, EntitySnapshot> play_mode_snapshot = new Dictionary<ulong, EntitySnapshot>();

    private static float play_mode_time_of_day = 0.0f;
    private static Dictionary<ulong, uint> entity_states = new Dictionary<ulong, uint>(); // stores: low 8 bits for flags, next 8 for component count, next 8 for cull mode, next 8 for light type

    // material state tracking (for fast change detection in rendering)
    private static readonly Dictionary<ulong, ulong> m_material_state_hashes = new Dictionary<ulong, ulong>();

    // light change tracking - things that change the nature of the light for rendering
    private static readonly Dictionary<ulong, ulong> m_light_state_hashes = new Dictionary<ulong, ulong>();
    private static void MarkEntityChanged(ulong id, EntityChange change)
    {
        entity_states.TryGetValue(id, out uint state);
        entity_states[id] = state | (uint)change;
        resolve = true;
    }
    /*
    private ulong compute_material_hash(Material material)
    {
        ulong hash = 17; // FNV-1a seed

        // include resource state so async preparation completion triggers an update
        hash = (hash * 31) ^ (ulong)material->GetResourceState();

        foreach (var texture in material->GetTextures())
        {
            hash = (hash * 31) ^ (ulong)texture;

            // include texture's resource state so async texture preparation triggers an update
            if (texture)
                hash = (hash * 31) ^ (ulong)(texture->GetResourceState());
        }
        foreach (float prop in material->GetProperties())
        {
            hash = (hash * 31) ^ (ulong)prop.GetHashCode();
        }
        return hash;
    }
    private ulong compute_light_hash(Light light, Entity entity)
    {
        ulong hash = 17;

        hash = (hash * 31) ^ (ulong)light.GetColor().r.GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetColor().g.GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetColor().b.GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetColor().a.GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetIntensityWatt().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetRange().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetAngle().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetAreaWidth().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetAreaHeight().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetLightType().GetHashCode();
        hash = (hash * 31) ^ (ulong)light.GetFlags().GetHashCode();
        hash = (hash * 31) ^ (ulong)entity.GetActive().GetHashCode();

        Vector3 pos = entity.GetPosition();
        hash = (hash * 31) ^ (ulong)pos.X.GetHashCode();
        hash = (hash * 31) ^ (ulong)pos.Y.GetHashCode();
        hash = (hash * 31) ^ (ulong)pos.Z.GetHashCode();
        Vector3 fwd = entity.GetForward();
        hash = (hash * 31) ^ (ulong)fwd.X.GetHashCode();
        hash = (hash * 31) ^ (ulong)fwd.Y.GetHashCode();
        hash = (hash * 31) ^ (ulong)fwd.Z.GetHashCode();

        for (uint i = 0; i < light.GetSliceCount(); i++)
        {
            Matrix4x4 vp = light.GetViewProjectionMatrix(i);
            const float* vp_data = vp.Data();
            for (uint32_t j = 0; j < 16; j++)
            {
                hash = (hash * 31) ^ (ulong) (vp_data[j]);
            }
        }

        return hash;
    }*/

    public static Entity? GetEntityById(ulong id)
    {
        lock (entity_access_mutex)
            return entities_by_id.TryGetValue(id, out Entity? entity) ? entity : null;
    }
    // threading model:
    // - entities/entities_lights/entities_renderables and the entity hierarchy are owned by the main thread
    //   (Tick, editor, gameplay). GetEntities() returns the live list, only read it on the main thread.
    // - CreateEntity/RemoveEntity may be called from other threads (loading, tools); they only touch the
    //   pending_add/pending_remove queues under entity_access_mutex, which the main thread drains in Tick.
    private static int main_thread_id = Environment.CurrentManagedThreadId;
    public static void SetMainThread() { main_thread_id = Environment.CurrentManagedThreadId; }
    [System.Diagnostics.Conditional("DEBUG")]
    public static void AssertMainThread()
    {
        Helpers.PLXAssertMsg(Environment.CurrentManagedThreadId == main_thread_id, "World/entity hierarchy accessed off the main thread");
    }
    public static IReadOnlyList<Entity> GetEntities() { AssertMainThread(); return entities; }
    public static IReadOnlyList<Entity> GetEntitiesLights() { AssertMainThread(); return entities_lights; }           // World.cpp:2688 (views.lights)
    public static IReadOnlyList<Entity> GetEntitiesRenderables() { AssertMainThread(); return entities_renderables; } // views.render
    public static Entity? GetDirectionalLight() { return light; }
    public static BoundingBox GetBoundingBox() { return bounding_box; }

    // World::Tick (World.cpp:833), the part that exists in Phalanx: the actions deferred by the last frame's render
    // callbacks, removals, the behaviours (Start, FixedUpdate, Update, LateUpdate; World.Behaviours.cs, not in
    // spartan), render then logic entity ticks, additions (whose behaviours awake and enable), then the resolve
    // (World.cpp:1122) rebuilding the camera, the directional light and the views.
    // Not ported: world io/loading, play mode snapshots, physics/scripts pretick, parallel render ticks, weather,
    // volumes/ragdolls/particles/icons views, the player-camera preference of pick_default_camera (no Physics yet).
    private static readonly List<Entity> entities_logic = new List<Entity>();
    public static void Tick() => Tick(Time.GetDeltaTimeSec());

    /// <summary>A world tick that advances the behaviours by <paramref name="delta_seconds"/> (FixedUpdate steps and
    /// <see cref="DeltaTime"/>); <see cref="Tick()"/> uses the engine's frame time.</summary>
    public static void Tick(double delta_seconds)
    {
        AssertMainThread();
        long tick_start = System.Diagnostics.Stopwatch.GetTimestamp();
        RunDeferred();
        ProcessPendingRemovals();
        RunUpdates(delta_seconds);

        for (int i = 0; i < entities_renderables.Count; i++)
            if (entities_renderables[i].GetActive())
                entities_renderables[i].Tick();
        for (int i = 0; i < entities_logic.Count; i++)
            if (entities_logic[i].GetActive())
                entities_logic[i].Tick();

        ProcessPendingAdditions();

        if (resolve)
            Resolve();
        SyncStoreTransforms();
        Stats.TickMs = System.Diagnostics.Stopwatch.GetElapsedTime(tick_start).TotalMilliseconds;
    }

    // the resolve (World.cpp:1122): the tracked lists, the camera, the directional light and the bounds. Spartan and
    // the first port rebuilt everything from every entity whenever one was added or removed (0.45 s at 2M entities);
    // this one keeps the lists sorted by world order and only places the arrivals and the entities whose activity or
    // components changed (MarkResolveDirty), with the same result as the rebuild.
    private static readonly List<Entity> s_resolve_arrivals = new();
    private static readonly HashSet<Entity> s_resolve_dirty = new();
    private static readonly List<Entity> s_resolve_dirty_sorted = new();
    private static bool s_resolve_removed;
    private static bool s_camera_scan_needed; // the camera was taken away (RemoveEntity), every camera is a candidate
    private static long s_world_order;

    // an entity's activity or components changed (it and its descendants are placed again at the next resolve)
    internal static void MarkResolveDirty(Entity entity)
    {
        if (!entity.m_in_world || entity.m_store_bound)
            return;
        lock (entity_access_mutex)
            s_resolve_dirty.Add(entity);
        IReadOnlyList<Entity> children = entity.GetChildren();
        for (int i = 0; i < children.Count; i++)
            MarkResolveDirty(children[i]);
        resolve = true;
    }

    private static int OrderIndex(List<Entity> list, long order)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long value = list[mid].m_world_order;
            if (value == order) return mid;
            if (value < order) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }
    private static void RemoveSorted(List<Entity> list, Entity entity)
    {
        int index = OrderIndex(list, entity.m_world_order);
        if (index >= 0)
            list.RemoveAt(index);
    }
    private static void InsertSorted(List<Entity> list, Entity entity)
    {
        int index = OrderIndex(list, entity.m_world_order);
        if (index < 0)
            list.Insert(~index, entity);
    }

    private static bool IsTracked(Entity entity) => entity.m_in_world && entity.GetActive() && !pending_remove.Contains(entity.GetObjectId());

    // which lists the entity belongs in (the rebuild's rules)
    private static void Place(Entity entity, bool append)
    {
        if (!IsTracked(entity))
            return;
        if (entity.GetComponent<Light>() != null)
        {
            if (append) entities_lights.Add(entity); else InsertSorted(entities_lights, entity);
        }
        if (entity.GetComponent<Render>() != null)
        {
            if (append) entities_renderables.Add(entity); else InsertSorted(entities_renderables, entity);
        }
        else if (entity.GetComponentCount() > 0)
        {
            // lights, scripts, audio, etc without a mesh still need Entity::Tick
            if (append) entities_logic.Add(entity); else InsertSorted(entities_logic, entity);
        }
    }

    private static void Resolve()
    {
        bool full_bounds = false;
        if (s_resolve_removed)
        {
            entities_lights.RemoveAll(static e => !e.m_in_world);
            entities_renderables.RemoveAll(static e => !e.m_in_world);
            entities_logic.RemoveAll(static e => !e.m_in_world);
            s_resolve_removed = false;
            full_bounds = true;
        }

        // the entities that changed, in world order, out of their lists and placed again
        s_resolve_dirty_sorted.Clear();
        lock (entity_access_mutex)
        {
            foreach (Entity entity in s_resolve_dirty)
                if (entity.m_in_world && entity.m_world_order > 0)
                    s_resolve_dirty_sorted.Add(entity);
            s_resolve_dirty.Clear();
        }
        if (s_resolve_arrivals.Count > 0 && s_resolve_dirty_sorted.Count > 0)
        {
            var arrivals = new HashSet<Entity>(s_resolve_arrivals);
            s_resolve_dirty_sorted.RemoveAll(arrivals.Contains);
        }
        s_resolve_dirty_sorted.Sort(static (a, b) => a.m_world_order.CompareTo(b.m_world_order));
        foreach (Entity entity in s_resolve_dirty_sorted)
        {
            RemoveSorted(entities_lights, entity);
            RemoveSorted(entities_renderables, entity);
            RemoveSorted(entities_logic, entity);
            Place(entity, append: false);
            full_bounds = true;
        }
        // the arrivals are the newest, they go at the ends
        foreach (Entity entity in s_resolve_arrivals)
            Place(entity, append: true);

        // the incumbent camera keeps the seat unless it is gone or went inactive; else the first camera in world order
        Entity? camera_incumbent = camera;
        if (camera_incumbent == null || !IsTracked(camera_incumbent) || camera_incumbent.GetComponent<Camera>() == null)
        {
            camera = null;
            if (camera_incumbent != null || s_camera_scan_needed)
            {
                foreach (Entity entity in entities)
                    if (entity.GetComponent<Camera>() != null && IsTracked(entity)) { camera = entity; break; }
            }
            else
            {
                // nothing qualified before, so only an arrival or a changed entity can
                foreach (Entity entity in s_resolve_dirty_sorted)
                    if (entity.GetComponent<Camera>() != null && IsTracked(entity) && (camera == null || entity.m_world_order < camera.m_world_order)) camera = entity;
                foreach (Entity entity in s_resolve_arrivals)
                    if (entity.GetComponent<Camera>() != null && IsTracked(entity) && (camera == null || entity.m_world_order < camera.m_world_order)) camera = entity;
            }
        }

        s_camera_scan_needed = false;

        // the first directional light in world order
        light = null;
        foreach (Entity entity in entities_lights)
            if (entity.GetComponent<Light>() is Light light_comp && light_comp.GetLightType() == LightType.Directional) { light = entity; break; }

        if (full_bounds)
            ComputeBoundingBox();
        else
        {
            foreach (Entity entity in s_resolve_arrivals)
            {
                if (!IsTracked(entity) || entity.GetComponent<Render>() is not Render render)
                    continue;
                if (!s_bounding_box_set) { bounding_box = render.GetBoundingBox(); s_bounding_box_set = true; }
                else bounding_box.Merge(render.GetBoundingBox());
            }
        }
        s_resolve_arrivals.Clear();
        s_resolve_dirty_sorted.Clear();
        resolve = false;
    }

    private static bool s_bounding_box_set;
    private static void ComputeBoundingBox()
    {
        bounding_box = BoundingBox.Unit;
        bool first = true;
        foreach (Entity entity in entities_renderables)
        {
            BoundingBox box = entity.GetComponent<Render>()!.GetBoundingBox();
            if (first) { bounding_box = box; first = false; }
            else bounding_box.Merge(box);
        }
        s_bounding_box_set = !first;
    }

    // World::ProcessPendingRemovals
    private static readonly List<Entity> s_entities_leaving = new();
    private static void ProcessPendingRemovals()
    {
        lock (entity_access_mutex)
        {
            if (pending_remove.Count == 0)
                return;
            foreach (Entity entity in entities)
                if (pending_remove.Contains(entity.GetObjectId()))
                    s_entities_leaving.Add(entity);
            entities.RemoveAll(e => pending_remove.Contains(e.GetObjectId()));
            foreach (Entity entity in s_entities_leaving)
                entities_by_id.Remove(entity.GetObjectId());
            pending_add.RemoveAll(e => pending_remove.Contains(e.GetObjectId()));
            foreach (ulong id in pending_remove)
                entity_states.Remove(id);
            pending_remove.Clear();
            // the tracked lists still point at the removed entities
            resolve = true;
            s_resolve_removed = true;
        }
        // their behaviours are disabled and destroyed outside the lock (the callbacks may create or remove entities)
        foreach (Entity entity in s_entities_leaving)
        {
            entity.m_in_world = false;
            DestroyBehaviours(entity);
            if (!entity.m_store_id.IsNull)
                Store.DestroyTree(entity.m_store_id); // its row and the store entities parented to it
        }
        s_entities_leaving.Clear();
    }

    // World::ProcessPendingAdditions (World.cpp:634)
    private static readonly List<Entity> s_entities_arriving = new();
    private static void ProcessPendingAdditions()
    {
        lock (entity_access_mutex)
        {
            if (pending_add.Count == 0)
                return;
            entities.AddRange(pending_add);
            foreach (Entity entity in pending_add)
            {
                entity.m_world_order = ++s_world_order;
                entities_by_id[entity.GetObjectId()] = entity;
            }
            s_entities_arriving.AddRange(pending_add);
            s_resolve_arrivals.AddRange(pending_add);
            pending_add.Clear();
            resolve = true;
        }
        // in the world now: their behaviours awake and enable, in creation order
        foreach (Entity entity in s_entities_arriving)
            entity.m_in_world = true;
        foreach (Entity entity in s_entities_arriving)
            RefreshBehaviours(entity, recursive: false);
        s_entities_arriving.Clear();
    }

    // World::ClearScene (World.cpp:657), the entity part
    public static void ClearScene()
    {
        // the behaviours of the world go first (OnDisable, OnDestroy)
        foreach (Entity entity in entities.ToArray())
        {
            entity.m_in_world = false;
            DestroyBehaviours(entity);
        }
        lock (entity_access_mutex)
        {
            camera = null;
            light  = null;
            entities.Clear();
            pending_add.Clear();
            pending_remove.Clear();
            entities_lights.Clear();
            entities_renderables.Clear();
            entities_logic.Clear();
            entities_by_id.Clear();
            entity_states.Clear();
            bounding_box = BoundingBox.Unit;
            s_bounding_box_set = false;
            s_resolve_arrivals.Clear();
            s_resolve_dirty.Clear();
            s_resolve_removed = false;
            s_camera_scan_needed = false;
            resolve = false;
        }
        Store.Clear();
        ClearBehaviourState();
    }

    public static Camera? GetCamera() { return (camera != null) ? camera.GetComponent<Camera>() : null; }
    public static float GetTimeOfDay(bool use_real_world_time) { return WorldTime.GetTimeOfDay(use_real_world_time); }
    public static void SetTimeOfDay(float TimeOfDay)
    {
        if (TimeOfDay < 0.0f)
            TimeOfDay = 0.0f;
        else if (TimeOfDay > 1.0f)
            TimeOfDay = 1.0f;
        WorldTime.time_of_day = TimeOfDay;
    }

    public static Entity CreateEntity()
    {
        Entity entity = new Entity();
        lock (entity_access_mutex)
        {
            pending_add.Add(entity);
            MarkEntityChanged(entity.GetObjectId(), EntityChange.Components); // new entity requires resolve
        }
        return entity;
    }
    public static void RemoveEntity(Entity? entity_to_remove)
    {
        Helpers.PLXAssertMsg(entity_to_remove != null, "Entity is null");

        lock (entity_access_mutex)
        {
            // keep track of the local camera pointer so we don't have a dangling pointer
            if (camera == entity_to_remove)
            {
                camera = null;
                s_camera_scan_needed = true;
            }

            // remove the entity and all of its children

            // get the root entity and its descendants
            List<Entity> entities_to_remove = new List<Entity>() { entity_to_remove }; // add the root entity
            entity_to_remove.GetDescendants(entities_to_remove); // get descendants

            // create a set containing the object ids of entities to remove
            // defer removal
            for (int i = 0; i < entities_to_remove.Count; i++)
            {
                pending_remove.Add(entities_to_remove[i].GetObjectId());
                if (entities_to_remove[i].m_in_world)
                    s_resolve_dirty.Add(entities_to_remove[i]); // out of the tracked lists at the next resolve
            }

            // detach from parent so it won't hold a dangling pointer after deferred deletion
            if (entity_to_remove.GetParent() is Entity parent)
                parent.RemoveChild(entity_to_remove, false);
        }
        resolve = true;
    }
}
public struct WorldMetadata
{
    string file_path;
    string name;
    string description;
};