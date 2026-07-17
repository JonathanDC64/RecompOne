namespace RecompOne.Runtime;

// PGXP geometry correction, modeled on DuckStation's implementation:
//
//  1. The GTE keeps the sub-pixel screen coordinates (and depth) it computed
//     BEFORE truncating to the 16-bit SXY registers (a precise FIFO parallel to
//     SX/SY).
//  2. When the game reads an SXY register (MFC2/SWC2), the precise coords are
//     stashed as "pending", keyed by the packed value.
//  3. When that exact value is then stored to RAM (building a GPU primitive),
//     the RAM address is recorded -> precise coords (address-keyed map).
//  4. When the GPU's DMA feeds a polygon command, each vertex word carries its
//     RAM source address; the rasterizer looks the address up and — after
//     validating the stored value still matches — uses the precise floats and
//     perspective-correct W instead of the snapped integers.
//
// Address-primary lookup means CPU-computed vertices (billboard corners, skybox,
// HUD) are simply left uncorrected instead of getting a colliding vertex's
// coordinates — that was the source of the "warping in the distance" artifacts
// with the earlier value-keyed cache. A value-keyed fallback is available as an
// option (like DuckStation's "vertex cache") but is off by default.
public static class Pgxp
{
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("KF2_PGXP") == "1";
    public static bool ValueFallback { get; set; } =
        Environment.GetEnvironmentVariable("KF2_PGXP_VC") == "1";
    // Sub-options (DuckStation parity): perspective-correct interpolation of
    // textures (on by default) and vertex colors (off — games often use gouraud
    // for fog/lighting tuned to affine interpolation).
    public static bool PerspectiveTextures { get; set; } = true;
    public static bool PerspectiveColors { get; set; }
    // Compute the game's NCLIP from precise coordinates so sliver "stitch"
    // triangles (zero area at integer precision) aren't culled — without this
    // they vanish and leave hairline gaps ("black lines") between polygons.
    public static bool CullingCorrection { get; set; } = true;

    // Flags: bit0 = X half valid, bit1 = Y half valid (3 = full vertex).
    struct P { public float X, Y, W; public uint Value; public int Stamp; public byte Flags; }
    const byte FX = 1, FY = 2, FXY = 3;

    // address (physical RAM offset) -> precise vertex; validated by Value on lookup.
    // Two generations: on overflow the current map is demoted instead of cleared —
    // live entries get re-recorded every frame and migrate seamlessly, so eviction
    // never causes a visible "pop" (a wholesale clear made polygons snap back to
    // vanilla for a frame every couple of seconds).
    static Dictionary<uint, P> _addrMap = new(1 << 16);
    static Dictionary<uint, P> _addrMapOld = new(1 << 16);
    // optional value-keyed fallback (collision-prone; off by default)
    static readonly Dictionary<uint, P> _valueMap = new(1 << 16);
    const int MaxEntries = 1 << 17;

    // Per-CPU-register precision tags (indexed by MIPS register number). The
    // recompiler emits RegLw/RegSw/RegMfc2/Swc2 calls on the instructions that
    // move vertex data, so precision follows REGISTER IDENTITY — two in-flight
    // vertices with identical packed values can never contaminate each other
    // (the flaw of value-keyed matching). Tags are validated by value at every
    // use, so a stale tag after untracked arithmetic self-rejects; this is the
    // same scheme DuckStation's default (non-CPU) PGXP mode uses.
    static readonly P[] _regTag = new P[32];

    const uint NoKey = 0xFFFFFFFFu;

    static uint KeyOf(uint addr)
    {
        uint phys = Memory.MemoryMap.ToPhysical(addr);
        if (phys < Memory.MemoryMap.RamWindow) return phys & 0x1FFFFFu;
        if (phys >= Memory.MemoryMap.ScratchpadBase && phys < Memory.MemoryMap.ScratchpadBase + Memory.MemoryMap.ScratchpadSize)
            return 0x200000u + (phys - Memory.MemoryMap.ScratchpadBase);
        return NoKey;
    }

    // lw rt, imm(rs) — the register inherits the precision tracked at the address.
    public static void RegLw(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        uint key = KeyOf(addr);
        if (key != NoKey && value != 0
            && (_addrMap.TryGetValue(key, out var p) || _addrMapOld.TryGetValue(key, out p))
            && p.Flags == FXY && p.Value == value)
            _regTag[rt] = p;
        else
            _regTag[rt] = default;
    }

    // lh/lhu rt, imm(rs) — half of a vertex (PS1 prims store X,Y as consecutive
    // shorts, so half-copies are THE standard vertex-building pattern). The
    // register inherits that half's precision by identity.
    public static void RegLh(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        uint wordKey = KeyOf(addr & ~3u);
        int half = (int)(addr & 2); // 0 = X half, 2 = Y half
        if (wordKey != NoKey
            && (_addrMap.TryGetValue(wordKey, out var p) || _addrMapOld.TryGetValue(wordKey, out p))
            && (p.Flags & (half == 0 ? FX : FY)) != 0
            && (ushort)(half == 0 ? p.Value : p.Value >> 16) == (ushort)value)
        {
            _regTag[rt] = new P
            {
                X = half == 0 ? p.X : 0, Y = half == 0 ? 0 : p.Y, W = p.W,
                Value = (ushort)value, Stamp = p.Stamp, Flags = half == 0 ? FX : FY,
            };
        }
        else
            _regTag[rt] = default;
    }

    // sh rt, imm(rs) — half-vertex store: merge the half into the target word's
    // entry; when both halves land, the entry becomes a full vertex.
    public static void RegSh(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        ref var t = ref _regTag[rt];
        float coord; float w; int stamp;
        if ((t.Flags == FX || t.Flags == FY) && (ushort)t.Value == (ushort)value)
        { coord = t.Flags == FX ? t.X : t.Y; w = t.W; stamp = t.Stamp; }
        else if (t.Flags == FXY && (ushort)t.Value == (ushort)value)
        { coord = t.X; w = t.W; stamp = t.Stamp; } // low half of a full vertex = X
        else return;

        uint wordKey = KeyOf(addr & ~3u);
        if (wordKey == NoKey) return;
        int half = (int)(addr & 2);

        if (!_addrMap.TryGetValue(wordKey, out var e)) e = default;
        if (half == 0)
        {
            e.X = coord;
            e.Value = (e.Value & 0xFFFF0000u) | (ushort)value;
            e.Flags |= FX;
        }
        else
        {
            e.Y = coord;
            e.Value = (e.Value & 0x0000FFFFu) | ((uint)(ushort)value << 16);
            e.Flags |= FY;
        }
        e.W = w;
        e.Stamp = stamp;
        if (_addrMap.Count >= MaxEntries)
        {
            (_addrMapOld, _addrMap) = (_addrMap, _addrMapOld);
            _addrMap.Clear();
        }
        _addrMap[wordKey] = e;
        MarkTracked(wordKey);
    }

    // sw rt, imm(rs) — a register carrying a valid full tag plants it at the
    // address (exact identity). If the tag is stale — KF2 repacks vertices through
    // arithmetic that identity tracking can't follow — fall back to the value
    // table: same-packed-value ambiguity bounds any error to under a pixel, and
    // 96% coverage with rare sub-pixel error looks far better than the 30%
    // coverage identity-only achieves on this engine.
    public static void RegSw(int rt, uint addr, uint value)
    {
        if (!Enabled || value == 0) return;
        ref var p = ref _regTag[rt];
        if (p.Flags == FXY && p.Value == value) { Plant(addr, p); return; }
        if (_valueMap.TryGetValue(value, out var q) && q.Flags == FXY) Plant(addr, q);
    }

    static void Plant(uint addr, in P p)
    {
        uint key = KeyOf(addr);
        if (key == NoKey) return;
        if (_addrMap.Count >= MaxEntries)
        {
            (_addrMapOld, _addrMap) = (_addrMap, _addrMapOld);
            _addrMap.Clear();
        }
        _addrMap[key] = p;
        MarkTracked(key);
    }

    // mfc2 rt, SXY0/1/2 — the register receives the GTE's precise coordinates.
    public static void RegMfc2(int rt, int gteReg, uint value)
    {
        if (!Enabled) return;
        int slot = gteReg >= 14 ? 2 : gteReg - 12;
        Gte.GetPrecise(slot, out float x, out float y, out float w);
        _regTag[rt] = new P { X = x, Y = y, W = w, Value = value, Stamp = FrameStamp, Flags = FXY };
        RecordValue(value, x, y, w);
    }

    // mtc2 rt, SXY0/1/2 — the game reloads a stored vertex into the GTE (typically
    // right before NCLIP). Restore its precision so culling correction works on
    // real sub-pixel coordinates (DuckStation's CPU_MTC2 equivalent).
    public static void RegMtc2(int rt, int gteReg, uint value)
    {
        if (!Enabled || value == 0) return;
        int slot = gteReg >= 14 ? 2 : gteReg - 12;
        ref var p = ref _regTag[rt];
        if (p.Flags == FXY && p.Value == value)
            Gte.SetPrecise(slot, p.X, p.Y, p.W);
        else if (_valueMap.TryGetValue(value, out var q) && q.Flags == FXY)
            Gte.SetPrecise(slot, q.X, q.Y, q.W);
    }

    // lwc2 SXY0/1/2, imm(rs) — vertex loaded from memory straight into the GTE.
    public static void Lwc2(int gteReg, uint addr, uint value)
    {
        if (!Enabled || value == 0) return;
        int slot = gteReg >= 14 ? 2 : gteReg - 12;
        uint key = KeyOf(addr);
        if (key != NoKey
            && (_addrMap.TryGetValue(key, out var p) || _addrMapOld.TryGetValue(key, out p))
            && p.Flags == FXY && p.Value == value)
            Gte.SetPrecise(slot, p.X, p.Y, p.W);
        else if (_valueMap.TryGetValue(value, out var q) && q.Flags == FXY)
            Gte.SetPrecise(slot, q.X, q.Y, q.W);
    }

    // swc2 SXY0/1/2, imm(rs) — GTE register stored straight to memory.
    public static void Swc2(int gteReg, uint addr)
    {
        if (!Enabled) return;
        uint key = KeyOf(addr);
        if (key == NoKey) return;
        int slot = gteReg >= 14 ? 2 : gteReg - 12;
        Gte.GetPrecise(slot, out float x, out float y, out float w);
        uint packed = Gte.PackedSxy(slot);
        if (packed == 0) return;
        if (_addrMap.Count >= MaxEntries)
        {
            (_addrMapOld, _addrMap) = (_addrMap, _addrMapOld);
            _addrMap.Clear();
        }
        _addrMap[key] = new P { X = x, Y = y, W = w, Value = packed, Stamp = FrameStamp, Flags = FXY };
        MarkTracked(key);
        RecordValue(packed, x, y, w);
    }

    // Advanced by the runtime once per presented frame; used to reject stale
    // weld entries (a last-frame coordinate welded into an animated model's
    // triangle shows up as cracks on NPCs/moving objects).
    public static int FrameStamp;

    static void RecordValue(uint packed, float x, float y, float w)
    {
        if (packed == 0) return;
        if (_valueMap.Count >= MaxEntries) _valueMap.Clear();
        _valueMap[packed] = new P { X = x, Y = y, W = w, Value = packed, Stamp = FrameStamp, Flags = FXY };
    }

    // Geometry tolerance, DuckStation semantics: max px distance between precise
    // and snapped coordinate, < 0 = disabled (their default). Value-keyed matches
    // are bounded to < 1px by construction (same packed value = same integer
    // pixel), so with the check disabled only screen-edge CLAMPED vertices can
    // sit far from their packed coords — the Guard below rejects those.
    public static float Tolerance { get; set; } = -1f;
    const float Guard = 64f;

    public static long Hits, Misses; // diagnostics; reset with the [prim] stats
    public static long MissNoAddr, MissNoEntry, MissValue, MissTol; // miss breakdown
    static int _missLog;

    // --- GTE side -----------------------------------------------------------

    // Called when the game reads SXY0/1/2 out of the GTE (register-tag tracking
    // handles the main path; this keeps the weld table fed).
    public static void StashPending(uint packed, float x, float y, float w)
        => RecordValue(packed, x, y, w);

    // Weld helper: precise coords for a vertex by its packed value only. Used for
    // polygons that already have address-verified vertices — same-integer-position
    // corners get consistent precise coords, closing hairline cracks between
    // corrected neighbours. Never applied to fully CPU-authored polygons.
    public static bool WeldLookup(uint vertexWord, out float x, out float y, out float w)
    {
        x = y = 0; w = 1;
        if (!Enabled) return false;
        if (!_valueMap.TryGetValue(vertexWord, out var p) || !WithinTolerance(p, vertexWord))
            return false;
        if (FrameStamp - p.Stamp > 3) return false; // stale: wrong for animated geometry
        x = p.X; y = p.Y; w = p.W;
        return true;
    }

    // --- memory side --------------------------------------------------------

    // One bit per 4-byte word over RAM (2MB) + scratchpad (keys 0x200000+): marks
    // addresses that MAY hold a tracked vertex, so the hot read path can skip the
    // dictionary for everything else.
    static readonly ulong[] _trackedBits = new ulong[(0x200000 + 0x400) >> 8];

    static void MarkTracked(uint key) => _trackedBits[key >> 8] |= 1ul << (int)((key >> 2) & 63);
    static bool MaybeTracked(uint key) => (_trackedBits[key >> 8] & (1ul << (int)((key >> 2) & 63))) != 0;

    // --- GPU side -----------------------------------------------------------

    public static bool Lookup(uint srcAddr, uint vertexWord, out float x, out float y, out float w)
    {
        x = y = 0; w = 1;
        if (!Enabled) return false;

        P p;
        if (srcAddr == 0) MissNoAddr++;
        else if ((!_addrMap.TryGetValue(srcAddr, out p) || p.Flags != FXY) && (!_addrMapOld.TryGetValue(srcAddr, out p) || p.Flags != FXY))
        {
            MissNoEntry++;
            if (_missLog < 24)
            { Console.WriteLine($"[pgxp] miss addr=0x{srcAddr:X6} word=0x{vertexWord:X8}"); _missLog++; }
        }
        else if (p.Value != vertexWord) MissValue++;
        else if (!WithinTolerance(p, vertexWord)) MissTol++;
        else
        {
            x = p.X; y = p.Y; w = p.W;
            Hits++;
            return true;
        }

        if (ValueFallback && _valueMap.TryGetValue(vertexWord, out p) && WithinTolerance(p, vertexWord))
        {
            x = p.X; y = p.Y; w = p.W;
            Hits++;
            return true;
        }

        Misses++;
        return false;
    }

    static bool WithinTolerance(in P p, uint word)
    {
        int ix = (short)(word & 0xFFFF);
        int iy = (short)(word >> 16);
        float dx = Math.Abs(p.X - ix), dy = Math.Abs(p.Y - iy);
        if (dx > Guard || dy > Guard) return false; // clamped/garbage vertex
        return Tolerance < 0f || (dx <= Tolerance && dy <= Tolerance);
    }

    public static void ResetStats() { Hits = Misses = MissNoAddr = MissNoEntry = MissValue = MissTol = 0; }
}
