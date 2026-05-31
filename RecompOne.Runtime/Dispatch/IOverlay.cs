using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Dispatch;

public interface IOverlay
{
    string Name { get; }
    int LbaStart => -1;
    IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; }
}
