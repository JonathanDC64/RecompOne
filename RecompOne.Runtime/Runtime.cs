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
        HostWindow.Present(Gpu);
        Audio.Attach(Spu);
        FrameClock.Throttle();
        Cd?.AdvanceStreaming(); // keep hardware-level ReadN/ReadS streaming progressing
        Host.BotControl.Tick();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        DispatchIrq(0); //using this to dispatch irqs too if necessary, probably not needed after the rest of stuff is reimplemented
        PumpCdIsr();
        PumpCdDataReadyFallback();
        // Fire the psyq vblank event (RootCounter 3, EvSpINT) each frame so games
        // that registered an EvMdINTR vblank handler get ticked — e.g. KF2's frame
        // pacing counter, which world-build waits on.
        if (Mem != null && Cpu != null)
            Bios.BiosB.DeliverEventIntr(Cpu, Mem, 0xF2000003u, 0x0002u);
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
