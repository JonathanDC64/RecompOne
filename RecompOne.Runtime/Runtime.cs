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

    public static void PresentFrame()
    {
        HostWindow.Present(Gpu);
        Audio.Attach(Spu);
        FrameClock.Throttle();
        Cd?.AdvanceStreaming(); // keep hardware-level ReadN/ReadS streaming progressing
        Host.BotControl.Tick();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); } //is this correct?
        DispatchIrq(0); //using this to dispatch irqs too if necessary, probably not needed after the rest of stuff is reimplemented
        PumpCdIsr();
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

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    public static void Shutdown()
    {
        Audio.Shutdown();
        HostWindow.Shutdown();
    }
}
