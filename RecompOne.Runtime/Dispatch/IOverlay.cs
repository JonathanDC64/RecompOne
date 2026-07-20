using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Dispatch;

public interface IOverlay
{
    string Name { get; }
    int LbaStart => -1;
    uint Base => 0;
    uint Size => 0;
    // First two words of the overlay's code, used to identify which overlay is
    // actually resident when several share a base (streamed per-map code
    // overlays). 0/0 = no signature (content-based activation disabled).
    uint Sig0 => 0;
    uint Sig1 => 0;
    IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; }
}
