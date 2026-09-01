using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    // Optional game-registered hook: return true to make VSync(0) a cheap
    // counter read (no present, no vblank wait). Required for a logic rate
    // above 60fps — otherwise WaitVBlanks paces the game at the hardware rate.
    public static Func<bool>? VsyncSkip;

    private static int _vcount;
    private static readonly VSyncEvent _vsyncEvent = new();

    private const double HblankHz = 15734.0; //correct?

    private static int _lastVSyncCount;
    private static double _lastVSyncMs;

    public static void VSync(CpuContext c, IMemory m)
    {
        var mode = (int)c.A0;
        Log.Sdk($"VSync({mode})");
        if (mode < 0)
        {
            c.V0 = (uint)Interrupts.VBlankCount;
            return;
        }

        if (mode == 1)
        {
            c.V0 = Elapsed();
            return;
        }

        // Game-registered pacing hook (e.g. KF2 delta-time above 60fps): when it
        // returns true, skip the present machinery — VSync(0) must not throttle
        // or wait for a vblank when a present isn't due, or ticks cap at 60.
        if (VsyncSkip != null && VsyncSkip())
        {
            c.V0 = (uint)Interrupts.VBlankCount;
            return;
        }

        Runtime.PresentFrame();
        // WaitVBlanks pulls double duty: it paces to the 60Hz vblank grid AND its
        // wait loop calls Interrupts.PollNow, which is what services vblank / audio
        // / CD while the game is otherwise idle. Both matter, in opposite
        // directions, so pick per target (measured):
        //   target <= 60 : wait. The game sleeps inside its own pacer, so nothing
        //                  else polls — dropping the wait starved the sound driver
        //                  to 1.6 ticks/s at a 15fps target. Pacing to 60 costs
        //                  nothing when the target is at or below 60 anyway.
        //   target >  60 : do NOT wait, it would hard-cap the world at 60. The game
        //                  runs hot at these rates, so the emitted Poll sites
        //                  service everything on their own (measured 59.8 ticks/s
        //                  at a 120fps target with no wait).
        if (VsyncSkip == null || Runtime.PresentCapHz <= 60.0)
            WaitVBlanks(c, m, mode == 0 ? 1 : mode);
        var elapsed = Elapsed();
        _lastVSyncCount = Interrupts.VBlankCount;
        _lastVSyncMs = Interrupts.ClockMs;
        _vcount++;

        if (Event.HasAnyListeners<VSyncEvent>())
        {
            var e = _vsyncEvent;
            e.Context = c;
            e.Memory = m;
            e.Frame = _vcount;
            Event.Dispatch(e);
        }

        c.V0 = elapsed;
    }

    private static uint Elapsed()
    {
        return (uint)((Interrupts.ClockMs - _lastVSyncMs) * HblankHz / 1000.0) & 0xFFFF;
    }

    private const double SleepMarginMs = 2.0;

    private static void WaitVBlanks(CpuContext c, IMemory m, int count)
    {
        var target = _lastVSyncCount + count;
        var floor = Interrupts.VBlankCount + 1;
        if (target < floor) target = floor;

        var began = Interrupts.ClockMs;

        while (Interrupts.VBlankCount < target)
        {
            var remaining = Interrupts.MsToNextVBlank;
            if (remaining > SleepMarginMs)
            {
                var ms = (int)(remaining - SleepMarginMs);
                if (ms > 0) Thread.Sleep(ms);
            }
            else
            {
                Thread.SpinWait(64);
            }

            Interrupts.PollNow(c, m);
        }

        var waited = Interrupts.ClockMs - began;

        var extra = Interrupts.VBlankCount - target;
    }
}