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
//
// CPU MODE (DuckStation cpu_pgxp.cpp port): precision also propagates through
// CPU ARITHMETIC. KF2 builds/repacks vertex words through arithmetic — pack
// X-low|Y-high via `sll t,sy,16` + `or v,sx,t`, offset via `addiu` — which pure
// identity tags can't follow (the tag's Value goes stale and the vertex falls
// back to integers = the residual ~2.4% seam tail). The Cpu* handlers below
// model 16-bit signed halves with carry (f16Sign/f16Unsign/f16Overflow), so a
// register's precise X/Y halves survive shifting/masking/or-combining and the
// repacked word plants a fully precise vertex. Handlers are emitted by the
// recompiler BEFORE each arithmetic op (operands still hold pre-op values) and
// are self-validating: a tag whose Value doesn't match the live register value
// drops its precision, exactly like DuckStation's PGXPValue::Validate.
public static class Pgxp
{
    static bool _enabled = Environment.GetEnvironmentVariable("KF2_PGXP") == "1";
    static bool _cpuMode = Environment.GetEnvironmentVariable("KF2_PGXP_NOCPU") != "1";

    public static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; CpuOn = _enabled && _cpuMode; }
    }

    // CPU-mode arithmetic propagation (DuckStation "PGXP CPU mode").
    public static bool CpuMode
    {
        get => _cpuMode;
        set { _cpuMode = value; CpuOn = _enabled && _cpuMode; }
    }

    // Fast gate read by recompiled code before every Cpu* call (a static-field
    // load + branch when off). Kept in sync by the Enabled/CpuMode setters.
    public static bool CpuOn = _enabled && _cpuMode;

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

    // Flags (DuckStation VALID_X/Y/Z + VALID_TAINTED_Z; LOWZ/HIGHZ not tracked —
    // KF2 never splits depth through halfword stores on the vertex path).
    struct P { public float X, Y, W; public uint Value; public int Stamp; public uint Flags; }
    const uint FX = 1, FY = 2, FZ = 4, FXY = 3, FXYZ = 7, TZ = 0x80000000u;

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

    // Per-CPU-register precision tags (indexed by MIPS register number; slots
    // 32/33 = the LO/HI multiply-divide registers, DuckStation Reg::lo/hi). The
    // recompiler emits RegLw/RegSw/RegMfc2/Swc2 calls on the instructions that
    // move vertex data — and, in CPU mode, Cpu* calls on the arithmetic that
    // transforms it — so precision follows REGISTER IDENTITY. Tags are validated
    // by value at every use, so a stale tag after any untracked op self-rejects;
    // this is DuckStation's scheme exactly.
    static readonly P[] _regTag = new P[34];
    const int LO = 32, HI = 33;

    const uint NoKey = 0xFFFFFFFFu;

    static uint KeyOf(uint addr)
    {
        uint phys = Memory.MemoryMap.ToPhysical(addr);
        if (phys < Memory.MemoryMap.RamWindow) return phys & 0x1FFFFFu;
        if (phys >= Memory.MemoryMap.ScratchpadBase && phys < Memory.MemoryMap.ScratchpadBase + Memory.MemoryMap.ScratchpadSize)
            return 0x200000u + (phys - Memory.MemoryMap.ScratchpadBase);
        return NoKey;
    }

    // --- DuckStation float16 helpers (cpu_pgxp.cpp f16Sign/f16Unsign/f16Overflow)

    static double F16Sign(double v) => (double)(int)(long)(v * 65536.0) / 65536.0;
    static double F16Unsign(double v) => v >= 0.0 ? v : v + 65536.0;
    static double F16Overflow(double v) => (double)((long)v >> 16);

    static float ValidX(in P p, uint val) => (p.Flags & FX) != 0 ? p.X : (short)(val & 0xFFFF);
    static float ValidY(in P p, uint val) => (p.Flags & FY) != 0 ? p.Y : (short)(val >> 16);

    // Tag whose Value doesn't match the live register value has been changed by
    // an untracked op — drop its precision (DuckStation PGXPValue::Validate).
    static void Validate(ref P p, uint val) { if (p.Value != val) p.Flags = 0; }

    static void CopyZIfMissing(ref P dst, in P src)
    {
        if ((dst.Flags & FZ) == 0) dst.W = src.W;
        dst.Flags |= src.Flags & FZ;
    }

    static void SelectZ(ref float w, ref uint flags, in P s1, in P s2)
    {
        w = ((s1.Flags & FZ) == 0 ||
             ((s1.Flags & TZ) != 0 && (s2.Flags & (FZ | TZ)) == FZ)) ? s2.W : s1.W;
        flags |= (s1.Flags | s2.Flags) & FZ;
    }

    // lw rt, imm(rs) — the register inherits the precision tracked at the address
    // (partial halves included), validated against the loaded value.
    public static void RegLw(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        uint key = KeyOf(addr);
        if (key != NoKey
            && (_addrMap.TryGetValue(key, out var p) || _addrMapOld.TryGetValue(key, out p))
            && p.Value == value && p.Flags != 0)
        {
            _regTag[rt] = p;
            return;
        }
        if (_swLog is { } r && _swLogged < 60 && key >= r.lo && key < r.hi)
        {
            bool found = _addrMap.TryGetValue(key, out var q) || _addrMapOld.TryGetValue(key, out q);
            Console.WriteLine($"[pgxp-lw] MISS rt={rt} addr=0x{key:X6} val=0x{value:X8} entry={(found ? $"val=0x{q.Value:X8} flags={q.Flags:X}" : "NONE")}");
            _swLogged++;
        }
        _regTag[rt] = new P { Value = value };
    }

    // lh/lhu rt, imm(rs) — half of a vertex (PS1 prims store X,Y as consecutive
    // shorts, so half-copies are THE standard vertex-building pattern). The
    // 16-bit value lands in the LOW half of the register, so the precise coord
    // goes to the tag's X component regardless of which half of the word it was
    // (DuckStation ValidateAndLoadMem16); Y becomes the sign-extension constant.
    public static void RegLh(int rt, uint addr, uint value, bool sign)
    {
        if (!Enabled) return;
        uint wordKey = KeyOf(addr & ~3u);
        P dest = default;
        dest.Value = value;
        if (wordKey != NoKey
            && (_addrMap.TryGetValue(wordKey, out var p) || _addrMapOld.TryGetValue(wordKey, out p)))
        {
            bool hi = (addr & 2) != 0;
            // validate only the half being read
            if (hi) { if ((ushort)(p.Value >> 16) != (ushort)value) p.Flags &= ~FY; }
            else { if ((ushort)p.Value != (ushort)value) p.Flags &= ~FX; }

            dest = p;
            if (hi)
            {
                dest.X = dest.Y;
                dest.Flags = (dest.Flags & ~FX) | ((dest.Flags & FY) >> 1);
            }
            // only claim a valid Y (the sign/zero-extended high half) if X is valid
            if ((dest.Flags & FX) != 0)
            {
                dest.Y = (sign && dest.X < 0f) ? -1f : 0f;
                dest.Flags |= FY;
            }
            else
            {
                dest.Y = 0f;
                dest.Flags &= ~FY;
            }
            dest.Value = value;
        }
        _regTag[rt] = dest;
    }

    // sh rt, imm(rs) — half-vertex store: the register's X component lands in
    // whichever half of the target word the address selects (DuckStation
    // WriteMem16); when both halves land, the entry becomes a full vertex.
    public static void RegSh(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        ref var t = ref _regTag[rt];
        Validate(ref t, value);

        uint wordKey = KeyOf(addr & ~3u);
        if (wordKey == NoKey) return;
        bool hi = (addr & 2) != 0;

        if (!_addrMap.TryGetValue(wordKey, out var e))
            _addrMapOld.TryGetValue(wordKey, out e); // migrate the old-gen entry if any

        if (hi)
        {
            e.Y = t.X;
            e.Flags = (e.Flags & ~FY) | ((t.Flags & FX) << 1);
            e.Value = (e.Value & 0x0000FFFFu) | (value << 16);
        }
        else
        {
            e.X = t.X;
            e.Flags = (e.Flags & ~FX) | (t.Flags & FX);
            e.Value = (e.Value & 0xFFFF0000u) | (value & 0xFFFFu);
        }
        if ((t.Flags & FZ) != 0) { e.W = t.W; e.Flags |= FZ; }
        if (t.Stamp > e.Stamp) e.Stamp = t.Stamp;

        if ((e.Flags & FXY) == 0) { _addrMap.Remove(wordKey); _addrMapOld.Remove(wordKey); return; }
        if (_addrMap.Count >= MaxEntries)
        {
            (_addrMapOld, _addrMap) = (_addrMap, _addrMapOld);
            _addrMap.Clear();
        }
        _addrMap[wordKey] = e;
        MarkTracked(wordKey);
    }

    // sw rt, imm(rs) — a register carrying a valid full tag plants it at the
    // address (exact identity). If the tag is stale — arithmetic CPU mode didn't
    // cover — fall back to the value table: same-packed-value ambiguity bounds
    // any error to under a pixel, and high coverage with rare sub-pixel error
    // looks far better than identity-only misses. A store with no precision
    // clears any stale entry at the address (DuckStation writes the invalid
    // value through; equivalent hygiene).
    // Diagnostics (KF2_PGXP_SWLOG=hexLo,hexHi): log stores into the range whose
    // register tag failed to plant, with the tag state — pinpoints which copy
    // idiom loses precision.
    static readonly (uint lo, uint hi)? _swLog = ParseSwLog();
    static int _swLogged;
    static (uint, uint)? ParseSwLog()
    {
        var s = Environment.GetEnvironmentVariable("KF2_PGXP_SWLOG");
        if (string.IsNullOrEmpty(s)) return null;
        var p = s.Split(',');
        return (Convert.ToUInt32(p[0], 16), Convert.ToUInt32(p[1], 16));
    }

    public static void RegSw(int rt, uint addr, uint value)
    {
        if (!Enabled) return;
        ref var p = ref _regTag[rt];
        Validate(ref p, value);
        if ((p.Flags & FXY) == FXY) { Plant(addr, p); return; }
        if (!CpuOn && value != 0 && _valueMap.TryGetValue(value, out var q) && (q.Flags & FXY) == FXY) { Plant(addr, q); return; }
        if (_swLog is { } r && _swLogged < 60)
        {
            uint k = KeyOf(addr);
            if (k >= r.lo && k < r.hi)
            {
                Console.WriteLine($"[pgxp-sw] FAIL rt={rt} addr=0x{k:X6} val=0x{value:X8} tagVal=0x{p.Value:X8} tagFlags={p.Flags:X}");
                _swLogged++;
            }
        }
        uint key = KeyOf(addr);
        if (key != NoKey && MaybeTracked(key)) { _addrMap.Remove(key); _addrMapOld.Remove(key); }
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
        _regTag[rt] = new P { X = x, Y = y, W = w, Value = value, Stamp = FrameStamp, Flags = FXYZ };
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
        if ((p.Flags & FXY) == FXY && p.Value == value)
            Gte.SetPrecise(slot, p.X, p.Y, p.W);
        else if (!CpuOn && _valueMap.TryGetValue(value, out var q) && (q.Flags & FXY) == FXY)
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
            && (p.Flags & FXY) == FXY && p.Value == value)
            Gte.SetPrecise(slot, p.X, p.Y, p.W);
        else if (!CpuOn && _valueMap.TryGetValue(value, out var q) && (q.Flags & FXY) == FXY)
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
        _addrMap[key] = new P { X = x, Y = y, W = w, Value = packed, Stamp = FrameStamp, Flags = FXYZ };
        MarkTracked(key);
        RecordValue(packed, x, y, w);
    }

    // --- CPU-mode arithmetic handlers (DuckStation cpu_pgxp.cpp CPU_*) ---------
    //
    // All are emitted by the recompiler BEFORE the C# operation, guarded by
    // `if (Pgxp.CpuOn)`, with the operands' PRE-op values (in-place ops would
    // otherwise clobber their source). Each handler computes the result value
    // itself, so the tag's Value always matches the register after the op.

    // addi/addiu rt, rs, imm (imm already sign-extended to 32 bits)
    public static void CpuAddi(int rt, int rs, uint rsVal, uint imm)
    {
        ref var prs = ref _regTag[rs];
        Validate(ref prs, rsVal);
        P prt = prs;
        if (imm != 0)
        {
            if (rsVal == 0)
            {
                // li rt, imm — a constant gets a low-precision but VALID tag so
                // later combines (CPU_BITWISE half-matching) can use it.
                prt.X = (short)(imm & 0xFFFF);
                prt.Y = (short)(imm >> 16);
                prt.Flags |= FXY | TZ;
                prt.Value = imm;
                prt.Stamp = FrameStamp;
            }
            else
            {
                double x = F16Unsign(prt.X) + (ushort)(imm & 0xFFFF);
                float of = x > 65535.0 ? 1f : x < 0.0 ? -1f : 0f;
                prt.X = (float)F16Sign(x);
                prt.Y += (short)(imm >> 16) + of;
                if (prt.Y > short.MaxValue) prt.Y -= 65536f;
                else if (prt.Y < short.MinValue) prt.Y += 65536f;
                prt.Value = rsVal + imm;
                prt.Flags |= TZ;
            }
        }
        _regTag[rt] = prt;
    }

    // add/addu rd, rs, rt
    public static void CpuAdd(int rd, int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P prd;
        if (rtVal == 0) { prd = prs; CopyZIfMissing(ref prd, prt); }
        else if (rsVal == 0) { prd = prt; CopyZIfMissing(ref prd, prs); }
        else
        {
            double x = F16Unsign(ValidX(prs, rsVal)) + F16Unsign(ValidX(prt, rtVal));
            float of = x > 65535.0 ? 1f : x < 0.0 ? -1f : 0f;
            prd = default;
            prd.X = (float)F16Sign(x);
            prd.Y = ValidY(prs, rsVal) + ValidY(prt, rtVal) + of;
            if (prd.Y > short.MaxValue) prd.Y -= 65536f;
            else if (prd.Y < short.MinValue) prd.Y += 65536f;
            prd.Value = rsVal + rtVal;
            prd.Flags = prs.Flags | (prt.Flags & FXY) | TZ;
            SelectZ(ref prd.W, ref prd.Flags, prs, prt);
            prd.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        }
        _regTag[rd] = prd;
    }

    // sub/subu rd, rs, rt
    public static void CpuSub(int rd, int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P prd;
        if (rtVal == 0) { prd = prs; CopyZIfMissing(ref prd, prt); }
        else
        {
            double x = F16Unsign(ValidX(prs, rsVal)) - F16Unsign(ValidX(prt, rtVal));
            float of = x > 65535.0 ? 1f : x < 0.0 ? -1f : 0f;
            prd = default;
            prd.X = (float)F16Sign(x);
            prd.Y = ValidY(prs, rsVal) - (ValidY(prt, rtVal) - of);
            if (prd.Y > short.MaxValue) prd.Y -= 65536f;
            else if (prd.Y < short.MinValue) prd.Y += 65536f;
            prd.Value = rsVal - rtVal;
            prd.Flags = prs.Flags | (prt.Flags & FXY) | TZ;
            SelectZ(ref prd.W, ref prd.Flags, prs, prt);
            prd.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        }
        _regTag[rd] = prd;
    }

    // and/or/xor/nor rd, rs, rt (DuckStation CPU_BITWISE): each 16-bit half of
    // the result that passed through unchanged keeps its source's precision —
    // this is the `or v, sx, t` vertex-pack combine.
    static void Bitwise(int rd, int rs, int rt, uint rdVal, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);

        float x;
        if ((ushort)rdVal == 0) x = 0f;
        else if ((ushort)rdVal == (ushort)rsVal) x = ValidX(prs, rsVal);
        else if ((ushort)rdVal == (ushort)rtVal) x = ValidX(prt, rtVal);
        else x = (short)(rdVal & 0xFFFF);

        float y;
        if ((rdVal >> 16) == 0) y = 0f;
        else if ((rdVal >> 16) == (rsVal >> 16)) y = ValidY(prs, rsVal);
        else if ((rdVal >> 16) == (rtVal >> 16)) y = ValidY(prt, rtVal);
        else y = (short)(rdVal >> 16);

        uint flags = ((prs.Flags | prt.Flags) & FXY) != 0 ? (FXY | TZ) : 0u;
        P prd = default;
        SelectZ(ref prd.W, ref flags, prs, prt);
        prd.X = x;
        prd.Y = y;
        prd.Flags = flags;
        prd.Value = rdVal;
        prd.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        _regTag[rd] = prd;
    }

    public static void CpuAnd(int rd, int rs, int rt, uint rsVal, uint rtVal) => Bitwise(rd, rs, rt, rsVal & rtVal, rsVal, rtVal);
    public static void CpuOr(int rd, int rs, int rt, uint rsVal, uint rtVal) => Bitwise(rd, rs, rt, rsVal | rtVal, rsVal, rtVal);
    public static void CpuXor(int rd, int rs, int rt, uint rsVal, uint rtVal) => Bitwise(rd, rs, rt, rsVal ^ rtVal, rsVal, rtVal);
    public static void CpuNor(int rd, int rs, int rt, uint rsVal, uint rtVal) => Bitwise(rd, rs, rt, ~(rsVal | rtVal), rsVal, rtVal);

    // andi rt, rs, imm
    public static void CpuAndi(int rt, int rs, uint rsVal, uint imm)
    {
        ref var prs = ref _regTag[rs];
        Validate(ref prs, rsVal);
        uint rtVal = rsVal & imm;
        P p = prs;
        p.Y = 0f;
        p.Value = rtVal;
        p.Flags = prs.Flags | FY | TZ;
        if (imm == 0) { p.X = 0f; p.Flags |= FX; }
        else if (imm == 0xFFFFu) { /* x keeps rs precision */ }
        else { p.X = (short)(rtVal & 0xFFFF); p.Flags |= FX; }
        _regTag[rt] = p;
    }

    // ori rt, rs, imm
    public static void CpuOri(int rt, int rs, uint rsVal, uint imm)
    {
        ref var prs = ref _regTag[rs];
        Validate(ref prs, rsVal);
        P p = prs;
        p.Value = rsVal | imm;
        if (imm != 0) { p.X = (short)((rsVal | imm) & 0xFFFF); p.Flags |= FX | TZ; }
        _regTag[rt] = p;
    }

    // xori rt, rs, imm
    public static void CpuXori(int rt, int rs, uint rsVal, uint imm)
    {
        ref var prs = ref _regTag[rs];
        Validate(ref prs, rsVal);
        P p = prs;
        p.Value = rsVal ^ imm;
        if (imm != 0) { p.X = (short)((rsVal ^ imm) & 0xFFFF); p.Flags |= FX | TZ; }
        _regTag[rt] = p;
    }

    // lui rt, imm — value already shifted (full 32-bit result).
    public static void CpuLui(int rt, uint value)
    {
        _regTag[rt] = new P { X = 0f, Y = (short)(value >> 16), W = 0f, Value = value, Stamp = FrameStamp, Flags = FXY };
    }

    // sll rd, rt, sa / sllv rd, rt, rs — sh==16 is THE vertex-pack move: the
    // register's X half becomes the result's Y half.
    public static void CpuSll(int rd, int rt, uint rtVal, int sh)
    {
        ref var prt = ref _regTag[rt];
        Validate(ref prt, rtVal);
        P prd = default;
        prd.W = prt.W;
        prd.Value = rtVal << sh;
        prd.Stamp = prt.Stamp;
        if (sh == 16)
        {
            prd.Y = prt.X;
            prd.X = 0f;
            prd.Flags = (prt.Flags | TZ) | ((prt.Flags & FY) >> 1);
        }
        else if (sh > 16)
        {
            prd.Y = (float)F16Sign(F16Unsign(prt.X * (double)(1 << (sh - 16))));
            prd.X = 0f;
            prd.Flags = (prt.Flags | TZ) | ((prt.Flags & FY) >> 1);
        }
        else
        {
            double x = F16Unsign(prt.X) * (double)(1 << sh);
            double y = F16Unsign(prt.Y) * (double)(1 << sh) + F16Overflow(x);
            prd.X = (float)F16Sign(x);
            prd.Y = (float)F16Sign(y);
            prd.Flags = prt.Flags | TZ;
        }
        _regTag[rd] = prd;
    }

    public static void CpuSllv(int rd, int rt, uint rtVal, uint rsVal) => CpuSll(rd, rt, rtVal, (int)(rsVal & 31u));

    // srl/sra rd, rt, sa and srlv/srav (DuckStation CPU_SRx).
    static void Srx(int rd, int rt, uint rtVal, int sh, bool sign, bool isVariable)
    {
        ref var prt = ref _regTag[rt];
        Validate(ref prt, rtVal);
        if (sh == 0) { _regTag[rd] = prt; return; }

        uint rdVal = sign ? (uint)((int)rtVal >> sh) : rtVal >> sh;

        double x = prt.X;
        double y = sign ? prt.Y : F16Unsign(prt.Y);

        uint iX = (uint)(int)(short)rtVal;                    // sign-extended low half (Y removed)
        uint iY = (rtVal & 0xFFFF0000u) | ((iX >> 16) & 0xFFFFu); // low half replaced by sign(x)

        uint dX = (uint)((int)iX >> sh);
        uint dY = sign ? (uint)((int)iY >> sh) : iY >> sh;

        if ((short)(dX & 0xFFFF) != (short)(iX >> 16))
            x = x / (double)(1 << sh);
        else
            x = (short)(dX & 0xFFFF); // only sign bits left

        if ((short)(dY & 0xFFFF) != (short)(iX >> 16))
        {
            if (sh == 16)
            {
                x = y;
            }
            else if (sh < 16)
            {
                x += y * (double)(1 << (16 - sh));
                if (prt.X < 0) x += (double)(1 << (16 - sh));
            }
            else
            {
                x += y / (double)(1 << (sh - 16));
            }
        }

        if ((short)(dY >> 16) == 0 || (short)(dY >> 16) == -1)
            y = (short)(dY >> 16);
        else
            y = y / (double)(1 << sh);

        P prd = default;
        // Low-precision result when we're not shifting an entire component and
        // the source wasn't 3D (no valid Z) — DuckStation's false-positive guard.
        if (sign && !isVariable && (prt.Flags & FZ) == 0 && sh < 16)
        {
            prd.X = (short)(rdVal & 0xFFFF);
            prd.Y = (short)(rdVal >> 16);
            prd.W = 0f;
            prd.Value = rdVal;
            prd.Flags = FXY | TZ;
            prd.Stamp = FrameStamp;
        }
        else
        {
            prd.X = (float)F16Sign(x);
            prd.Y = (float)F16Sign(y);
            prd.W = prt.W;
            prd.Value = rdVal;
            prd.Flags = prt.Flags | TZ;
            prd.Stamp = prt.Stamp;
        }
        _regTag[rd] = prd;
    }

    public static void CpuSrl(int rd, int rt, uint rtVal, int sh) => Srx(rd, rt, rtVal, sh, false, false);
    public static void CpuSra(int rd, int rt, uint rtVal, int sh) => Srx(rd, rt, rtVal, sh, true, false);
    public static void CpuSrlv(int rd, int rt, uint rtVal, uint rsVal) => Srx(rd, rt, rtVal, (int)(rsVal & 31u), false, true);
    public static void CpuSrav(int rd, int rt, uint rtVal, uint rsVal) => Srx(rd, rt, rtVal, (int)(rsVal & 31u), true, true);

    // --- LO/HI tracking (DuckStation CPU_MULT/MULTU/DIV/DIVU + CPU_MOVE). KF2
    // clips far geometry on the CPU: the clip-edge intersection vertices are
    // interpolated with mult/div, and CPU-authored 3D (the HUD compass) rotates
    // through mult — without this, exactly those vertices fall back to integers
    // (the distance-boundary seams and the compass wobble).

    static void Move(int rd, int rs, uint rsVal)
    {
        ref var prs = ref _regTag[rs];
        Validate(ref prs, rsVal);
        _regTag[rd] = prs;
    }

    public static void CpuMfhi(int rd, uint hiVal) => Move(rd, HI, hiVal);
    public static void CpuMflo(int rd, uint loVal) => Move(rd, LO, loVal);
    public static void CpuMthi(int rs, uint rsVal) => Move(HI, rs, rsVal);
    public static void CpuMtlo(int rs, uint rsVal) => Move(LO, rs, rsVal);

    public static void CpuMult(int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P plo = prs;
        CopyZIfMissing(ref plo, prt);
        plo.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        P phi = plo;

        double rsx = ValidX(prs, rsVal), rsy = ValidY(prs, rsVal);
        double rtx = ValidX(prt, rtVal), rty = ValidY(prt, rtVal);

        double xx = F16Unsign(rsx) * F16Unsign(rtx);
        double xy = F16Unsign(rsx) * rty;
        double yx = rsy * F16Unsign(rtx);
        double yy = rsy * rty;

        double lx = xx;
        double ly = F16Overflow(xx) + (xy + yx);
        double hx = F16Overflow(ly) + yy;
        double hy = F16Overflow(hx);

        plo.X = (float)F16Sign(lx);
        plo.Y = (float)F16Sign(ly);
        plo.Flags |= TZ | (prt.Flags & FXY);
        phi.X = (float)F16Sign(hx);
        phi.Y = (float)F16Sign(hy);
        phi.Flags |= TZ | (prt.Flags & FXY);

        ulong result = (ulong)((long)(int)rsVal * (long)(int)rtVal);
        phi.Value = (uint)(result >> 32);
        plo.Value = (uint)result;
        _regTag[LO] = plo;
        _regTag[HI] = phi;
    }

    public static void CpuMultu(int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P plo = prs;
        CopyZIfMissing(ref plo, prt);
        plo.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        P phi = plo;

        double rsx = ValidX(prs, rsVal), rsy = ValidY(prs, rsVal);
        double rtx = ValidX(prt, rtVal), rty = ValidY(prt, rtVal);

        double xx = F16Unsign(rsx) * F16Unsign(rtx);
        double xy = F16Unsign(rsx) * F16Unsign(rty);
        double yx = F16Unsign(rsy) * F16Unsign(rtx);
        double yy = F16Unsign(rsy) * F16Unsign(rty);

        double lx = xx;
        double ly = F16Overflow(xx) + (xy + yx);
        double hx = F16Overflow(ly) + yy;
        double hy = F16Overflow(hx);

        plo.X = (float)F16Sign(lx);
        plo.Y = (float)F16Sign(ly);
        plo.Flags |= TZ | (prt.Flags & FXY);
        phi.X = (float)F16Sign(hx);
        phi.Y = (float)F16Sign(hy);
        phi.Flags |= TZ | (prt.Flags & FXY);

        ulong result = (ulong)rsVal * rtVal;
        phi.Value = (uint)(result >> 32);
        plo.Value = (uint)result;
        _regTag[LO] = plo;
        _regTag[HI] = phi;
    }

    public static void CpuDiv(int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P plo = prs;
        CopyZIfMissing(ref plo, prt);
        plo.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        P phi = plo;

        double vs = F16Unsign(ValidX(prs, rsVal)) + ValidY(prs, rsVal) * 65536.0;
        double vt = F16Unsign(ValidX(prt, rtVal)) + ValidY(prt, rtVal) * 65536.0;

        double lo = vs / vt;
        plo.Y = (float)F16Sign(F16Overflow(lo));
        plo.X = (float)F16Sign(lo);
        plo.Flags |= TZ | (prt.Flags & FXY);

        double hi = vs % vt;
        phi.Y = (float)F16Sign(F16Overflow(hi));
        phi.X = (float)F16Sign(hi);
        phi.Flags |= TZ | (prt.Flags & FXY);

        if ((int)rtVal == 0)
        {
            plo.Value = (int)rsVal >= 0 ? 0xFFFFFFFFu : 1u;
            phi.Value = rsVal;
        }
        else if (rsVal == 0x80000000u && (int)rtVal == -1)
        {
            plo.Value = 0x80000000u;
            phi.Value = 0;
        }
        else
        {
            plo.Value = (uint)((int)rsVal / (int)rtVal);
            phi.Value = (uint)((int)rsVal % (int)rtVal);
        }
        _regTag[LO] = plo;
        _regTag[HI] = phi;
    }

    public static void CpuDivu(int rs, int rt, uint rsVal, uint rtVal)
    {
        ref var prs = ref _regTag[rs];
        ref var prt = ref _regTag[rt];
        Validate(ref prs, rsVal);
        Validate(ref prt, rtVal);
        P plo = prs;
        CopyZIfMissing(ref plo, prt);
        plo.Stamp = Math.Max(prs.Stamp, prt.Stamp);
        P phi = plo;

        double vs = F16Unsign(ValidX(prs, rsVal)) + F16Unsign(ValidY(prs, rsVal)) * 65536.0;
        double vt = F16Unsign(ValidX(prt, rtVal)) + F16Unsign(ValidY(prt, rtVal)) * 65536.0;

        double lo = vs / vt;
        plo.Y = (float)F16Sign(F16Overflow(lo));
        plo.X = (float)F16Sign(lo);
        plo.Flags |= TZ | (prt.Flags & FXY);

        double hi = vs % vt;
        phi.Y = (float)F16Sign(F16Overflow(hi));
        phi.X = (float)F16Sign(hi);
        phi.Flags |= TZ | (prt.Flags & FXY);

        if (rtVal == 0)
        {
            plo.Value = 0xFFFFFFFFu;
            phi.Value = rsVal;
        }
        else
        {
            plo.Value = rsVal / rtVal;
            phi.Value = rsVal % rtVal;
        }
        _regTag[LO] = plo;
        _regTag[HI] = phi;
    }

    // Advanced by the runtime once per presented frame; used to reject stale
    // weld entries (a last-frame coordinate welded into an animated model's
    // triangle shows up as cracks on NPCs/moving objects).
    public static int FrameStamp;

    static void RecordValue(uint packed, float x, float y, float w)
    {
        if (packed == 0 || CpuOn) return; // value table unused in CPU mode
        if (_valueMap.Count >= MaxEntries) _valueMap.Clear();
        _valueMap[packed] = new P { X = x, Y = y, W = w, Value = packed, Stamp = FrameStamp, Flags = FXYZ };
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
    // In CPU mode the weld (and every other value-keyed path) is DISABLED: at
    // far distances many distinct vertices snap to the SAME integer pixel, so
    // identical packed words collide constantly and the weld hands a colliding
    // vertex's sub-pixel coords out — deformed polys, dropped slivers and wrong
    // culling exactly at the distance/fog boundary. With arithmetic tracking the
    // identity path resolves everything correctable, so the weld only has
    // collisions left to contribute (DuckStation ships no value path either).
    public static bool WeldLookup(uint vertexWord, out float x, out float y, out float w)
    {
        x = y = 0; w = 1;
        if (!Enabled || CpuOn) return false;
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
        else if ((!_addrMap.TryGetValue(srcAddr, out p) || (p.Flags & FXY) != FXY)
                 && (!_addrMapOld.TryGetValue(srcAddr, out p) || (p.Flags & FXY) != FXY))
        {
            MissNoEntry++;
            if (_missLog < 24)
            { Console.WriteLine($"[pgxp] miss addr=0x{srcAddr:X6} word=0x{vertexWord:X8}"); _missLog++; }
        }
        else if (p.Value != vertexWord) MissValue++;
        else
        {
            // Identity-verified (address + value match): the precise coords are
            // genuinely this vertex's. Like DuckStation, wrap the integer part
            // to the GPU's 11-bit signed range (matches command parsing, which
            // drops the upper bits) instead of rejecting far-offscreen verts —
            // Guard-rejection made polys snap between precise and integer as
            // their offscreen corner crossed the threshold ("bouncing" walls).
            float tx = Truncate11(p.X), ty = Truncate11(p.Y);
            if (Tolerance >= 0f
                && (Math.Abs(tx - (short)(vertexWord & 0xFFFF)) > Tolerance
                    || Math.Abs(ty - (short)(vertexWord >> 16)) > Tolerance))
            {
                MissTol++;
            }
            else
            {
                x = tx; y = ty;
                w = (p.Flags & FZ) != 0 ? p.W : 1f;
                Hits++;
                return true;
            }
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

    // Value-keyed paths (weld, fallback) only: a clamped/saturated packed word
    // can collide across genuinely different positions, so keep the Guard there.
    static bool WithinTolerance(in P p, uint word)
    {
        int ix = (short)(word & 0xFFFF);
        int iy = (short)(word >> 16);
        float dx = Math.Abs(p.X - ix), dy = Math.Abs(p.Y - iy);
        if (dx > Guard || dy > Guard) return false; // clamped/garbage vertex
        return Tolerance < 0f || (dx <= Tolerance && dy <= Tolerance);
    }

    // DuckStation TruncateVertexPosition: wrap the integer part to the GPU's
    // 11-bit signed vertex range (upper bits are dropped by command parsing),
    // preserving the sub-pixel fraction.
    static float Truncate11(float pos)
    {
        int i = (int)pos;
        return ((i << 21) >> 21) + (pos - i);
    }

    static readonly bool MissLogEveryWindow =
        Environment.GetEnvironmentVariable("KF2_PGXP_MISSLOG") == "1";

    public static void ResetStats()
    {
        Hits = Misses = MissNoAddr = MissNoEntry = MissValue = MissTol = 0;
        if (MissLogEveryWindow) { _missLog = 0; _swLogged = 0; } // fresh samples per stats window
    }
}
