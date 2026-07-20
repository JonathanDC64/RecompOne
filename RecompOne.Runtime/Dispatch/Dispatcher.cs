using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using BiosKernel = RecompOne.Runtime.Bios.Bios;

namespace RecompOne.Runtime.Dispatch;

public static class Dispatcher
{
    static readonly Dictionary<string, IOverlay> _registry = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, string> _lbaToName = [];
    static readonly List<string> _active = [];
    static readonly Dictionary<uint, Action<CpuContext, IMemory>> _funcMap = [];
    private static IOverlay? _pending;

    // Content-based activation: several small overlays can be streamed into the
    // SAME base (per-map code overlays), and the game's load path doesn't always
    // route the sector through LibCd, so LBA activation misses. These overlays
    // carry a signature (first two words of their code); when a write lands in
    // their shared region we identify which one is actually resident by matching
    // RAM against the signatures and activate it. Restricted to small overlays
    // (<=64KB) so the large base-sharing EXEs (OPEN/GAME/END) are never matched.
    static readonly List<IOverlay> _contentOverlays = [];
    static uint _cmLo = uint.MaxValue, _cmHi;

    public static void Register(string name, IOverlay overlay)
    {
        _registry[name] = overlay;
        if (overlay.LbaStart >= 0) _lbaToName[overlay.LbaStart] = name;
        if ((overlay.Sig0 != 0 || overlay.Sig1 != 0) && overlay.Size > 0 && overlay.Size <= 0x10000)
        {
            _contentOverlays.Add(overlay);
            uint s = overlay.Base & 0x1FFFFFu;
            if (s < _cmLo) _cmLo = s;
            if (s + overlay.Size > _cmHi) _cmHi = s + overlay.Size;
        }
    }

    public static string[] ActiveNames
    {
        get { lock (_active) return _active.ToArray(); }
    }

    public static bool IsActive(string name)
    {
        lock (_active) return _active.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
    
    public static IReadOnlyDictionary<string, IOverlay> Overlays => _registry;

    public static void LoadByLba(int lba)
    {
        if (!_lbaToName.TryGetValue(lba, out var name)) return;
        var overlay = _registry[name];
        if(overlay.Base == 0) {
            Load(name);
            return;
        }

        // Content-signatured overlays (per-map code sharing one base) are
        // activated solely by matching RAM content in NotifyWrite. Setting
        // _pending here too would fight that: the game re-reads a sibling's LBA
        // (e.g. MAP_START's 12600) and the pending load would flip the overlay
        // back and forth every write. Let content be authoritative for them.
        if ((overlay.Sig0 != 0 || overlay.Sig1 != 0) && overlay.Size > 0 && overlay.Size <= 0x10000)
            return;

        _pending = overlay;
    }

    public static void NotifyWrite(uint phys)
    {
        // A write into a shared per-map-overlay region: identify the resident
        // overlay by signature and activate it (robust to LBA-activation misses).
        if (_contentOverlays.Count > 0 && phys >= _cmLo && phys < _cmHi)
            ResolveByContent();

        var p = _pending;
        if (p == null) return;
        uint start = p.Base & 0x1FFFFFFFu;
        if (phys < start || phys >= start + 0x800u) return;
        _pending = null;
        Load(p.Name);
    }

    static void ResolveByContent()
    {
        var mem = Runtime.Mem;
        if (mem == null) return;
        foreach (var ov in _contentOverlays)
        {
            if (mem.ReadU32(ov.Base) != ov.Sig0 || mem.ReadU32(ov.Base + 4u) != ov.Sig1)
                continue;
            lock (_active) if (_active.Contains(ov.Name)) return; // already resident
            Load(ov.Name);
            return;
        }
    }
    public static void ClearPending() => _pending = null;

    public static void Load(string name)
    {
        if (!_registry.TryGetValue(name, out var overlay))
            throw new KeyNotFoundException($"overlay not registered: {name}");

        bool already;
        lock (_active) already = _active.Remove(name);

        if (!already) HandleRegionOverwrites(overlay);

        lock (_active) _active.Add(name);
        foreach (var (addr, fn) in overlay.Functions)
            _funcMap[addr] = fn;

        if (already) return;
        Runtime.OverlayLog.Record(name, OverlayEventKind.Loaded);
        Console.WriteLine($"[Dispatcher] loaded overlay: {name}");
    }

    static void HandleRegionOverwrites(IOverlay overlay)
    {
        uint newStart = overlay.Base & 0x1FFFFFFFu;
        uint newEnd = newStart + overlay.Size;
        bool hasRegion = overlay.Base != 0 && overlay.Size != 0;

        List<string>? overwritten = null;
        List<(string Name, int Funcs)>? vramCollisions = null;

        lock (_active)
        {
            foreach (var activeName in _active)
            {
                var other = _registry[activeName];
                bool otherHasRegion = other.Base != 0 && other.Size != 0;

                if (hasRegion && otherHasRegion)
                {
                    uint s = other.Base & 0x1FFFFFFFu;
                    uint e = s + other.Size;

                    if (s < newEnd && e > newStart)
                    {
                        if (s >= newStart && e <= newEnd)
                        {
                            overwritten ??= [];
                            overwritten.Add(activeName);
                        }
                        continue;
                    }
                }

                int shared = CountSharedFunctions(overlay, other);
                if (shared > 0)
                {
                    vramCollisions ??= [];
                    vramCollisions.Add((activeName, shared));
                }
            }

            if (overwritten != null)
                foreach (var d in overwritten) _active.Remove(d);
        }

        if (overwritten != null)
        {
            Rebuild();
            foreach (var d in overwritten)
            {
                Runtime.OverlayLog.Record(d, OverlayEventKind.Overwritten, overlay.Name);
                Console.WriteLine($"[Dispatcher] overlay {d} overwritten by {overlay.Name}");
            }
        }

        if (vramCollisions != null)
        {
            foreach (var (otherName, n) in vramCollisions)
            {
                Runtime.OverlayLog.Record(overlay.Name, OverlayEventKind.VramCollision, $"{otherName} ({n} funcs)");
                Console.WriteLine($"[Dispatcher] overlay {overlay.Name} vvram colision with {otherName}: {n} functions");
            }
        }
    }

    static int CountSharedFunctions(IOverlay a, IOverlay b)
    {
        var smaller = a.Functions.Count <= b.Functions.Count ? a : b;
        var larger = ReferenceEquals(smaller, a) ? b : a;

        int n = 0;
        foreach (var addr in smaller.Functions.Keys)
            if (larger.Functions.ContainsKey(addr)) n++;
        return n;
    }

    public static void TryLoad(string name)
    {
        if (_registry.ContainsKey(name))
            Load(name);
    }

    public static void Unload(string name)
    {
        bool removed;
        lock (_active) removed = _active.Remove(name);
        if (!removed) return;
        Rebuild();
        Runtime.OverlayLog.Record(name, OverlayEventKind.Unloaded);
    }

    public static void Call(CpuContext c, IMemory m, uint addr)
    {
        if (BiosKernel.TryDispatch(c, m, addr)) return;
        if (!_funcMap.TryGetValue(addr, out var fn))
        {
            // Games place trivial default handlers in RAM (e.g. KF2 fills its
            // entity vtables with `jr $ra` stubs and patches real handlers in
            // later). Execute those directly instead of failing.
            uint w0 = m.ReadU32(addr), w1 = m.ReadU32(addr + 4u);
            if (w0 == 0x03E00008u)
            {
                if (w1 == 0u) return;                                  // jr $ra; nop
                if ((w1 & 0xFFFF0000u) == 0x24020000u)                 // jr $ra; addiu $v0, $zero, imm
                { c.V0 = (uint)(short)(w1 & 0xFFFFu); return; }
                if (w1 == 0x00001021u) { c.V0 = 0u; return; }          // jr $ra; move $v0, $zero
            }
            var sb = new System.Text.StringBuilder();
            for (uint i = 0; i < 8; i++) sb.Append($"{m.ReadU32(addr + i * 4u):X8} ");
            throw new InvalidOperationException($"unmapped call: 0x{addr:X8} mem=[{sb}]");
        }
        fn(c, m);
    }

    static void Rebuild()
    {
        _funcMap.Clear();
        lock (_active)
        {
            foreach (var name in _active)
                foreach (var (addr, fn) in _registry[name].Functions)
                    _funcMap[addr] = fn;
        }
    }
}
