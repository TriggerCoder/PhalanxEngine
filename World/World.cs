using System;
using System.Numerics;

namespace Phalanx;

public class World
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

    private List<Entity> entities = new List<Entity>();
    private List<Entity> entities_lights = new List<Entity>();       // entities subset that contains only lights
    private List<Entity> entities_renderables = new List<Entity>();  // entities subset that contains only active renderables
    private string file_path = string.Empty;
    private string world_name = string.Empty; // cached to avoid per-frame allocation
    private string world_description = string.Empty;
    private object entity_access_mutex = new object();

    private List<Entity> pending_add = new List<Entity>();
    private HashSet<ulong> pending_remove = new HashSet<ulong>();

    private uint audio_source_count = 0;

    private bool _resolve = false;
    private bool resolve
    {
        get => Volatile.Read(ref _resolve);
        set => Volatile.Write(ref _resolve, value);
    }

    private bool was_in_editor_mode = false;
    private BoundingBox bounding_box = BoundingBox.Unit;
    private Entity? camera = null;
    private Entity? light = null;

    private Dictionary<ulong, EntitySnapshot> play_mode_snapshot = new Dictionary<ulong, EntitySnapshot>();

    private float play_mode_time_of_day = 0.0f;
    private Dictionary<ulong, uint> entity_states = new Dictionary<ulong, uint>(); // stores: low 8 bits for flags, next 8 for component count, next 8 for cull mode, next 8 for light type

    // material state tracking (for fast change detection in rendering)
    private readonly Dictionary<ulong, ulong> m_material_state_hashes = new Dictionary<ulong, ulong>();

    // light change tracking - things that change the nature of the light for rendering
    private readonly Dictionary<ulong, ulong> m_light_state_hashes = new Dictionary<ulong, ulong>();
    private void mark_entity_changed(ulong id, EntityChange change)
    {
        entity_states[id] |= (uint)change;
        resolve = true;
    }
    /*TODO
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
        hash = (hash * 31) ^ (ulong)pos.x.GetHashCode();
        hash = (hash * 31) ^ (ulong)pos.y.GetHashCode();
        hash = (hash * 31) ^ (ulong)pos.z.GetHashCode();
        Vector3 fwd = entity.GetForward();
        hash = (hash * 31) ^ (ulong)fwd.x.GetHashCode();
        hash = (hash * 31) ^ (ulong)fwd.y.GetHashCode();
        hash = (hash * 31) ^ (ulong)fwd.z.GetHashCode();

        for (uint i = 0; i < light.GetSliceCount(); i++)
        {
            Matrix vp = light.GetViewProjectionMatrix(i);
            const float* vp_data = vp.Data();
            for (uint32_t j = 0; j < 16; j++)
            {
                hash = (hash * 31) ^ (ulong) (vp_data[j]);
            }
        }

        return hash;
    }
    */ //END TODO
}

public struct WorldMetadata
{
    string file_path;
    string name;
    string description;
};