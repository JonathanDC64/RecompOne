using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Memory;

public sealed class PSMemory : IMemory
{
    private readonly byte[] _ram = new byte[Runtime.Mode == RunMode.Devkit ? MemoryMap.DevkitRamSize : MemoryMap.RetailRamSize];
    private readonly byte[] _scratchpad = new byte[MemoryMap.ScratchpadSize];
    private readonly byte[] _hwregs = new byte[MemoryMap.HwRegsSize];
    private readonly byte[] _bios = new byte[MemoryMap.BiosSize];

    private readonly Gpu _gpu = new();
    private readonly Spu _spu = new();
    private readonly Mdec _mdec = new();
    private readonly Timers _timers = new();
    private readonly Dma _dma;
    private CdController? _cd;

    public ReadOnlySpan<byte> Ram => _ram;
    internal byte[] RamBuffer => _ram;

    public PSMemory()
    {
        _dma = new Dma(this, _gpu, _spu, _mdec, () => Runtime.DispatchIrq(3));
        Runtime.Gpu = _gpu;
        Runtime.Spu = _spu;
        Bios.KromFont.InstallInto(_bios);
    }

    public void SetCd(CdController cd) { _cd = cd; _dma.SetCd(cd); }

    private static bool IsDmaChcr(uint phys) => phys >= 0x1F801080u && phys < 0x1F8010F0u && (phys & 0xFu) == 8u;

    private uint Hw32(uint phys)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        return (uint)(_hwregs[o] | (_hwregs[o + 1] << 8) | (_hwregs[o + 2] << 16) | (_hwregs[o + 3] << 24));
    }

    private void Hw32(uint phys, uint v)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        _hwregs[o] = (byte)v;
        _hwregs[o + 1] = (byte)(v >> 8);
        _hwregs[o + 2] = (byte)(v >> 16);
        _hwregs[o + 3] = (byte)(v >> 24);
    }

    // Debug write-watch (KF2_WATCH=hexPhysStart,hexPhysEnd): print the writer of
    // any write into the range. Deduped by writer function (each distinct func
    // printed once) so a long play session or a per-frame writer (e.g. player
    // position) doesn't flood the log or hide a rarer writer behind a hit cap.
    static readonly (uint lo, uint hi)? _watch = ParseWatch();
    static readonly HashSet<string> _watchSeen = new();
    // KF2_WATCH_AFTER=seconds: arm the watch only after N seconds, so boot-time
    // writers (stack regions are reused by everything) don't fill the dedup cap.
    static readonly System.Diagnostics.Stopwatch _watchClock = System.Diagnostics.Stopwatch.StartNew();
    static readonly long _watchAfterMs =
        long.TryParse(Environment.GetEnvironmentVariable("KF2_WATCH_AFTER"), out var _wa) ? _wa * 1000 : 0;
    static (uint, uint)? ParseWatch()
    {
        var s = Environment.GetEnvironmentVariable("KF2_WATCH");
        if (string.IsNullOrEmpty(s)) return null;
        var p = s.Split(',');
        return (Convert.ToUInt32(p[0], 16), Convert.ToUInt32(p[1], 16));
    }

    private void TrackWrite(uint phys, int size)
    {
        if (_watch is { } w && phys >= w.lo && phys < w.hi && _watchSeen.Count < 200
            && _watchClock.ElapsedMilliseconds >= _watchAfterMs)
        {
            var st = new System.Diagnostics.StackTrace(false);
            // Collect the top few recompiled frames (call chain), so a generic
            // leaf writer (memset/copy) doesn't hide the real caller logic.
            var chain = new List<string>(4);
            for (int i = 1; i < st.FrameCount && chain.Count < 4; i++)
            {
                var mth = st.GetFrame(i)?.GetMethod();
                if (mth != null && (mth.Name.StartsWith("func_") || mth.Name.StartsWith("map_fn_") || mth.Name.StartsWith("ind_")))
                    chain.Add(mth.Name);
            }
            string who = chain.Count > 0 ? string.Join(" <- ", chain) : "?";
            if (_watchSeen.Add($"{who}:{size}"))
                Console.WriteLine($"[watch] write phys=0x{phys:X8} size={size} by {who} (distinct #{_watchSeen.Count})");
        }
        if (phys < MemoryMap.RamWindow)
        {
            uint off = phys % (uint)_ram.Length;
            Runtime.RamLog.RecordWrite(phys % (uint)_ram.Length, size);
            Dispatcher.NotifyWrite(off);
        }

    }

    private void TrackRead(uint phys, int size)
    {
        if (RamLogger.TrackReads && phys < MemoryMap.RamWindow)
            Runtime.RamLog.RecordRead(phys % (uint)_ram.Length, size);
    }

    private Span<byte> Resolve(uint address, int size)
    {
        uint phys = MemoryMap.ToPhysical(address);

        if (phys < MemoryMap.RamWindow)
            return _ram.AsSpan((int)(phys % (uint)_ram.Length), size);

        if (phys >= MemoryMap.ScratchpadBase && phys < MemoryMap.ScratchpadBase + MemoryMap.ScratchpadSize)
            return _scratchpad.AsSpan((int)(phys - MemoryMap.ScratchpadBase), size);

        if (phys >= MemoryMap.HwRegsBase && phys < MemoryMap.HwRegsBase + MemoryMap.HwRegsSize)
            return _hwregs.AsSpan((int)(phys - MemoryMap.HwRegsBase), size);

        if (phys >= MemoryMap.BiosBase && phys < MemoryMap.BiosBase + MemoryMap.BiosSize)
            return _bios.AsSpan((int)(phys - MemoryMap.BiosBase), size);

        throw new InvalidOperationException($"unmapped address: 0x{address:X8}");
    }

    private static bool IsCd(uint phys) => phys >= 0x1F801800u && phys <= 0x1F801803u;
    private static bool IsSpu(uint phys) => phys >= 0x1F801C00u && phys < 0x1F801E80u;

    public byte ReadU8(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 1);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        return Resolve(address, 1)[0];
    }

    public ushort ReadU16(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 2);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (IsSpu(phys)) return _spu.ReadReg16(phys);
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return (ushort)tv;
        var s = Resolve(address, 2);
        return (ushort)(s[0] | (s[1] << 8));
    }

    public uint ReadU32(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 4);
        if (phys == 0x1F801810u) return _gpu.ReadData();
        if (phys == 0x1F801814u) return _gpu.ReadStat();
        if (phys == 0x1F801820u) return _mdec.ReadData();
        if (phys == 0x1F801824u) return _mdec.ReadStatus();
        if (phys == 0x1F8010F4u) return _dma.ReadDicr();
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (IsSpu(phys)) return (uint)(_spu.ReadReg16(phys) | (_spu.ReadReg16(phys + 2) << 16));
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return tv;
        var s = Resolve(address, 4);
        return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
    }

    public void WriteU8(uint address, byte value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 1);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, value); return; }
        Resolve(address, 1)[0] = value;
    }

    public void WriteU16(uint address, ushort value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 2);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, value); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 2);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
    }

    public void WriteU32(uint address, uint value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 4);
        if (phys == 0x1F801810u) { _gpu.WriteGp0(value); return; }
        if (phys == 0x1F801814u) { _gpu.WriteGp1(value); return; }
        if (phys == 0x1F801820u) { _mdec.Write0(value); return; }
        if (phys == 0x1F801824u) { _mdec.WriteControl(value); return; }
        if (phys == 0x1F8010F4u) { _dma.WriteDicr(value); return; }
        if (IsDmaChcr(phys) && (value & 0x01000000u) != 0)
        {
            Hw32(phys, value & ~0x01000000u);
            _dma.Run((int)((phys - 0x1F801080u) / 0x10u), Hw32(phys - 8u), Hw32(phys - 4u), value);
            return;
        }
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, (ushort)value); _spu.WriteReg16(phys + 2, (ushort)(value >> 16)); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 4);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
        s[2] = (byte)(value >> 16);
        s[3] = (byte)(value >> 24);
    }

    public uint ReadWordLeft(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0x00FFFFFFu >> shift)) | (word << (24 - shift));
    }

    public uint ReadWordRight(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0xFFFFFF00u << (24 - shift))) | (word >> shift);
    }

    public void WriteWordLeft(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0xFFFFFF00u << shift)) | (value >> (24 - shift)));
    }

    public void WriteWordRight(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0x00FFFFFFu >> (24 - shift))) | (value << shift));
    }

    public void LoadBytes(uint address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            WriteU8(address + (uint)i, data[i]);
    }

    public void ZeroRange(uint address, uint length)
    {
        for (uint i = 0; i < length; i++)
            WriteU8(address + i, 0);
    }
}
