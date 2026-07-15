using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    // The PSX vblank counter is driven by the vblank IRQ at ~60Hz, independent of
    // what the game does. Games busy-wait on VSync(-1) expecting it to keep ticking,
    // so it MUST advance in real time — not only when the game calls VSync(0).
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    static int Vbl => (int)(_clock.Elapsed.TotalSeconds * 60.0);

    public static void VSync(CpuContext c, IMemory m)
    {
        int mode = (int)c.A0;
        if (mode < 0) { c.V0 = (uint)Vbl; return; }   // read current vblank count
        if (mode == 1) { c.V0 = 0; return; }

        Runtime.PresentFrame();
        c.V0 = (uint)Vbl;
    }
}
