using System.Diagnostics;

namespace RecompOne.Runtime.Host;

internal static class FrameClock
{
    private const double FrameMs = 1000.0 / 60.0;
    private const double SpinMs = 1.5;

    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _nextFrameMs;

    public static bool VSync { get; set; }

    public static double LastFrameMs { get; private set; }

    public static double Fps { get; private set; }
    private static double _fpsAccumMs;
    private static int _fpsFrames;
    public static double LastWaitMs { get; private set; }

    private static double _lastStart;
    private static uint _lastTickSeq;


    public static void Throttle()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        LastFrameMs = now - _lastStart;
        _lastStart = now;

        _fpsAccumMs += LastFrameMs;
        _fpsFrames++;
        if (_fpsAccumMs >= 1000.0)
        {
            Fps = _fpsFrames * 1000.0 / _fpsAccumMs;
            _fpsAccumMs = 0;
            _fpsFrames = 0;
        }

        // When the game's world pacer (Speed.CapWaitMore) is driving frames it
        // already paces both ticks AND presents; throttling again here just
        // double-sleeps and pins the rate to FrameMs (60Hz) however high the
        // target. Detect it by the world tick sequence advancing since the last
        // present and bow out, re-basing the grid so the next throttled present
        // doesn't burst. Menus/FMVs present outside the world pacer, don't bump
        // the sequence, and so keep the fixed 60Hz pacing below.
        var seq = Runtime.WorldTickSeq;
        if (seq != _lastTickSeq)
        {
            _lastTickSeq = seq;
            _nextFrameMs = now;
            LastWaitMs = 0;
            return;
        }

        _nextFrameMs += FrameMs;
        var wait = _nextFrameMs - now;

        // Never schedule more than one frame ahead: callers presenting faster than
        // the cap would otherwise push the grid forward and stall in bursts.
        if (wait > FrameMs)
        {
            _nextFrameMs = now + FrameMs;
            wait = FrameMs;
        }

        if (wait < -100)
        {
            _nextFrameMs = now;
            LastWaitMs = 0;
            return;
        }

        if (wait <= 0)
        {
            LastWaitMs = 0;
            return;
        }

        if (VSync && wait < FrameMs * 0.75)
        {
            LastWaitMs = 0;
            return;
        }

        var sleepUntil = _nextFrameMs - SpinMs;
        if (now < sleepUntil)
        {
            var ms = (int)(sleepUntil - now);
            if (ms > 0) Thread.Sleep(ms);
        }

        while (_clock.Elapsed.TotalMilliseconds < _nextFrameMs)
            Thread.SpinWait(48);

        LastWaitMs = wait;
    }

    public static void Resync()
    {
        _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
    }
}