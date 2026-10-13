using System;

namespace Phalanx;

[Serializable]
public class SpartanObject
{
    protected string m_object_name = string.Empty;
    protected ulong m_object_id = 0;
    protected ulong m_object_size = 0;

    public SpartanObject()
    {
        // stack-only, deterministic pseudo-random ID
        ulong timeNow = (ulong)System.Diagnostics.Stopwatch.GetTimestamp();

        // simple stack-safe thread-unique value
        uint threadUnique = (uint)Environment.CurrentManagedThreadId;

        ulong randomValue = (timeNow ^ threadUnique) * 2654435761U;
        randomValue ^= (randomValue >> 16);
        m_object_id = randomValue;
    }

    // name
    public string GetObjectName()    { return m_object_name; }
    public void SetObjectName(string name) { m_object_name = name; }

    // id
    public ulong GetObjectId()  { return m_object_id; }
    public void SetObjectId(ulong id) { m_object_id = id; }

    // sizes
    public ulong GetObjectSize() { return m_object_size; }
}
