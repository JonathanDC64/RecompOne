using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibCdStream
{
    const int HeaderSize = 32;
    const int SlotData = 2016;
    const ushort VideoMagic = 0x0160;

    public static bool InUse { get; private set; }
    static uint _statusBase;
    static int _slots;
    static uint _dataBase;

    static volatile bool _active;
    static volatile bool _reading;
    static int _pendingLba = -1;
    static int _streamLba = -1;
    static int _streamStartLba;
    static readonly Stopwatch _clock = new();

    static int _writeIdx;
    static bool[] _busy = System.Array.Empty<bool>();
    static readonly Queue<(int start, int n)> _ready = new();
    static int _prevStart = -1, _prevN;

    static Thread? _thread;
    static volatile bool _run;
    static readonly object _lock = new();

    public static void StSetRing(CpuContext c, IMemory m)
    {
        InUse = true;
        lock (_lock)
        {
            _statusBase = c.A0;
            _slots = (int)c.A1;
            _dataBase = _statusBase + (uint)(_slots * HeaderSize);
            ResetRing(m);
        }
        EnsureThread();
        Log.Sdk($"StSetRing base=0x{_statusBase:X8} slots={_slots} data=0x{_dataBase:X8}");
    }

    public static void StClearRing(CpuContext c, IMemory m)
    {
        lock (_lock) ResetRing(m);
        c.V0 = 0;
        Log.Sdk("StClearRing");
    }

    public static void StUnSetRing(CpuContext c, IMemory m)
    {
        _active = false;
        _reading = false;
        Log.Sdk("StUnSetRing");
    }

    public static void StSetStream(CpuContext c, IMemory m)
    {
        lock (_lock)
        {
            _streamLba = -1;
            ResetRing(m);
            XaAudio.Reset();
        }
        _active = true;
        EnsureThread();
        Log.Sdk("StSetStream");
    }

    public static void StSetMask(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StSetMask"); }

    public static void StGetNext(CpuContext c, IMemory m)
    {
        if (!_active) { c.V0 = 1; return; }

        lock (_lock)
        {
            if (_prevStart >= 0)
            {
                for (int i = 0; i < _prevN; i++) _busy[_prevStart + i] = false;
                _prevStart = -1;
            }

            if (_ready.Count == 0) { c.V0 = 1; return; }

            var (start, n) = _ready.Dequeue();
            uint dataPtr = _dataBase + (uint)(start * SlotData);
            uint hdrPtr = _statusBase + (uint)(start * HeaderSize);
            m.WriteU32(c.A0, dataPtr);
            m.WriteU32(c.A1, hdrPtr);
            _prevStart = start;
            _prevN = n;
            c.V0 = 0;
        }
    }

    public static void StFreeRing(CpuContext c, IMemory m) { c.V0 = 0; Log.Sdk("StFreeRing"); }

    public static void StGetBackloc(CpuContext c, IMemory m) { c.V0 = 0xFFFFFFFFu; Log.Sdk("StGetBackloc"); }

    static readonly bool XaLog = System.Environment.GetEnvironmentVariable("KF2_XALOG") == "1";
    static int _xaSecLog;
    static double _rate = -1; // sectors/sec override (from the hardware CD path); -1 = use LibCd's mode
    static bool _filterOn;
    static byte _filterFile, _filterChannel;

    internal static void SetXaFilter(bool on, byte file, byte channel)
    {
        _filterOn = on; _filterFile = file; _filterChannel = channel;
    }

    internal static void OnReadStream(int lba, double sectorsPerSecond = -1)
    {
        if (!InUse) return;
        _rate = sectorsPerSecond;
        _pendingLba = lba;
        // A ReadS is a stream (re)start: drop any previous stream position so the
        // loop picks up the new LBA and repaces from now. Without this a second
        // movie resumes the first one's position with a long-elapsed clock, so the
        // pacing gate always passes (the "old intro resumes, sped-up" bug).
        lock (_lock)
        {
            _streamLba = -1;
            _ready.Clear();                                  // stale frames of the old stream
            if (_busy.Length > 0) System.Array.Clear(_busy); // (game hasn't consumed them)
            _writeIdx = 0;
            _prevStart = -1;
            _xaSecLog = 0; // re-arm per-stream sector logging
        }
        _reading = true;
        _active = true; // auto-activate: some games fold StSetStream into a combined stream-start fn
        EnsureThread();
    }

    internal static void OnStopStream()
    {
        if (_reading) Log.Sdk("stream stop");
        _reading = false;
    }

    static void ResetRing(IMemory m)
    {
        _writeIdx = 0;
        _prevStart = -1;
        _prevN = 0;
        _ready.Clear();
        _busy = _slots > 0 ? new bool[_slots] : System.Array.Empty<bool>();
        for (int i = 0; i < _slots; i++)
            m.WriteU16(_statusBase + (uint)(i * HeaderSize), 0);
    }

    static void EnsureThread()
    {
        if (_thread is { IsAlive: true }) return;
        _run = true;
        _thread = new Thread(StreamLoop) { IsBackground = true, Name = "CdStream" };
        _thread.Start();
    }

    static void StreamLoop()
    {
        while (_run)
        {
            var cd = Runtime.Cd;
            var m = Runtime.Mem;
            if (cd == null || m == null || !_active || !_reading || _slots <= 0)
            {
                Thread.Sleep(2);
                continue;
            }

            if (_streamLba < 0)
            {
                _streamLba = _pendingLba >= 0 ? _pendingLba : LibCd.CurrentLba;
                _streamStartLba = _streamLba;
                _clock.Restart();
            }

            // Pace EVERY sector to the disc rate. Audio-only streams (e.g. the title
            // jingles L0.S/L1.S) have no video frames to gate on — without this the
            // whole file decodes at disk speed, overflowing the XA ring ("cut off").
            // Run a few sectors AHEAD of real time so the XA buffer keeps a small
            // cushion against thread-sleep jitter (Windows sleeps are ~16ms coarse).
            const double Lead = 8;
            double delivered = _clock.Elapsed.TotalSeconds * (_rate > 0 ? _rate : LibCd.SectorsPerSecond) + Lead;
            if ((_streamLba - _streamStartLba) + 1 > delivered) { Thread.Sleep(1); continue; }

            byte[] sec;
            try { lock (LibCd.DiscLock) sec = cd.ReadSectorData(_streamLba, 2336); }
            catch { Thread.Sleep(2); continue; }

            if ((sec[2] & 0x04) != 0)
            {
                // XA files interleave channels: honour the game's Setfilter so only
                // the selected file/channel plays (decoding all channels mangles it).
                bool pass = !_filterOn || (sec[0] == _filterFile && sec[1] == _filterChannel);
                if (XaLog && _xaSecLog < 40)
                { Console.WriteLine($"[xas] lba={_streamLba} file={sec[0]} ch={sec[1]} sub=0x{sec[2]:X2} coding=0x{sec[3]:X2} pass={pass} filt={_filterOn}/{_filterFile}/{_filterChannel} buf={XaAudio.BufferedSamples}"); _xaSecLog++; }
                if (pass)
                    XaAudio.DecodeSector(sec, 8, sec[3]);
                _streamLba++; continue;
            }
            if (XaLog && _xaSecLog < 40)
            { Console.WriteLine($"[xas] lba={_streamLba} NONAUDIO sub=0x{sec[2]:X2} magic=0x{Read16(sec, 8):X4} buf={XaAudio.BufferedSamples}"); _xaSecLog++; }
            if (Read16(sec, 8) != VideoMagic || Read16(sec, 12) != 0) { _streamLba++; continue; }

            int n = Read16(sec, 14);
            if (n <= 0 || n > _slots) { _streamLba++; continue; }

            if ((_streamLba - _streamStartLba) + n > delivered) { Thread.Sleep(1); continue; }

            int start;
            lock (_lock)
            {
                if (_writeIdx + n > _slots) _writeIdx = 0;
                start = _writeIdx;
                bool free = true;
                for (int i = 0; i < n; i++) if (_busy[start + i]) { free = false; break; }
                if (!free)
                {
                    // The real drive never stalls: if the game isn't consuming frames
                    // (e.g. it only wants the XA audio of this stream — the title
                    // jingles L0/L1.S are STR files whose video is ignored), drop the
                    // oldest undelivered frame and keep streaming. Stalling here
                    // starves the interleaved audio ("jingle cut off" bug).
                    if (_ready.Count > 0)
                    {
                        var (os, on) = _ready.Dequeue();
                        for (int i = 0; i < on; i++) _busy[os + i] = false;
                    }
                    else Thread.Sleep(1); // all frames held by the game — genuinely wait
                    continue;
                }
            }

            if (!CollectFrame(cd, m, start, n)) continue;

            lock (_lock)
            {
                for (int i = 0; i < n; i++) _busy[start + i] = true;
                _ready.Enqueue((start, n));
                _writeIdx = start + n;
            }
        }
    }

    static bool CollectFrame(Cdrom.CdController cd, IMemory m, int start, int n)
    {
        int collected = 0;
        int lba = _streamLba;
        while (collected < n)
        {
            byte[] sec;
            try { lock (LibCd.DiscLock) sec = cd.ReadSectorData(lba, 2336); }
            catch { return false; }
            lba++;

            if ((sec[2] & 0x04) != 0)
            {
                if (!_filterOn || (sec[0] == _filterFile && sec[1] == _filterChannel))
                    XaAudio.DecodeSector(sec, 8, sec[3]);
                continue;
            }
            if (Read16(sec, 8) != VideoMagic) continue;

            uint hdr = _statusBase + (uint)((start + collected) * HeaderSize);
            uint dat = _dataBase + (uint)((start + collected) * SlotData);
            for (int j = 0; j < HeaderSize; j++) m.WriteU8(hdr + (uint)j, sec[8 + j]);
            for (int j = 0; j < SlotData; j++) m.WriteU8(dat + (uint)j, sec[8 + HeaderSize + j]);
            collected++;
        }
        _streamLba = lba;
        Thread.MemoryBarrier();
        return true;
    }

    static ushort Read16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
}
