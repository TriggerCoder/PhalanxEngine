using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Phalanx;

public enum ComponentType : uint
{
    AudioSource,
    Camera,
    Light,
    Physics,
    Render,
    Spline,
    SplineFollower,
    Terrain,
    Volume,
    Script,
    ParticleSystem,
    Max
}

public class Component : SpartanObject
{
    public struct Attribute
    {
        public Func<object?> getter { get; init; }
        public Action<object?> setter { get; init; }
    }

    private readonly List<Attribute> m_attributes = new List<Attribute>();
    // the type of the component
    protected ComponentType m_type = ComponentType.Max;
    // the state of the component
    protected bool m_enabled = false;
    // the owner of the component
    protected Entity? m_entity_ptr = null;

    private static readonly Dictionary<Type, ComponentType> s_typeMap = new()
    {/* TODO
        [typeof(AudioSource)] = ComponentType.AudioSource,
        [typeof(Camera)] = ComponentType.Camera,
        [typeof(Light)] = ComponentType.Light,
        [typeof(Physics)] = ComponentType.Physics,
        [typeof(Render)] = ComponentType.Render,
        [typeof(Spline)] = ComponentType.Spline,
        [typeof(SplineFollower)] = ComponentType.SplineFollower,
        [typeof(Terrain)] = ComponentType.Terrain,
        [typeof(Volume)] = ComponentType.Volume,
        [typeof(Script)] = ComponentType.Script,
        [typeof(ParticleSystem)] = ComponentType.ParticleSystem,
*/    };

    public ComponentType GetComponentType() { return m_type; }
    public void SetComponentType(ComponentType type) { m_type = type; }
    public void SetAttributes(List<Attribute> attributes)
    {
        foreach (var(target,source) in m_attributes.Zip(attributes))
        {
            target.setter(source.getter());
        }
    }
    public List<Attribute> GetAttributes() { return m_attributes; }
    public static ComponentType TypeToEnum<T>() where T : Component { return s_typeMap.TryGetValue(typeof(T), out var componentType) ? componentType : ComponentType.Max; }

    // called when the component gets added
    public virtual void Initialize() { }

    // called every time the simulation starts
    public virtual void Start() { }

    // called every time the simulation stops
    public virtual void Stop() { }

    // called when the component is removed
    public virtual void Remove() { }

    // called every frame, before Tick, useful to reset states before the main update
    public virtual void PreTick() { }

    // called every frame
    public virtual void Tick() { }
}