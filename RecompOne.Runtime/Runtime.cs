using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode
{
    Retail,
    Devkit
}

public sealed class HardResetSignal : Exception;

public static class Runtime
{
    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Cdrom.CdController? Cd;

    public static RunMode Mode { get; private set; } = RunMode.Retail;

    public static void SetMode(RunMode mode)
    {
        Mode = mode;
        //devkit vs retail, devkits reads from sim and has more ram
    }

    public static uint RamSize { get; internal set; } = MemoryMap.RetailRamSize;
    public static uint RamWordMask => (RamSize - 1) & ~3u;
    public static string CdPath => Config.ConfigManager.Game.CdPath;

    public static Func<string, string?>? DiscValidator;

    public static string? ValidateDisc(string path)
    {
        try
        {
            return DiscValidator?.Invoke(path);
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public static Config.ViewConfig View => Config.ConfigManager.View;

    public static void SaveView()
    {
        Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    }

    public static Hardware.MemoryCard CardA = new("carda.sav") { Enabled = true };
    public static Hardware.MemoryCard CardB = new("cardb.sav") { Enabled = true };

    private static void LoadMemoryCards()
    {
        var g = Config.ConfigManager.Game;
        CardA = new MemoryCard(Fallback(g.CardAPath, "carda.sav")) { Enabled = g.CardAEnabled };
        CardB = new MemoryCard(Fallback(g.CardBPath, "cardb.sav")) { Enabled = g.CardBEnabled };

        static string Fallback(string path, string def)
        {
            return string.IsNullOrWhiteSpace(path) ? def : path;
        }
    }

    public static readonly RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    private static bool _hostReady;

    public static void Initialize(string title)
    {
        if (!_hostReady)
        {
            _hostReady = true;
            Diagnostics.ConsoleMirror.Install();
            HostWindow.Initialize(title);
        Host.BotControl.Start();
            Audio.Initialize();
        }

        LoadMemoryCards();
        Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
        if (Event.HasAnyListeners<RuntimeReadyEvent>()) Event.Dispatch(new RuntimeReadyEvent());
    }

    public static void WaitForValidDisc()
    {
        HostWindow.WaitForValidDisc();
    }

    public static string Title
    {
        get => HostWindow.Title;
        set => HostWindow.Title = value;
    }

    public static void SetTitle(string title)
    {
        HostWindow.SetTitle(title);
    }

    public static void SetIcon(byte[] data)
    {
        HostWindow.SetIcon(data);
    }

    public static void SetIcon(byte[] rgba, int width, int height)
    {
        HostWindow.SetIcon(rgba, width, height);
    }

    public static void ClearIcon()
    {
        HostWindow.ClearIcon();
    }

    public static void ShowNotice(string message)
    {
        Host.Window.NoticePopup.Show(message);
    }

    public static void SetStartupNotice(string message, string title = "common.notice",
        string ackKey = "StartupNoticeAck")
    {
        Host.Window.StartupNoticePopup.Set(message, title, ackKey);
    }

    public static void AddLanguages(string json)
    {
        Host.Window.Localization.Merge(json);
    }

    public static bool AddLanguages(System.Reflection.Assembly assembly, string resourceName)
    {
        return Host.Window.Localization.MergeEmbedded(assembly, resourceName);
    }

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    // Optional game-registered callback fired once per presented frame (before
    // the host present), e.g. for pacing bookkeeping.
    public static Action? OnPresent;

    // Game-supplied VSync-callback dispatcher (KF2 func_80079038) — bumps the
    // VSync counter and calls each registered VSync callback, incl. the sound
    // driver's tick. The game's frame loop drives it at 60Hz; a busy-poll stalls
    // it, freezing the music sequencer. We pump it during stalls. A delegate (not
    // an address) because it's directly-called, not a registered indirect target.
    public static Action<CpuContext, IMemory>? AuxAudioTick;

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
    // throttle) or the game is presenting outside it (menus/FMVs -> do throttle).
    public static uint WorldTickSeq;

    // The game registers a CD interrupt handler that its real CdInit would hook; we
    // redirect CdInit, so instead we invoke that ISR directly whenever a CD IRQ is
    // pending. This is what advances the game's async CD stream-queue while it waits
    // (e.g. seek-complete INT2 between reads) without polling the CD registers itself.
    public static uint CdIsrAddr;

    public static void PumpCdIsr()
    {
        if (CdIsrAddr == 0 || Cpu == null || Mem == null || Cd == null) return;
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
    public static void PumpCdDataReadyFallback()
    {
        if (Cpu == null || Mem == null || Cd == null) return;
        if (!Cd.DataSittingUnconsumed) return;
        Bios.BiosB.DeliverEventIntr(Cpu, Mem, 0xF0000003u, 0x40u);
    }

    // Pump host window events + input from recompiled busy-wait loops (keeps the
    // window responsive and lets bot/screenshot commands work while the game spins).
    public static void PumpHost()
    {
        HostWindow.PumpInput();
    }

    // Real-time ~60Hz gate for the vblank IRQ + sound-driver tick. On hardware the
    // vblank fires at 60Hz no matter how fast the game renders; with delta-time we
    // may present at 120/144, and firing the vblank event every present made the
    // music sequencer and vblank-paced menus run fast. Returns a CATCH-UP count,
    // not a single beat: at 144fps it returns 0 most calls and 1 occasionally; at
    // 30fps ~2; at 15fps ~4 — averaging a true 60Hz tick at any framerate. Capped
    // so a hitch can't fire a burst.
    private static readonly System.Diagnostics.Stopwatch _vblankClock =
        System.Diagnostics.Stopwatch.StartNew();

    private static double _nextVblankSec;

    public static int VblankBeats()
    {
        var now = _vblankClock.Elapsed.TotalSeconds;
        var n = 0;
        while (now >= _nextVblankSec && n < 4)
        {
            _nextVblankSec += 1.0 / 60.0;
            n++;
        }

        if (now - _nextVblankSec > 0.25) _nextVblankSec = now + 1.0 / 60.0; // resync after a hitch
        return n;
    }

    private static bool _inBusyService;
    private static uint _lastSeenVSyncCtr;
    private static bool _inVblankAudio, _audioInit;
    private static uint _lastAudioCtr;
    private static double _audioOwed, _lastAudioTime, _tempoLogT;
    private static int _pumpedBeats, _gameTicks;
    private static long _pumpDelta;

    // Keep the game's VSync-callback dispatcher (and therefore the sound driver /
    // music sequencer) running at a true wall-clock 60Hz, whatever the render rate.
    //
    // The vblank IRQ path already drives it, but not reliably 60 times a second:
    // measured 60/s at a 120fps target yet only ~30/s at 15fps, because raises are
    // coalesced and absorbed while the game sits in its frame wait. So rather than
    // guessing, track the DEFICIT: accumulate the beats wall-clock says are owed,
    // subtract the ticks the game actually produced (its own counter delta), and
    // pump only the shortfall. That converges to exactly 60/s at any framerate and
    // contributes nothing when the IRQ path is already keeping up.
    public static void PumpVblankAudio()
    {
        if (_inVblankAudio || Cpu == null || Mem == null || AuxAudioTick == null) return;
        if (VSyncCounterAddr == 0) return;
        if (AuxAudioTickOverlay != null && !Dispatch.Dispatcher.IsActive(AuxAudioTickOverlay)) return;

        var now = _vblankClock.Elapsed.TotalSeconds;
        var cur = Mem.ReadU32(VSyncCounterAddr);

        if (!_audioInit)
        {
            _audioInit = true;
            _lastAudioTime = now;
            _lastAudioCtr = cur;
            return;
        }

        _audioOwed += (now - _lastAudioTime) * 60.0;   // beats wall-clock says are due
        var gt = unchecked(cur - _lastAudioCtr);
        _gameTicks += (int)gt;
        _audioOwed -= gt;  // beats the game served itself
        _lastAudioTime = now;
        _lastAudioCtr = cur;

        // A game running faster than 60Hz must not bank credit that later suppresses
        // legitimate beats; a long stall must not let a burst build up either.
        if (_audioOwed < -4.0) _audioOwed = -4.0;
        if (_audioOwed > 4.0) _audioOwed = 4.0;
        if (_audioOwed < 1.0) return;

        var beats = (int)_audioOwed;
        _inVblankAudio = true;
        try
        {
            for (var i = 0; i < beats; i++)
            {
                // Only the dispatcher. NOT DeliverEventIntr(0xF2000003) as well: the
                // IRQ path already delivers the psyq vblank event, and the game's
                // handler for it calls the dispatcher too — so doing both here
                // advanced the counter more than once per beat and overshot 60Hz.
                var snap = Cpu.Snapshot();
                AuxAudioTick(Cpu, Mem);
                Cpu.Restore(snap);
            }
        }
        finally
        {
            _inVblankAudio = false;
        }

        _audioOwed -= beats;
        var after = Mem.ReadU32(VSyncCounterAddr);
        _pumpedBeats += beats;
        _pumpDelta += unchecked(after - _lastAudioCtr);
        if (Environment.GetEnvironmentVariable("KF2_TEMPO_LOG") == "1")
        {
            var t = _vblankClock.Elapsed.TotalSeconds;
            if (t - _tempoLogT >= 1.0)
            {
                Console.WriteLine(
                    $"[tempo] pumped={_pumpedBeats} counterDeltaDuringPump={_pumpDelta} " +
                    $"gameTicks={_gameTicks} owed={_audioOwed:F2}");
                _pumpedBeats = 0; _pumpDelta = 0; _gameTicks = 0; _tempoLogT = t;
            }
        }

        // Attribute ONLY our own ticks. Any increments the IRQ path made while we
        // were pumping stay unattributed here so the next pass subtracts them as
        // game ticks — re-reading the counter would swallow them and over-pump.
        _lastAudioCtr = unchecked(_lastAudioCtr + (uint)beats);
    }

    public static void PumpBusyFrameServices()
    {
        if (_inBusyService || Cpu == null || Mem == null) return;

        // If the game advanced the VSync counter since our last pump, its own
        // frame loop is still servicing audio/frame (a menu that keeps rendering,
        // brief dialogue transitions) — don't double it, or the sound driver
        // ticks twice and the music plays fast. Only pump when truly stalled.
        if (VSyncCounterAddr != 0)
        {
            var cur = Mem.ReadU32(VSyncCounterAddr);
            if (cur != _lastSeenVSyncCtr)
            {
                _lastSeenVSyncCtr = cur;
                return;
            }
        }

        _inBusyService = true;
        try
        {
            Audio.Attach(Spu);
            Cd?.AdvanceStreaming();
            Sdk.LibCd.Tick();
            PumpCdIsr();
            PumpCdDataReadyFallback();
            var beats = VblankBeats();
            for (var i = 0; i < beats; i++)
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
        finally
        {
            _inBusyService = false;
        }
    }

    private static volatile bool _hardResetPending;

    public static bool HardResetPending => _hardResetPending;

    public static void HardReset()
    {
        _hardResetPending = true;
    }

    public static void Run(Action boot)
    {
        while (true)
            try
            {
                boot();
                return;
            }
            catch (HardResetSignal)
            {
                Console.WriteLine("[Runtime] hard reset, game restarting");
                ResetForBoot();
            }
    }

    private static void ResetForBoot()
    {
        Audio.Detach();

        Sdk.LibCd.Reset();
        Sdk.LibCdStream.Reset();
        Assets.Xa.XaRouter.Reset();
        Sdk.LibPad.Reset();
        Dispatch.Dispatcher.Reset();
        Bios.BiosB.Reset();
        OverlayLog.Clear();

        Cpu = null;
        Mem = null;
        Gpu = null;
        Spu = null;
        Cd = null;

        if (Hle.GpuHle.Backend is { Ready: true } backend)
        {
            backend.FillRect(0, 0, Gpu.VramWidth, Gpu.VramHeight, 0);
            backend.Flush();
        }
    }

    public static void PresentFrame()
    {
        if (_hardResetPending)
        {
            _hardResetPending = false;
            throw new HardResetSignal();
        }

        OnPresent?.Invoke();
        HostWindow.Present(Gpu);
        Audio.Attach(Spu);
        FrameClock.Throttle();
        Cd?.AdvanceStreaming(); // keep hardware-level ReadN/ReadS streaming progressing
        Host.BotControl.Tick();
        Sdk.LibCd.Tick();
        if (Cpu != null && Mem != null) Sdk.LibMcrd.Tick(Cpu, Mem);
        if (Mem != null)
        {
            Bios.BiosB.RefreshPad(Mem);
            Sdk.LibPad.Refresh(Mem);
        } //is this correct?

        // NO vblank raise here. The vblank IRQ drives the game's VSync callbacks —
        // including the sound driver's tick — so raising it once per PRESENT makes
        // the music tempo scale with the render rate (slow at 15fps, fast at 120).
        // Interrupts.TickVBlank(), called from the Poll sites the recompiler emits
        // at every loop back-edge, already raises it off the wall clock at a true
        // 60Hz regardless of framerate. That is the only correct source.
        PumpCdIsr();
        PumpCdDataReadyFallback();
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