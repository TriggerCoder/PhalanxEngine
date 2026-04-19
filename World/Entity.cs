using System;
using System.Runtime.CompilerServices;

namespace Phalanx;

[Serializable]
public class Entity : SpartanObject
{
    private bool _m_is_active = true;
    public bool m_is_active
    {
        get => Volatile.Read(ref _m_is_active);
        set => Volatile.Write(ref _m_is_active, value);
    }

    private bool m_transient = false; // transient entities are not serialized
    private readonly Component?[] m_components = new Component?[(int)ComponentType.Max];

    // local
    Vector3 m_position_local = Vector3.Zero;
    Quaternion m_rotation_local = Quaternion.Identity;
    Vector3 m_scale_local = Vector3.One;

    Matrix m_matrix = Matrix.Identity;
    Matrix m_matrix_previous = Matrix.Identity;
    Matrix m_matrix_local = Matrix.Identity;

    // computed during UpdateTransform() and cached for performance
    Vector3 m_forward = Vector3.Zero;
    Vector3 m_backward = Vector3.Zero;
    Vector3 m_up = Vector3.Zero;
    Vector3 m_down = Vector3.Zero;
    Vector3 m_right = Vector3.Zero;
    Vector3 m_left = Vector3.Zero;

    private Entity? m_parent;
    private readonly List<Entity> m_children = new List<Entity>();

    // misc
    private readonly object m_mutex_children = new object();
    private readonly object m_mutex_parent = new object();
    private float m_time_since_last_transform_sec = 0.0f;

    // prefab data (if this entity was created from a prefab)
    private string m_prefab_type = string.Empty;
    private string m_prefab_file_path = string.Empty;
    private readonly Dictionary<string, string> m_prefab_attributes = new Dictionary<string, string>();

    public Entity()
    {
        m_object_name = "Entity";
        m_is_active = true;

        Array.Fill(m_components, null);
    }

    ~Entity()
    {
        Array.Fill(m_components, null);

        //TODO clean up after Wold and Camera are added
        /*
        // if this entity is selected, deselect it
        if (Camera camera = World::GetCamera())
        {
            if (Entity * selected_entity = camera->GetSelectedEntity())
            {
                if (selected_entity->GetObjectId() == GetObjectId())
                {
                    camera->SetSelectedEntity(nullptr);
                }
            }
        }
        */
    }
    private void UpdateTransform()
    {
        // compute local transform
        m_matrix_local = new Matrix(m_position_local, m_rotation_local, m_scale_local);

        // compute world transform
        if (m_parent != null)
            m_matrix = m_matrix_local * m_parent.GetMatrix();
        else
            m_matrix = m_matrix_local;

        // update directions directly from matrix (avoids unstable quaternion decomposition)
        // row-major layout: row 0 = right (X), row 1 = up (Y), row 2 = forward (Z)
        {
            // x
            m_right = Vector3.Normalize(new Vector3(m_matrix.m00, m_matrix.m01, m_matrix.m02));
            m_left = -m_right;
            // y
            m_up = Vector3.Normalize(new Vector3(m_matrix.m10, m_matrix.m11, m_matrix.m12));
            m_down = -m_up;
            // z
            m_forward = Vector3.Normalize(new Vector3(m_matrix.m20, m_matrix.m21, m_matrix.m22));
            m_backward = -m_forward;
        }

        // mark update
        m_time_since_last_transform_sec = 0.0f;

        // update children
        foreach (Entity child in m_children)
            child.UpdateTransform();
    }
    private Matrix GetParentTransformMatrix()
    {
        return (GetParent() != null) ? GetParent().GetMatrix() : Matrix.Identity;
    }
    public bool GetActive()
    {
        Entity parent = GetParent();
        if (parent != null)
            return m_is_active && parent.GetActive();
        return m_is_active;
    }
    public void SetActive(bool active)
    {
        if (active == m_is_active)
            return;
        m_is_active = active;
    }

    public Matrix GetMatrix() { return m_matrix; }
    public Matrix GetLocalMatrix() { return m_matrix_local; }
    public Matrix GetMatrixPrevious() { return m_matrix_previous; }
    void SetMatrixPrevious(Matrix matrix) { m_matrix_previous = matrix; }
    public float GetTimeSinceLastTransform() { return m_time_since_last_transform_sec; }
    public Component?[] GetAllComponents() { return m_components; }

    public int GetComponentCount() { return m_components.Count(c => c != null); }
    public bool IsActive() { return m_is_active; }

#region POSITION ======================================================================
    public Vector3 GetPosition() { return m_matrix.GetTranslation(); }
    public Vector3 GetPositionLocal() { return m_position_local; }
    public void SetPosition(Vector3 position)
    {
        if (GetPosition() == position)
            return;

        SetPositionLocal((GetParent() == null)? position : position * GetParent().GetMatrix().Inverted());
    }
    public void SetPositionLocal(Vector3 position)
    {
        if (m_position_local == position)
            return;

        m_position_local = position;
        UpdateTransform();
    }
#endregion

#region ROTATION ======================================================================
    public Quaternion GetRotation() { return m_matrix.GetRotation(); }
    public Quaternion GetRotationLocal() { return m_rotation_local; }
    public void SetRotation(Quaternion rotation)
    {
        // compute local rotation without using unstable GetRotation() decomposition
        Quaternion local_rotation;
        if (GetParent() == null)
            local_rotation = rotation;
        else
        {
            // compute parent's world rotation by composing local rotations up the hierarchy
            // world_rot = root_local * ... * parent_local (compose from root down)
            List<Quaternion> rotations = new List<Quaternion>();
            Entity ancestor = GetParent();
            while (ancestor != null)
            {
                rotations.Add(ancestor.GetRotationLocal());
                ancestor = ancestor.GetParent();
            }

            // compose from root (back of vector) to parent (front of vector)
            Quaternion parent_world_rotation = Quaternion.Identity;
            foreach (Quaternion it in rotations)
                parent_world_rotation = parent_world_rotation * it;

            local_rotation = parent_world_rotation.Inverse() * rotation;
        }

        SetRotationLocal(local_rotation);
    }
    public void SetRotationLocal(Quaternion rotation)
    {
        if (m_rotation_local == rotation)
            return;

        m_rotation_local = rotation;
        UpdateTransform();
    }
#endregion

#region SCALE =========================================================================
    public Vector3 GetScale() { return m_matrix.GetScale(); }
    public Vector3 GetScaleLocal() { return m_scale_local; }
    public void SetScale(Vector3 scale)
    {
        if (GetScale() == scale)
            return;

        SetScaleLocal((GetParent() == null) ? scale : scale / GetParent().GetScale());
    }
    public void SetScaleLocal(Vector3 scale)
    {
        if (m_scale_local == scale)
            return;

        m_scale_local = scale;

        // a scale of 0 will cause a division by zero when decomposing the world transform matrix
        m_scale_local.x = (m_scale_local.x == 0.0f) ? float.MinValue : m_scale_local.x;
        m_scale_local.y = (m_scale_local.y == 0.0f) ? float.MinValue : m_scale_local.y;
        m_scale_local.z = (m_scale_local.z == 0.0f) ? float.MinValue : m_scale_local.z;

        UpdateTransform();
    }
#endregion

#region TRANSLATION/ROTATION ======================================
    public void Translate(Vector3 delta)
    {
        if (GetParent() == null)
            SetPositionLocal(m_position_local + delta);
        else
        {
            // go through SetPosition so the world-space point (not direction) is correctly
            // converted to local space - Matrix * Vector3 treats the vector as a point (w=1),
            // so we must not pass a displacement directly through the inverse parent matrix
            SetPosition(GetPosition() + delta);
        }
    }
    public void Rotate(Quaternion delta)
    {
        if (GetParent() == null)
            SetRotationLocal((delta * m_rotation_local).Normalized());
        else
            SetRotationLocal(GetParent().GetRotation().Inverse() * delta * GetParent().GetRotation() * m_rotation_local);
    }
#endregion

#region DIRECTIONS ================================================
    public Vector3 GetUp() { return m_up; }
    public Vector3 GetDown() { return m_down; }
    public Vector3 GetForward() { return m_forward; }
    public Vector3 GetBackward() { return m_backward; }
    public Vector3 GetRight() { return m_right; }
    public Vector3 GetLeft() { return m_left; }
#endregion

#region HIERARCHY ===================================================================================
    public void SetParent(Entity? new_parent)
    {

        lock (m_mutex_parent)
        {
            if (new_parent != null)
            {
                // early exit if the parent is this entity
                if (GetObjectId() == new_parent.GetObjectId())
                    return;

                // early exit if the parent is already set
                if ((m_parent != null) && (m_parent.GetObjectId() == new_parent.GetObjectId()))
                    return;

                // if the new parent is a descendant of this transform (e.g. dragging and dropping an entity onto one of it's children)
                if (new_parent.IsDescendantOf(this))
                {
                    foreach (Entity child in m_children)
                    {
                        child.m_parent = m_parent; // directly setting parent
                        child.UpdateTransform();   // update transform if needed
                    }

                    m_children.Clear();
                }
            }

            // remove the this as a child from the existing parent
            if (m_parent != null)
            {
                bool update_child_with_null_parent = false;
                m_parent.RemoveChild(this, update_child_with_null_parent);
            }

            // add this is a child to new parent
            if (new_parent != null)
            {
                new_parent.AddChild(this);
            }

            m_parent = new_parent;
            UpdateTransform();
        }
    }
    public Entity? GetChildByIndex(int index)
    {
        if (!HasChildren() || index >= GetChildrenCount())
            return null;
        return m_children[index];
    }

    public Entity? GetChildByName(string name)
    {
        foreach (Entity child in m_children)
        {
            if (child.GetObjectName() == name)
                return child;
        }
        return null;
    }
    // searches the entire hierarchy, finds any children and saves them in m_children
    // this is a recursive function, the children will also find their own children and so on
    public void AcquireChildren()
    {
        lock (m_mutex_children)
        {
            m_children.Clear();

            List<Entity>? entities = null; //TODO = World::GetEntities();
            foreach (Entity? possible_child in entities)
            {
                if ((possible_child == null) || (possible_child.GetParent() == null) || (possible_child.GetObjectId() == GetObjectId()))
                    continue;

                // if it's parent matches this transform
                if (possible_child.GetParent().GetObjectId() == GetObjectId())
                {
                    // welcome home son
                    m_children.Add(possible_child);

                    // make the child do the same thing all over, essentially resolving the entire hierarchy
                    possible_child.AcquireChildren();
                }
            }
        }
    }
    public void RemoveChild(Entity child, bool update_child_with_null_parent)
    {
        Helpers.PLXAssert(child != null);

        // ensure the transform is not itself
        if (child.GetObjectId() == GetObjectId())
            return;

        lock (m_mutex_children)
        {
            // remove the child
            m_children.RemoveAll(c => c.GetObjectId() == child.GetObjectId());

            // remove the child's parent
            if (update_child_with_null_parent)
                child.SetParent(null);
        }
    }
    public void AddChild(Entity child)
    {
        Helpers.PLXAssert(child != null);

        // ensure that the child is not this transform
        if (child.GetObjectId() == GetObjectId())
            return;

        lock (m_mutex_children)
        {
            // if this is not already a child, add it
            if (!m_children.Contains(child))
                m_children.Add(child);
        }
    }
    public void MoveChildToIndex(Entity child, int index)
    {
        Helpers.PLXAssert(child != null);
        lock (m_mutex_children)
        {
            // find the child in the list and get current position before removing
            int currentIndex = m_children.IndexOf(child);

            if (currentIndex == -1)
                return; // child not found

            // Remove from current position (shifts everything after it)
            m_children.RemoveAt(currentIndex);

            // Adjust target index if the removed child was before the desired position
            // (removing it shifts all subsequent indices down by 1)
            if (currentIndex < index && index > 0)
                index--;

            // Clamp index to valid range [0, Count]
            if (index > m_children.Count)
                index = m_children.Count;

            // Insert at the new position
            m_children.Insert(index, child);
        }
    }
    public bool IsDescendantOf(Entity transform)
    {

        Helpers.PLXAssert(transform != null);

        if (m_parent == null)
            return false;

        if (m_parent.GetObjectId() == transform.GetObjectId())
            return true;

        List<Entity> childrens = transform.GetChildren();
        foreach (Entity child in childrens)
        {
            if (IsDescendantOf(child))
                return true;
        }

        return false;
    }
    public void GetDescendants(ref List<Entity> descendants)
    {
        foreach (Entity child in m_children)
        {
            descendants.Add(child);
            if (child.HasChildren())
                child.GetDescendants(ref descendants);
        }
    }
    public Entity? GetDescendantByName(string name)
    {
        List<Entity> descendants = new List<Entity>();
        GetDescendants(ref descendants);

        foreach (Entity entity in descendants)
        {
            if (entity.GetObjectName() == name)
                return entity;
        }
        return null;
    }
    public bool HasChildren() { return m_children.Count > 0; }
    public int GetChildrenCount() { return m_children.Count; }
    public Entity GetRoot() { return (m_parent != null) ? GetParent().GetRoot() : this; }
    public Entity GetParent() { return m_parent; }
    public List<Entity> GetChildren() { return m_children; }
    #endregion
    public void SetTransient(bool transient) { m_transient = transient; }
    public bool IsTransient() { return m_transient; }

    // input is an entity, output is a clone of that entity (descendant entities are not cloned)
    public Entity clone_entity(Entity entity)
    {
        // clone basic properties
        Entity? clone = null; //TODO World::CreateEntity();
        clone.SetObjectName(entity.GetObjectName());
        clone.SetActive(entity.GetActive());
        clone.SetPosition(entity.GetPositionLocal());
        clone.SetRotation(entity.GetRotationLocal());
        clone.SetScale(entity.GetScaleLocal());

        // clone all the components
        Component?[] allComponents = entity.GetAllComponents();
        foreach (Component? component_original in allComponents)
        {
            if (component_original != null)
            {
                // component
                Component component_clone = clone.AddComponent(component_original.GetComponentType());

                // component's properties
                component_clone.SetAttributes(component_original.GetAttributes());
            }
        }

        return clone;
    }

    // input is an entity, output is a clone of that entity (descendant entities are cloned)
    private Entity clone_entity_and_descendants(Entity entity)
    {
        Entity clone_self = clone_entity(entity);

        // clone children make them call this lambda
        if (entity.GetChildren().Count != 0)
        {
            foreach (Entity child_transform in entity.GetChildren())
            {
                Entity clone_child = clone_entity_and_descendants(child_transform);
                clone_child.SetParent(clone_self);
            }
        }
        return clone_self;
    }
    public Entity Clone()
    {
        return clone_entity_and_descendants(this);
    }
    public Component? AddComponent(ComponentType type)
    {
        Component? component;
        switch(type)
        {
/* TODO           case ComponentType.AudioSource:
                component = AddComponent<AudioSource>();
            break;
            case ComponentType.Camera:
                component = AddComponent<Camera>();
            break;
            case ComponentType.Light:
                component = AddComponent<Light>();
            break;
            case ComponentType.Physics:
                component = AddComponent<Physics>();
            break;
            case ComponentType.Render:
                component = AddComponent<Render>();
            break;
            case ComponentType.Spline:
                component = AddComponent<Spline>();
            break;
            case ComponentType.SplineFollower:
                component = AddComponent<SplineFollower>();
            break;
            case ComponentType.Terrain:
                component = AddComponent<Terrain>();
            break;
            case ComponentType.Volume:
                component = AddComponent<Volume>();
            break;
            case ComponentType.Script:
                component = AddComponent<Script>();
            break;
            case ComponentType.ParticleSystem:
                component = AddComponent<ParticleSystem>();
            break;
 */           default:
                component = null;
            break;
        }

        Helpers.PLXAssert(component != null);

        return component;
    }
    public T? AddComponent<T>() where T : Component, new()
    {
        ComponentType type = Component.TypeToEnum<T>();

        // early exit if the component already exists
        if (GetComponent<T>() is T existing)
            return existing;

        // create new component
        T component = new T();

        // save new component (same as your m_components array)
        m_components[(int)type] = component;

        // initialize
        component.SetComponentType(type);
        component.Initialize();

        return component;
    }

    public Component? AddComponentByType(ComponentType type)
    {
        // Early exit if it already exists (exact same logic as your C++)
        if (GetComponentByType(type) is Component existing)
            return existing;

        Component? component;
        switch (type)
        {
/*TODO            case ComponentType.AudioSource:
                component = new AudioSource();
                break;
            case ComponentType.Camera:
                component = new Camera();
                break;
            case ComponentType.Light:
                component = new Light();
                break;
            case ComponentType.Physics:
                component = new Physics();
                break;
            case ComponentType.Render:
                component = new Render();
                break;
            case ComponentType.Spline:
                component = new Spline();
                break;
            case ComponentType.SplineFollower:
                component = new SplineFollower();
                break;
            case ComponentType.Terrain:
                component = new Terrain();
                break;
            case ComponentType.Volume:
                component = new Volume();
            break;
            case ComponentType.Script:
                component = new Script();
            break;
            case ComponentType.ParticleSystem:
                component = new ParticleSystem();
                break;
*/            default:
                component = null;
            break;
        }
        if (component is null)
            return null;

        // Save it in the array
        m_components[(int)type] = component;

        // Initialize
        component.SetComponentType(type);
        component.Initialize();

        return component;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemoveComponentByType(ComponentType type) { m_components[(int)type] = null; }
    public void RemoveComponent<T>() where T : Component { RemoveComponentByType(Component.TypeToEnum<T>()); }
    public void RemoveComponentById(ulong id)
    {
        for (int i = 0; i < m_components.Count(); i++)
        {
            Component? component = m_components[i];
            if (component != null)
            {
                if (id == component.GetObjectId())
                {
                    component.Remove();
                    component = null;
                    return;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Component? GetComponentByType(ComponentType type) { return m_components[(int)type]; }
    public T? GetComponent<T>() where T : Component
    {
        ComponentType type = Component.TypeToEnum<T>();
        return GetComponentByType(type) as T;
    }

    public void Start()
    {
        foreach (Component? component in m_components)
        {
            if (component != null)
                component.Start();
        }
    }
    public void Stop()
    {
        foreach (Component? component in m_components)
        {
            if (component != null)
                component.Stop();
        }
    }
    public void PreTick()
    {
        foreach (Component? component in m_components)
        {
            if (component != null)
                component.PreTick();
        }
    }
    public void Tick()
    {
        foreach (Component? component in m_components)
        {
            if (component != null)
                component.Tick();
        }
        //TODO ADD TIME
//        m_time_since_last_transform_sec += Timer.GetDeltaTimeSec());
    }
}