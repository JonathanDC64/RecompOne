namespace RecompOne.Runtime;

// Delta-time / framerate targeting. KF2 paces the world at 15fps (it waits 4
// vblanks per game frame) and its per-frame quantities (movement steps, timers,
// gauges) assume that rate. Raising the framerate means letting the game tick
// more often and scaling those per-tick quantities down so real-time speed is
// unchanged — the exact scheme proven by the standalone KF2 60fps ROM patcher,
// generalized here with a runtime-configurable factor.
//
// Phase 1 supports power-of-two multiples of the native rate (15/30/60/120):
// the game's scaling sites are integer shift operations, so the hooks add
// DeltaShift to the shift amount. TargetFps 0 = unlimited (later phase).
public static class Speed
{
    static int _targetFps = 15;

    public static int TargetFps
    {
        get => _targetFps;
        set
        {
            _targetFps = value;
            // Snap to the nearest supported rate. Capped at 60 (shift 2) for
            // now: the world tick is vblank-paced (60Hz), so a larger shift
            // would divide quantities by 8 while ticking only 4x -> slow-mo.
            // 120/unlimited needs host vblank-rate work first.
            DeltaShift = value switch
            {
                < 23 => 0,  // -> 15
                < 45 => 1,  // -> 30
                _ => 2,     // -> 60
            };
        }
    }

    // log2(EffectiveFps / 15): 0 = native 15fps, 1 = 30, 2 = 60.
    public static int DeltaShift { get; private set; }

    public static int EffectiveFps => 15 << DeltaShift;

    public static bool Enabled => DeltaShift > 0;

    // The game's frame cap counts vblanks per game frame (native 4). 4>>shift:
    // 4 /  2 / 1 — beyond 60fps the vblank wait can't shrink further; 120+
    // additionally requires the host clock to run vblanks faster (unlimited mode).
    public static uint FrameCapVblanks => (uint)(4 >> Math.Min(DeltaShift, 2));

    // Round-half-away-from-zero divide by 2^DeltaShift (the 60fps patcher's
    // enemy-velocity cave) so slow/diagonal movers don't lose small velocities
    // to truncation. Identity at native rate.
    public static uint DivRound(uint v)
    {
        if (DeltaShift == 0) return v;
        int s = (int)v;
        int half = 1 << (DeltaShift - 1);
        return (uint)((s + (s < 0 ? -half : half)) >> DeltaShift);
    }

    // Every-Nth-call gates for per-frame effects whose magnitude is already
    // correct (they must fire less OFTEN, not smaller). Each has its own
    // counter, mirroring the patcher's self-counter caves. Always true at
    // native rate.
    static byte _magicTick, _enemyDmgTick, _poisonTick;

    public static bool MagicBeat() => Beat(ref _magicTick);
    public static bool EnemyDamageBeat() => Beat(ref _enemyDmgTick);
    public static bool PoisonBeat() => Beat(ref _poisonTick);

    static bool Beat(ref byte counter)
    {
        counter++;
        return (counter & ((1 << DeltaShift) - 1)) == 0;
    }

    // Frame-clock beat (the patcher's attack/waterscroll caves read the game's
    // live frame counter 0x801B2580 so those gates stay in phase with the world
    // tick). Mask == 0 at native rate.
    public static uint FrameClockMask => (uint)((1 << DeltaShift) - 1);
}
