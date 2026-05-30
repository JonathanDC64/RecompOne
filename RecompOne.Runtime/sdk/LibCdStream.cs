using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibCdStream
{
    const int HeaderSize = 32;
    const int SlotData = 2016;
    const ushort VideoMagic = 0x0160;

    static uint _statusBase;
    static int _slots;
    static uint _dataBase;
    static int _readIdx;
    static int _streamLba;
    static bool _active;
    
    static readonly Stopwatch _clock = new();
    static int _streamStartLba;

    public static void StSetRing(CpuContext c, IMemory m)
    {
        _statusBase = c.A0;
        _slots = (int)c.A1;
        _dataBase = _statusBase + (uint)(_slots * HeaderSize);
        _readIdx = 0;
        ClearStatuses(m);
        Log.Sdk($"StSetRing base=0x{_statusBase:X8} slots={_slots} data=0x{_dataBase:X8}");
    }

    public static void StClearRing(CpuContext c, IMemory m)
    {
        _readIdx = 0;
        ClearStatuses(m);
        c.V0 = 0;
        Log.Sdk("StClearRing");
    }

    public static void StUnSetRing(CpuContext c, IMemory m)
    {
        _active = false; 
        Log.Sdk("StUnSetRing");
    }

    public static void StSetStream(CpuContext c, IMemory m)
    {
        _active = true;
        _readIdx = 0;
        _streamLba = -1;
        Log.Sdk("StSetStream");
    }

    public static void StSetMask(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StSetMask"); }

    public static void StGetNext(CpuContext c, IMemory m)
    {
        if (!_active || Runtime.Cd == null) { c.V0 = 1; return; }
        if (_streamLba < 0) { _streamLba = LibCd.CurrentLba; _streamStartLba = _streamLba; _clock.Restart(); }
        
        byte[] first;
        int guard = 0;
        while (true)
        {
            first = Runtime.Cd.ReadSectorData(_streamLba, 2048);
            if (Read16(first, 0) == VideoMagic && Read16(first, 4) == 0) break;
            _streamLba++;
            if (++guard > 8192) { Log.Sdk($"StGetNext there no frame start near lba={_streamLba}"); c.V0 = 1; return; }
        }

        int n = Read16(first, 6);
        if (n <= 0 || n > _slots) { c.V0 = 1; return; }
        
        double delivered = _clock.Elapsed.TotalSeconds * LibCd.SectorsPerSecond; //cd pacer
        int needed = (_streamLba - _streamStartLba) + n;
        if (needed > delivered) { c.V0 = 1; return; }

        Log.Sdk($"StGetNext lba={_streamLba} chunks={n} frame#={Read32(first, 8)} readIdx={_readIdx} delivered={delivered:F0}");
        if (_readIdx + n > _slots) _readIdx = 0;
        
        int collected = 0;
        uint lba = (uint)_streamLba;
        while (collected < n)
        {
            byte[] sec = Runtime.Cd.ReadSectorData((int)lba, 2048);
            lba++;
            if (Read16(sec, 0) != VideoMagic) continue;
            uint hdr = _statusBase + (uint)((_readIdx + collected) * HeaderSize);
            uint dat = _dataBase + (uint)((_readIdx + collected) * SlotData);
            for (int j = 0; j < HeaderSize; j++) m.WriteU8(hdr + (uint)j, sec[j]);
            for (int j = 0; j < SlotData; j++) m.WriteU8(dat + (uint)j, sec[HeaderSize + j]);
            collected++;
        }
        _streamLba = (int)lba;

        uint dataPtr = _dataBase + (uint)(_readIdx * SlotData);
        uint hdrPtr = _statusBase + (uint)(_readIdx * HeaderSize);
        m.WriteU32(c.A0, dataPtr);
        m.WriteU32(c.A1, hdrPtr);
        Log.Sdk($"StGetNext ready data=0x{dataPtr:X8} hdr=0x{hdrPtr:X8} w={Read16(first, 0x10)} h={Read16(first, 0x12)}");

        _readIdx += n;
        c.V0 = 0;
    }

    static uint Read32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    public static void StFreeRing(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StFreeRing"); }

    public static void StGetBackloc(CpuContext c, IMemory m) { c.V0 = 0xFFFFFFFFu; Log.Sdk("StGetBackloc"); }

    static void ClearStatuses(IMemory m)
    {
        for (int i = 0; i < _slots; i++)
            m.WriteU16(_statusBase + (uint)(i * HeaderSize), 0);
    }

    static ushort Read16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
}
