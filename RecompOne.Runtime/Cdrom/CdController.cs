using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Cdrom;

public sealed class CdController
{
    private readonly CueFs _fs;
    private readonly IMemory _m;

    private byte _index;
    private readonly Queue<byte> _paramFifo = new();
    private readonly Queue<byte> _responseFifo = new();
    private readonly Queue<(byte irqType, byte[] response)> _pendingIrqs = new();
    private byte _irqFlags;
    private byte _lastMode;
    private int _seekLba;
    private byte[] _dataBuf = new byte[2048];

    private int _dataFifoPos;
    private bool _dataReady;
    private bool _reading;
    private bool _streamPending;
    private bool _sectorConsumed;
    private byte _lastIrq;

    private readonly object _dbgGate = new();
    private readonly Queue<string> _dbgEvents = new();
    private const int DbgMaxEvents = 256;
    private long _sectorsRead;
    private int _lastReadLba;
    private long _dbgSetloc;

    public struct CdDebug
    {
        public int SeekLba, LastReadLba;
        public bool Reading, StreamPending, DataReady;
        public byte IrqFlags, LastIrq, Index;
        public int PendingIrqCount, ParamCount, ResponseCount, DataFifoPos, DataBufLength;
        public long SectorsRead;
    }

    private sealed class ReadRun
    {
        public int Start, Count;
        public string Time = "";
    }

    private readonly Dictionary<string, ReadRun> _runs = new();

    private void DbgEvent(string msg)
    {
        lock (_dbgGate)
        {
            FlushRunsLocked();
            EnqueueLocked($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
        }
    }

    private void DbgReadRun(string source, int lba)
    {
        lock (_dbgGate)
        {
            if (_runs.TryGetValue(source, out var run))
            {
                if (lba == run.Start + run.Count) { run.Count++; return; }
                EnqueueLocked(RunLine(source, run));
            }
            _runs[source] = new ReadRun { Start = lba, Count = 1, Time = DateTime.Now.ToString("HH:mm:ss.fff") };
        }
    }

    private void FlushRunsLocked()
    {
        foreach (var (source, run) in _runs)
            EnqueueLocked(RunLine(source, run));
        _runs.Clear();
    }

    private void EnqueueLocked(string line)
    {
        _dbgEvents.Enqueue(line);
        while (_dbgEvents.Count > DbgMaxEvents) _dbgEvents.Dequeue();
    }

    private static string RunLine(string source, ReadRun run) =>
        run.Count == 1
            ? $"{run.Time}  {source} lba={run.Start}"
            : $"{run.Time}  {source} lba={run.Start}..{run.Start + run.Count - 1} ({run.Count} sectors)";

    public void ClearDebugEvents()
    {
        lock (_dbgGate)
        {
            _dbgEvents.Clear();
            _runs.Clear();
        }
    }

    public void CaptureDebug(out CdDebug d, List<string> events)
    {
        d = new CdDebug {
            SeekLba = _seekLba,
            LastReadLba = _lastReadLba,
            Reading = _reading,
            StreamPending = _streamPending,
            DataReady = _dataReady,
            IrqFlags = _irqFlags,
            LastIrq = _lastIrq,
            Index = _index,
            PendingIrqCount = _pendingIrqs.Count,
            ParamCount = _paramFifo.Count,
            ResponseCount = _responseFifo.Count,
            DataFifoPos = _dataFifoPos,
            DataBufLength = _dataBuf.Length,
            SectorsRead = _sectorsRead
        };
        lock (_dbgGate)
        {
            events.Clear();
            events.AddRange(_dbgEvents);
            foreach (var (source, run) in _runs)
                events.Add(RunLine(source, run));
        }
    }

    private static string CmdName(byte cmd) => cmd switch {
        0x01 => "GetStat",
        0x02 => "Setloc",
        0x06 => "ReadN",
        0x08 => "Stop",
        0x09 => "Pause",
        0x0A => "Init",
        0x0B => "Mute",
        0x0C => "Demute",
        0x0E => "Setmode",
        0x15 => "SeekL",
        0x16 => "SeekP",
        0x1B => "ReadS",
        _ => $"0x{cmd:X2}"
    };

    public CdController(CueFs fs, IMemory m)
    {
        _fs = fs;
        _m = m;
        BiosA.SetFs(fs);
        BiosA.SetCd(this);
        Runtime.Cd = this;
    }

    public void LoadToMemory(string path, uint address, int offset = 0, int length = -1)
    {
        var data = _fs.ReadFile(path);
        int count = length < 0 ? data.Length - offset : length;
        for (int i = 0; i < count; i++)
            _m.WriteU8(address + (uint)i, data[offset + i]);
        RecompOne.Runtime.Log.Cd($"{path} -> 0x{address:X8} | {count} bytes");
        DbgEvent($"file {path} -> 0x{address:X8} ({count} bytes)");
        Dispatcher.TryLoad(CdUtils.OverlayName(CdUtils.ExtractFileName(path)));
    }

    public byte Read(uint phys)
    {
        return (phys & 3) switch
        {
            0 => (byte)((_index & 3) | (_paramFifo.Count == 0 ? 0x08 : 0) | 0x10 | (_responseFifo.Count > 0 ? 0x20 : 0) | (_dataReady ? 0x40 : 0)),
            1 => _responseFifo.Count > 0 ? _responseFifo.Dequeue() : (byte)0,
            2 => ReadDataByte(),
            _ => _index == 1 ? _irqFlags : (byte)0,
        };
    }

    public void Write(uint phys, byte val)
    {
        switch (phys & 3)
        {
            case 0:
                _index = (byte)(val & 3);
                break;
            case 1:
                if (_index == 0) ExecuteCommand(val);
                break;
            case 2:
                if (_index == 0) _paramFifo.Enqueue(val);
                else if (_index == 1) _paramFifo.Clear();
                break;
            case 3:
                if (_index == 0)
                {
                    if ((val & 0x80) != 0) { _dataFifoPos = 0; _dataReady = true; }
                    else _dataReady = false;
                }
                else if (_index == 1)
                {
                    _irqFlags &= (byte)~val;
                    if (_irqFlags == 0) AfterAck();
                }
                break;
        }
    }

    private void ExecuteCommand(byte cmd)
    {
        RecompOne.Runtime.Log.Cd($"cmd 0x{cmd:X2}");
        var prms = new List<byte>();
        while (_paramFifo.Count > 0) prms.Add(_paramFifo.Dequeue());
        DbgEvent(prms.Count > 0
            ? $"{CmdName(cmd)} ({string.Join(" ", prms.Select(p => p.ToString("X2")))}) lba={_seekLba}"
            : $"{CmdName(cmd)} lba={_seekLba}");

        switch (cmd)
        {
            case 0x01:
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x02: //Setloc
                if (prms.Count >= 3)
                    _seekLba = BcdToLba(prms[0], prms[1], prms[2]);
                if (++_dbgSetloc % 10 == 0) Console.WriteLine($"[setloc] #{_dbgSetloc} lba={_seekLba}");
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x06: // ReadN
                _reading = true;
                _sectorConsumed = false; // fresh read: first sector isn't consumed until the game DMAs it (else it's skipped)
                _streamPending = false;
                _nextSectorMs = _readClock.Elapsed.TotalMilliseconds + SectorPeriodMs;
                ReadNextSector();
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(1, [DriveStatus()]);
                break;
            case 0x08: //Stop
                _reading = false;
                _streamPending = false;
                Sdk.LibCdStream.OnStopStream();
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x09: // Pause
                _reading = false;
                _streamPending = false;
                Sdk.LibCdStream.OnStopStream();
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x0A:
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x0B: // mute
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x0C: // demute
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x0D: // set filter (XA file/channel for real-time audio routing)
                if (prms.Count > 1) { _filterFile = prms[0]; _filterChannel = prms[1]; Console.WriteLine($"[cd] Setfilter file={prms[0]} ch={prms[1]}"); }
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x0E: // set mode
                if (prms.Count > 0) { _lastMode = prms[0]; Console.WriteLine($"[cd] Setmode 0x{prms[0]:X2}"); }
                QueueIrq(3, [DriveStatus()]);
                break;
            case 0x15: // seek L
            case 0x16: //seek P
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(2, [DriveStatus()]);
                break;
            case 0x1B: // read s
                if (Sdk.LibCdStream.InUse)
                {
                    // The ring library is HLE'd (StSetRing was called): the HLE stream
                    // thread reads the disc + fills the ring/XA directly. No data INT1s.
                    _reading = false;
                    Sdk.LibCdStream.OnReadStream(_seekLba, (_lastMode & 0x80) != 0 ? 150.0 : 75.0);
                    QueueIrq(3, [DriveStatus()]);
                    break;
                }
                _reading = true;
                _sectorConsumed = false;
                _streamPending = false;
                ReadNextSector();
                QueueIrq(3, [DriveStatus()]);
                QueueIrq(1, [DriveStatus()]);
                break;
            default:
                Console.WriteLine($"[CD] command 0x{cmd:X2} is unknow");
                QueueIrq(5, [DriveStatus(), 0x40]);
                break;
        }
    }

    private void QueueIrq(byte irqType, byte[] response)
    {
        if (_irqFlags == 0 && _pendingIrqs.Count == 0)
            DeliverImmediate(irqType, response);
        else
            _pendingIrqs.Enqueue((irqType, response));
    }

    private void AfterAck()
    {
        if (_pendingIrqs.Count > 0) { DeliverNext(); return; }
        // Continuous ReadN/ReadS: mark that the next sector may be delivered. The
        // actual delivery is time-paced in AdvanceStreaming (driven by register
        // polls + per-frame) so we don't flood the game with instant IRQs.
        // Deliver the next sector only after the game actually consumed (DMA'd) the
        // current one — paces by real data consumption: no overwrite, no ack-runaway.
        if (_reading && _lastIrq == 1)
        {
            _streamPending = true;
            AdvanceStreaming(); // delivers the next sector iff the game already consumed the current
            if (_irqFlags == 0 && _pendingIrqs.Count == 0) ClearInInterrupt();
            return;
        }
        ClearInInterrupt();
    }

    static readonly System.Diagnostics.Stopwatch _readClock = System.Diagnostics.Stopwatch.StartNew();
    double _nextSectorMs;
    const double SectorPeriodMs = 1000.0 / 150.0; // ~2x CD data rate

    public void AdvanceStreaming()
    {
        if (!_reading || !_streamPending || !_sectorConsumed) return; // never skip a sector the game hasn't consumed
        if (_irqFlags != 0 || _pendingIrqs.Count > 0) return;
        _streamPending = false;
        _sectorConsumed = false;
        ReadNextSector();
        DeliverImmediate(1, [DriveStatus()]);
    }

    static long _dbgIrq;
    private void DeliverImmediate(byte irqType, byte[] response)
    {
        if (irqType != 1 || _dbgIrq++ < 60) Console.WriteLine($"[irq] INT{irqType} resp=[{string.Join(" ", System.Array.ConvertAll(response, b => b.ToString("X2")))}]");
        _responseFifo.Clear();
        foreach (var b in response) _responseFifo.Enqueue(b);
        _irqFlags = irqType;
        _lastIrq = irqType;
        SetInInterrupt(1);
    }

    private void DeliverNext()
    {
        var (irqType, response) = _pendingIrqs.Dequeue();
        if (irqType != 1 || _dbgIrq++ < 60) Console.WriteLine($"[irq] (next) INT{irqType} resp=[{string.Join(" ", System.Array.ConvertAll(response, b => b.ToString("X2")))}]");
        _responseFifo.Clear();
        foreach (var b in response) _responseFifo.Enqueue(b);
        _irqFlags = irqType;
        _lastIrq = irqType;
        SetInInterrupt(1);
    }

    private byte ReadDataByte()
    {
        if (!_dataReady || _dataFifoPos >= _dataBuf.Length) { _dataReady = false; return 0; }
        byte b = _dataBuf[_dataFifoPos++];
        if (_dataFifoPos >= _dataBuf.Length) { _dataReady = false; _sectorConsumed = true; AdvanceStreaming(); }
        return b;
    }

    static long _dbgDma;
    public void DmaReadData(IMemory m, uint addr, uint byteCount)
    {
        Runtime.OnOverlayDma(addr); // activate a runtime-loaded code overlay if this DMA targets its base
        for (uint i = 0; i < byteCount; i++)
            m.WriteU8(addr + i, _dataFifoPos < _dataBuf.Length ? _dataBuf[_dataFifoPos++] : (byte)0);
        // Only consider the sector consumed (and advance the read) once its whole
        // FIFO is drained. Streaming reads pull a sector in small chunks (e.g. STR
        // in 32-byte DMAs); advancing after a partial read would skip most of it.
        if (_dataFifoPos >= _dataBuf.Length)
        {
            _dataReady = false;
            _sectorConsumed = true; // game read this sector
            AdvanceStreaming();     // deliver next now if the game already acked (ack-before-DMA order)
        }
    }

    public void LoadSectorToFifo(byte[] data)
    {
        _dataBuf = (byte[])data.Clone();
        _dataFifoPos = 0;
        _dataReady = true;
    }

    private void SetInInterrupt(ushort val)
    {
        if (BiosB.IntrEnvInInterruptAddr != 0)
            _m.WriteU16(BiosB.IntrEnvInInterruptAddr, val);
    }

    private void ClearInInterrupt()
    {
        if (BiosB.IntrEnvInInterruptAddr != 0)
            _m.WriteU16(BiosB.IntrEnvInInterruptAddr, 0);
    }

    static long _dbgReadN;
    byte _filterFile, _filterChannel;

    private void ReadNextSector()
    {
        try
        {
            // XA-ADPCM mode (Setmode bit 0x40): real-time audio sectors go to the SPU,
            // not the data FIFO — only video/data sectors produce a data INT1. This is
            // what STR (FMV) playback and XA music rely on. Cap the skip so a pure-audio
            // file can't decode itself to completion inside one call.
            if ((_lastMode & 0x40) != 0)
            {
                for (int guard = 0; guard < 64; guard++)
                {
                    var raw = _fs.ReadSectorData(_seekLba, 2336);
                    if ((raw[2] & 0x04) == 0) break; // data/video sector -> deliver below
                    bool pass = (_lastMode & 0x08) == 0 || (raw[0] == _filterFile && raw[1] == _filterChannel);
                    if (pass) XaAudio.DecodeSector(raw, 8, raw[3]);
                    _sectorsRead++;
                    _seekLba++;
                }
            }
            _dataBuf = _fs.ReadSector(_seekLba);
            _dataFifoPos = 0; // new sector replaces the data FIFO: read from the start
            _dataReady = true; // a fresh sector is available (poll-based CdReady reads depend on this)
            DbgReadRun("read", _seekLba);
            _lastReadLba = _seekLba;
            _sectorsRead++;
            _seekLba++;
        }
        catch
        {
            Array.Clear(_dataBuf);
        }
    }

    public CueFs Fs => _fs;
    public byte DriveStatusByte() => DriveStatus();
    // A CD IRQ is asserted and not yet acknowledged by the game (used to pump the
    // game's CD ISR when it's waiting without polling — e.g. seek-complete between reads).
    public bool HasPendingIrq => _irqFlags != 0;

    public byte[] ReadSectorData(int lba)
    {
        _seekLba = lba;
        ReadNextSector();
        return (byte[])_dataBuf.Clone();
    }

    public byte[] ReadSectorData(int lba, int size)
    {
        DbgReadRun(size == 2336 ? "readXA" : "read", lba);
        _lastReadLba = lba;
        _sectorsRead++;
        return _fs.ReadSectorData(lba, size);
    }

    public void QueueAsyncSeekL(byte mm, byte ss, byte ff)
    {
        _seekLba = BcdToLba(mm, ss, ff);
        DbgEvent($"async SeekL lba={_seekLba}");
        QueueIrq(3, [DriveStatus()]);
        QueueIrq(2, [DriveStatus()]);
    }

    public void QueueAsyncGetStatus()
    {
        QueueIrq(3, [DriveStatus()]);
    }

    public void QueueAsyncSetMode(byte mode)
    {
        DbgEvent($"async Setmode {mode:X2}");
        QueueIrq(3, [DriveStatus()]);
    }

    public void QueueAsyncReadSector(uint count, uint dstAddr, uint mode)
    {
        DbgEvent($"async ReadSector lba={_seekLba} count={count} dst=0x{dstAddr:X8}");
        for (uint i = 0; i < count; i++)
        {
            ReadNextSector();
            int sectorSize = (mode & 0x30) == 0 ? 2048 : 2048; //fix
            for (int j = 0; j < Math.Min(_dataBuf.Length, sectorSize); j++)
                _m.WriteU8(dstAddr + i * (uint)sectorSize + (uint)j, _dataBuf[j]);
            _seekLba++;
        }
        QueueIrq(3, [DriveStatus()]);
        QueueIrq(1, [DriveStatus()]);
        QueueIrq(2, [DriveStatus()]);
    }

    // bit1 (0x02) = motor on; bit5 (0x20) = reading. Games' CD ISRs inspect this.
    private byte DriveStatus() => (byte)(_reading ? 0x22 : 0x02);

    private static int BcdToLba(byte mm, byte ss, byte ff)
    {
        int m = (mm >> 4) * 10 + (mm & 0xF);
        int s = (ss >> 4) * 10 + (ss & 0xF);
        int f = (ff >> 4) * 10 + (ff & 0xF);
        return (m * 60 + s) * 75 + f - 150;
    }
}
