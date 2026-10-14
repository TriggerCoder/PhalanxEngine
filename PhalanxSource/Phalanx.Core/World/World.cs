using System;
using System.Numerics;
using Phalanx.Math;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;

namespace Phalanx.World;

public static class World
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
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity? entity = entities[i];
                if ((entity != null) && (entity.GetObjectId() == id))
                    return entity;
            }
        }
        return null;
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

    // World::Tick (World.cpp:833), the part that exists in Phalanx: removals, render then logic entity ticks,
    // additions, then the resolve (World.cpp:1122) rebuilding the camera, the directional light and the views.
    // Not ported: world io/loading, play mode snapshots, physics/scripts pretick, parallel render ticks, weather,
    // volumes/ragdolls/particles/icons views, the player-camera preference of pick_default_camera (no Physics yet).
    private static readonly List<Entity> entities_logic = new List<Entity>();
    public static void Tick()
    {
        AssertMainThread();
        ProcessPendingRemovals();

        for (int i = 0; i < entities_renderables.Count; i++)
            if (entities_renderables[i].GetActive())
                entities_renderables[i].Tick();
        for (int i = 0; i < entities_logic.Count; i++)
            if (entities_logic[i].GetActive())
                entities_logic[i].Tick();

        ProcessPendingAdditions();

        if (resolve)
        {
            // the incumbent camera keeps the seat unless it is gone or went inactive
            Entity? camera_incumbent   = camera;
            bool camera_incumbent_live = false;
            camera = null;
            light  = null;
            entities_lights.Clear();
            entities_renderables.Clear();
            entities_logic.Clear();
            foreach (Entity entity in entities)
            {
                if (pending_remove.Contains(entity.GetObjectId()) || !entity.GetActive())
                    continue;

                if (camera == null && entity.GetComponent<Camera>() != null)
                    camera = entity;
                camera_incumbent_live |= entity == camera_incumbent;

                if (entity.GetComponent<Light>() is Light light_comp)
                {
                    if (light == null && light_comp.GetLightType() == LightType.Directional)
                        light = entity;
                    entities_lights.Add(entity);
                }

                if (entity.GetComponent<Render>() != null)
                    entities_renderables.Add(entity);
                else if (entity.GetComponentCount() > 0)
                    entities_logic.Add(entity); // lights, scripts, audio, etc without a mesh still need Entity::Tick
            }
            if (camera_incumbent_live)
                camera = camera_incumbent;

            ComputeBoundingBox();
            resolve = false;
        }
    }

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
    }

    // World::ProcessPendingRemovals
    private static void ProcessPendingRemovals()
    {
        lock (entity_access_mutex)
        {
            if (pending_remove.Count == 0)
                return;
            entities.RemoveAll(e => pending_remove.Contains(e.GetObjectId()));
            pending_add.RemoveAll(e => pending_remove.Contains(e.GetObjectId()));
            foreach (ulong id in pending_remove)
                entity_states.Remove(id);
            pending_remove.Clear();
            // the tracked lists still point at the removed entities
            resolve = true;
        }
    }

    // World::ProcessPendingAdditions (World.cpp:634)
    private static void ProcessPendingAdditions()
    {
        lock (entity_access_mutex)
        {
            if (pending_add.Count == 0)
                return;
            entities.AddRange(pending_add);
            pending_add.Clear();
            resolve = true;
        }
    }

    // World::ClearScene (World.cpp:657), the entity part
    public static void ClearScene()
    {
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
            entity_states.Clear();
            bounding_box = BoundingBox.Unit;
            resolve = false;
        }
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
                camera = null;

            // remove the entity and all of its children

            // get the root entity and its descendants
            List<Entity> entities_to_remove = new List<Entity>() { entity_to_remove }; // add the root entity
            entity_to_remove.GetDescendants(entities_to_remove); // get descendants

            // create a set containing the object ids of entities to remove
            // defer removal
            for (int i = 0; i < entities_to_remove.Count; i++)
                pending_remove.Add(entities_to_remove[i].GetObjectId());

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