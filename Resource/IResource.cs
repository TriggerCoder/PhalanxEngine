using System;

namespace Phalanx;

public enum ResourceType
{
    Unknown,
    Texture,
    Audio,
    Material,
    Mesh,
    Cubemap,
    Animation,
    Font,
    Shader,
    Max
}
public enum ResourceState: uint
{
    LoadingFromDrive,
    PreparingForGpu,
    PreparedForGpu,
    Max
}

[Serializable]
public abstract class IResource : SpartanObject
{
    private uint _m_resource_state = (uint)ResourceState.Max;
    protected ResourceType m_resource_type = ResourceType.Max;
    protected ResourceState m_resource_state
    {
        get => (ResourceState)Volatile.Read(ref _m_resource_state);
        set => Volatile.Write(ref _m_resource_state, (uint)value);
    }
    protected uint m_flags = 0;

    private string m_resource_file_path = string.Empty;

    public void SetResourceFilePath(string path)
    {
        m_resource_file_path = FileSystem.GetRelativePath(path);
        m_object_name = FileSystem.GetFileNameWithoutExtensionFromFilePath(m_resource_file_path);
    }

    public void SetResourceName(string name)
    {
        m_object_name = name;

        // Reconstruct path: directory of previous path + new name
        string directory = FileSystem.GetDirectoryFromFilePath(m_resource_file_path);
        m_resource_file_path = directory + name;
    }

    public ResourceType GetResourceType() { return m_resource_type; }
    public virtual string GetResourceTypeCstr() { return GetType().Name; }
    public string GetResourceFilePath() { return m_resource_file_path; }
    public string GetResourceDirectory() { return FileSystem.GetDirectoryFromFilePath(m_resource_file_path); }

    // flags
    public void SetFlag(uint flag, bool enabled = true)
    {
        if (enabled)
            m_flags |= flag;
        else
            m_flags &= ~flag;
    }
    public uint GetFlags() { return m_flags; }
    public void SetFlags(uint flags) { m_flags = flags; }
    public virtual ResourceType TypeToEnum<T>() where T : IResource { return ResourceType.Unknown; }
    public ResourceState GetResourceState() { return m_resource_state; }
}