using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode { Retail, Devkit }

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Cdrom.CdController? Cd;

    public static RunMode Mode { get; private set; } = RunMode.Retail;
    public static void SetMode(RunMode mode) => Mode = mode; //devkit vs retail, devkits reads from sim and has more ram
    public static string CdPath => Config.ConfigManager.Game.CdPath;
    
    public static Config.ViewConfig View => Config.ConfigManager.View;
    public static void SaveView() => Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    
    public static Hardware.MemoryCard CardA = new("carda.sav") { Enabled = true };
    public static Hardware.MemoryCard CardB = new("cardb.sav") { Enabled = true };
    public static readonly Memory.RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    public static void Initialize(string title)
    {
        Diagnostics.ConsoleMirror.Install();
        HostWindow.Initialize(title);
        Host.BotControl.Start();
        Audio.Initialize();
        Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
    }

    public static void WaitForValidDisc() => HostWindow.WaitForValidDisc();

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    static int _primFrame;
    // Optional game-registered callback fired once per presented frame
    // (before the host present), e.g. for pacing bookkeeping.
    public static Action? OnPresent;

    public static void PresentFrame()
    {
        if (++_primFrame % 60 == 0)
        {
            System.Console.WriteLine($"[prim] poly={DbgPrim.Poly} line={DbgPrim.Line} rect={DbgPrim.Rect} gte:rtps={Gte.DbgOp[0x01]} rtpt={Gte.DbgOp[0x30]} nclip={Gte.DbgOp[0x06]} avsz={Gte.DbgOp[0x2D] + Gte.DbgOp[0x2E]}"
                + (Pgxp.Enabled ? $" pgxp={Pgxp.Hits}/{Pgxp.Hits + Pgxp.Misses} miss[noaddr={Pgxp.MissNoAddr} noent={Pgxp.MissNoEntry} val={Pgxp.MissValue} tol={Pgxp.MissTol}]" : ""));
            DbgPrim.Poly = DbgPrim.Line = DbgPrim.Rect = 0;
            DbgHit.A = DbgHit.B = DbgHit.C = DbgHit.D = DbgHit.E = DbgHit.F = 0;
            Pgxp.ResetStats();
            System.Array.Clear(Gte.DbgOp);
        }
        Pgxp.FrameStamp++;
        OnPresent?.Invoke();
        HostWindow.Present(Gpu);
        Audio.Attach(Spu);
        FrameClock.Throttle();
        Cd?.AdvanceStreaming(); // keep hardware-level ReadN/ReadS streaming progressing
        Host.BotControl.Tick();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        // One real-time 60Hz vblank decision for this present. The vblank IRQ
        // (DispatchIrq(0) -> KF2's vblank ISR -> func_80079038: the VSync-counter
        // bump + sound-driver tick) and the psyq VSync event were both fired every
        // present, so at 120/144fps the music sequencer and vblank-paced menus ran
        // fast. Gate both to 60Hz (hardware rate); the world stays fast because
        // it's paced by wall-clock delta-time, not the vblank.
        bool vbl = VblankBeat();
        if (vbl) DispatchIrq(0);
        PumpCdIsr();
        PumpCdDataReadyFallback();
        if (Mem != null && Cpu != null && vbl)
            Bios.BiosB.DeliverEventIntr(Cpu, Mem, 0xF2000003u, 0x0002u);
        VsyncCounterProbe();
    }

    // Env-gated (KF2_VSCTR_LOG=1) probe of the game's VSync counter rate, to
    // confirm the sound tick (func_80079038, which increments it) runs at ~60/s.
    static double _vsCtrWindow; static uint _vsCtrLast; static bool _vsCtrInit;
    static void VsyncCounterProbe()
    {
        if (Mem == null || System.Environment.GetEnvironmentVariable("KF2_VSCTR_LOG") != "1") return;
        double now = _vblankClock.Elapsed.TotalSeconds;
        uint cur = Mem.ReadU32(0x8009C07Cu);
        if (!_vsCtrInit) { _vsCtrInit = true; _vsCtrLast = cur; _vsCtrWindow = now; return; }
        if (now - _vsCtrWindow >= 1.0)
        {
            System.Console.WriteLine($"[vsctr] {(cur - _vsCtrLast) / (now - _vsCtrWindow):F0}/s");
            _vsCtrLast = cur; _vsCtrWindow = now;
        }
    }

    // The game registers a CD interrupt handler that its real CdInit would hook; we
    // redirect CdInit, so instead we invoke that ISR directly whenever a CD IRQ is
    // pending. This is what advances the game's async CD stream-queue while it waits
    // (e.g. seek-complete INT2 between reads) without polling the CD registers itself.
    public static uint CdIsrAddr;
    static int _pumpLog, _pumpFrame;
    public static void PumpCdIsr()
    {
        if (CdIsrAddr == 0 || Cpu == null || Mem == null || Cd == null) return;
        if ((Cd.HasPendingIrq || (++_pumpFrame % 60 == 0)) && _pumpLog < 200)
        {
            uint wr = Mem.ReadU32(0x801C1724u), rd = Mem.ReadU32(0x801C1728u);
            byte st = rd != 0 ? Mem.ReadU8(rd) : (byte)0xEE;
            byte sub = rd != 0 ? Mem.ReadU8(rd + 0x10u) : (byte)0xEE;
            byte status = Mem.ReadU8(0x80082CD8u);
            uint cb0 = Mem.ReadU32(0x80082A20u), cb1 = Mem.ReadU32(0x80082A24u), cb2 = Mem.ReadU32(0x80082A28u);
            uint b0 = Mem.ReadU32(0x801C17A4u), b1 = Mem.ReadU32(0x801C17A8u), b2 = Mem.ReadU32(0x801C17ACu), b3 = Mem.ReadU32(0x801C17B0u);
            System.Console.WriteLine($"[pump] wr=0x{wr:X8} rd=0x{rd:X8} state=0x{st:X2} sub=0x{sub:X2} cdStatus=0x{status:X2} blob=0x{b0:X8},0x{b1:X8},0x{b2:X8},0x{b3:X8}");
            _pumpLog++;
        }
        if (!Cd.HasPendingIrq) return;
        var snap = Cpu.Snapshot();
        Dispatch.Dispatcher.Call(Cpu, Mem, CdIsrAddr);
        Cpu.Restore(snap);
    }

    // Lost-INT1 recovery: if a data sector sits unconsumed with no IRQ pending, the
    // game acked the INT1 from a poll loop before its (pumped) ISR could deliver the
    // HwCdRom data-ready event — so the event-driven consumer never runs and the
    // consumption-paced CD never advances (deadlock; e.g. the item-menu model load).
    // Deliver the data-ready event ourselves, once per frame, exactly as the real
    // ISR would have.
    static int _drFallbackLog;
    public static void PumpCdDataReadyFallback()
    {
        if (Cpu == null || Mem == null || Cd == null) return;
        if (!Cd.DataSittingUnconsumed) return;
        if (_drFallbackLog < 40) { System.Console.WriteLine("[cd] data-ready event fallback"); _drFallbackLog++; }
        Bios.BiosB.DeliverEventIntr(Cpu, Mem, 0xF0000003u, 0x40u);
    }

    // Pump host window events + input from recompiled busy-wait loops (keeps the
    // window responsive and lets bot/screenshot commands work while the game spins).
    public static void PumpHost() => HostWindow.PumpInput();

    // Per-frame interrupt/audio/CD servicing for busy-wait loops that never reach
    // the world loop's VSync (NPC dialogue box, holding the menu button, load
    // waits). PresentFrame normally fires the vblank event each frame, which is
    // what ticks the game's VSync-callback sound driver / music sequencer; while
    // the game spins in a pad-poll that path stops, so the sequencer freezes and
    // the SPU just repeats the last-keyed notes. Firing it here at the poll's
    // present cadence (~60Hz, from HostWindow.PumpInput) mirrors real hardware,
    // where the vblank IRQ keeps firing through the busy-wait. Does the servicing
    // subset of PresentFrame only — NOT the present (PumpInput renders), pad
    // refresh (would re-enter PadRead), or frame throttle (would sleep the poll).
    // Game-supplied VSync-callback dispatcher (KF2 func_80079038) — bumps the
    // VSync counter and calls each registered VSync callback, incl. the sound
    // driver's tick. The game's frame loop drives it at 60Hz; a busy-poll stalls
    // it, freezing the music sequencer. We pump it during stalls. A delegate (not
    // an address) because it's directly-called, not a registered indirect target.
    public static Action<Context.CpuContext, Memory.IMemory>? AuxAudioTick;

    // Address of the counter AuxAudioTick increments (KF2 0x8009C07C). Lets us
    // tell whether the GAME is still advancing VSync itself (a menu that keeps
    // rendering, brief dialogue transitions): if so we must NOT also pump, or the
    // sound driver ticks twice and the music plays fast. 0 = always pump.
    public static uint VSyncCounterAddr;

    // Overlay that must be active for AuxAudioTick to be safe to call. It's a
    // function in a specific overlay (KF2's GAME.EXE); calling it while a
    // different overlay is active (e.g. OPEN.EXE's boot FMVs) reads that
    // overlay's memory as if it were GAME's — dispatching a garbage VSync
    // callback and crashing. Null = no gate.
    public static string? AuxAudioTickOverlay;

    // The active monitor's refresh rate in Hz, published by the host once the
    // window exists (0 if unknown). Pacing uses it to cap the present rate at
    // the display's real refresh so a high fps target isn't wasted rendering
    // frames the monitor can't show.
    public static double MonitorRefreshHz;

    // Ceiling (Hz) for the host present throttle (FrameClock). Published by the
    // game's pacing so a >60 fps delta-time target isn't dragged back to 60 by a
    // fixed throttle, while menus/FMVs (which present outside the world pacer)
    // still can't free-run. Default 60; never let it fall below 60.
    public static double PresentCapHz = 60;

    // Bumped once per world tick by the game's pacer. FrameClock watches it to
    // tell whether the world pacer is driving frames (so it shouldn't also
    // throttle) or the game is presenting outside it (menus/FMVs → do throttle).
    public static uint WorldTickSeq;

    static bool _inBusyService;
    static uint _lastSeenVSyncCtr;

    // Real-time ~60Hz gate for the vblank IRQ + sound-driver tick. On hardware the
    // vblank fires at 60Hz no matter how fast the game renders; with delta-time we
    // may present at 120/144, and firing the vblank event every present made the
    // music sequencer and vblank-paced menus run fast. This caps vblank delivery
    // at 60Hz. (When the present rate is below 60 it fires per-present, so low
    // targets are unchanged.) The world loop is unaffected — it's paced by
    // wall-clock delta-time (Speed.CapWaitMore), not this counter.
    static readonly System.Diagnostics.Stopwatch _vblankClock = System.Diagnostics.Stopwatch.StartNew();
    static double _nextVblankSec, _vblankLogWindow;
    static int _vblankFires;
    public static bool VblankBeat()
    {
        double now = _vblankClock.Elapsed.TotalSeconds;
        if (now < _nextVblankSec) return false;
        _nextVblankSec += 1.0 / 60.0;
        if (now - _nextVblankSec > 0.25) _nextVblankSec = now + 1.0 / 60.0; // resync after a hitch
        if (System.Environment.GetEnvironmentVariable("KF2_VBLANK_LOG") == "1")
        {
            _vblankFires++;
            if (now - _vblankLogWindow >= 1.0)
            { System.Console.WriteLine($"[vblank] {_vblankFires / (now - _vblankLogWindow):F0}/s"); _vblankFires = 0; _vblankLogWindow = now; }
        }
        return true;
    }

    // Call once per present (~the game's normal frame cadence) from a busy-poll.
    public static void PumpBusyFrameServices()
    {
        if (_inBusyService || Cpu == null || Mem == null) return;

        // If the game advanced the VSync counter since our last pump, its own
        // frame loop is still servicing audio/frame (a menu that keeps rendering,
        // brief dialogue transitions) — don't double it, or the sound driver
        // ticks twice and the music plays fast. Only pump when truly stalled.
        if (VSyncCounterAddr != 0)
        {
            uint cur = Mem.ReadU32(VSyncCounterAddr);
            if (cur != _lastSeenVSyncCtr) { _lastSeenVSyncCtr = cur; return; }
        }

        _inBusyService = true;
        try
        {
            Audio.Attach(Spu);
            Cd?.AdvanceStreaming();
            Sdk.LibCd.Tick();
            PumpCdIsr();
            PumpCdDataReadyFallback();
            // psyq vblank event + VSync callbacks (music sequencer), gated to a
            // real-time 60Hz so busy-poll menus/dialogue keep hardware audio tempo
            // regardless of how fast we present. Only tick AuxAudioTick when its
            // overlay is active (else it reads another overlay's memory and
            // dispatches garbage). Snapshot/restore so it doesn't clobber the
            // poll's registers.
            if (VblankBeat())
            {
                Bios.BiosB.DeliverEventIntr(Cpu, Mem, 0xF2000003u, 0x0002u);
                if (AuxAudioTick != null
                    && (AuxAudioTickOverlay == null || Dispatch.Dispatcher.IsActive(AuxAudioTickOverlay)))
                {
                    var snap = Cpu.Snapshot();
                    AuxAudioTick(Cpu, Mem);
                    Cpu.Restore(snap);
                }
            }
            if (VSyncCounterAddr != 0) _lastSeenVSyncCtr = Mem.ReadU32(VSyncCounterAddr);
        }
        finally { _inBusyService = false; }
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    static bool _dumpedOnce;
    public static void DumpMem(uint addr, int len, string path)
    {
        if (_dumpedOnce || Mem == null) return;
        _dumpedOnce = true;
        var buf = new byte[len];
        for (int i = 0; i < len; i++) buf[i] = Mem.ReadU8(addr + (uint)i);
        try { System.IO.File.WriteAllBytes(path, buf); System.Console.WriteLine($"[dump] {path} <- 0x{addr:X8} ({len} bytes)"); } catch { }
    }

    // Auto-activated overlays: code loaded to RAM at runtime from a data file
    // (e.g. a game's per-map code overlays). The game recompiles them as normal
    // overlays; the runtime activates the matching one when its base address is
    // DMA'd, so the recompiled functions become dispatchable. A game registers
    // (loadBaseAddress -> overlayName) up front.
    static readonly System.Collections.Generic.Dictionary<uint, string> _autoOverlays = new();
    public static void RegisterAutoOverlay(uint baseAddr, string overlayName) => _autoOverlays[baseAddr] = overlayName;
    public static void OnOverlayDma(uint destAddr)
    {
        if (_autoOverlays.Count != 0 && _autoOverlays.TryGetValue(destAddr, out var name))
            Dispatch.Dispatcher.Load(name); // (re)activates; swaps out any overlay at the same base
    }

    public static void Shutdown()
    {
        Audio.Shutdown();
        HostWindow.Shutdown();
    }
}
