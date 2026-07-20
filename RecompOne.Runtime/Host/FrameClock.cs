using System.Diagnostics;

namespace RecompOne.Runtime.Host;

internal static class FrameClock
{
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _nextFrameMs;
    static uint _lastTickSeq;

    //maybe not the best but it seens to work for now
    public static void Throttle()
    {
        // When the game's world pacer (Speed.CapWaitMore) is driving frames it
        // already paces both ticks AND presents; throttling again here just
        // double-sleeps and caps the rate below target. Detect that by the world
        // tick sequence advancing since the last present and bow out. Only
        // throttle when it hasn't (menus/FMVs, which present outside the world
        // pacer and would otherwise free-run).
        uint seq = Runtime.WorldTickSeq;
        if (seq != _lastTickSeq) { _lastTickSeq = seq; return; }

        // Menu/FMV present cap: follow the game's cap (floored at 60) so it stays
        // smooth but can't run away.
        double hz = Runtime.PresentCapHz;
        double frameMs = 1000.0 / (hz >= 60.0 ? hz : 60.0);
        _nextFrameMs += frameMs;
        double now = _clock.Elapsed.TotalMilliseconds;
        double wait = _nextFrameMs - now;
        // Never schedule more than one frame ahead: callers that present more
        // than the cap (e.g. delta-time pacing + the game's own per-frame VSync)
        // would otherwise push the grid forward and stall in bursts.
        if (wait > frameMs) { _nextFrameMs = now + frameMs; wait = frameMs; }
        if (wait > 1) Thread.Sleep((int)wait);
        else if (wait < -100) _nextFrameMs = now;
    }
}
