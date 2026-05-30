using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Dispatch;

public interface IOverlay
{
    string Name { get; }
    IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; }
}
