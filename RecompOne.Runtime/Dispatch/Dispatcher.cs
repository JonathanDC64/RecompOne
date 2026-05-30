using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using BiosKernel = RecompOne.Runtime.Bios.Bios;

namespace RecompOne.Runtime.Dispatch;

public static class Dispatcher
{
    static readonly Dictionary<string, IOverlay> _registry = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _active = [];
    static readonly Dictionary<uint, Action<CpuContext, IMemory>> _funcMap = [];

    public static void Register(string name, IOverlay overlay) => _registry[name] = overlay;

    public static void Load(string name)
    {
        if (!_registry.TryGetValue(name, out var overlay))
            throw new KeyNotFoundException($"overlay not registered: {name}");
        if (_active.Contains(name)) return;

        var newAddrs = new HashSet<uint>(overlay.Functions.Keys);
        var toUnload = _active
            .Where(n => _registry[n].Functions.Keys.Any(a => newAddrs.Contains(a)))
            .ToList();
        foreach (var n in toUnload) _active.Remove(n);

        _active.Add(name);
        Rebuild();
        Console.WriteLine($"[Dispatcher] loaded overlay: {name}");
    }

    public static void TryLoad(string name)
    {
        if (_registry.ContainsKey(name))
            Load(name);
    }

    public static void Unload(string name)
    {
        if (!_active.Remove(name)) return;
        Rebuild();
    }

    public static void Call(CpuContext c, IMemory m, uint addr)
    {
        if (BiosKernel.TryDispatch(c, m, addr)) return;
        if (_funcMap.TryGetValue(addr, out var fn)) fn(c, m);
        else throw new InvalidOperationException($"unmapped call: 0x{addr:X8}");
    }

    static void Rebuild()
    {
        _funcMap.Clear();
        foreach (var name in _active)
            foreach (var (addr, fn) in _registry[name].Functions)
                _funcMap[addr] = fn;
    }
}
