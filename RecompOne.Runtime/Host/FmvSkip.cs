namespace RecompOne.Runtime.Host;

// FMV skip policy. Game-side hooks (see kf2/apply_hooks.py KF2HOOK:fmvskip) poll
// WantSkip() during movie playback and end the movie through the game's own
// "finished" path when it returns true. Designed to be driven from a settings UI:
//  - AllowSkip: pressing Start/Cross during any FMV skips it (default on).
//  - AutoSkip:  skip every FMV immediately without input (a future settings toggle).
public static class FmvSkip
{
    public static bool AllowSkip { get; set; } = true;
    public static bool AutoSkip { get; set; } =
        System.Environment.GetEnvironmentVariable("KF2_FMV_AUTOSKIP") == "1";

    const ushort StartBit = 1 << 3;
    const ushort CrossBit = 1 << 14;

    static bool _armed;

    // Poll from the game's frame-fetch during playback. Edge-triggered: the skip
    // buttons must be seen released during playback before a press counts, so the
    // button press that launched the movie can't skip it instantly.
    public static bool WantSkip()
    {
        if (AutoSkip) return true;
        if (!AllowSkip) return false;
        // Pad state is active-low; combine the real pad with bot-injected input.
        ushort s = (ushort)(Hardware.Controller.State & BotControl.InjectMask);
        bool pressed = (s & StartBit) == 0 || (s & CrossBit) == 0;
        if (!pressed) { _armed = true; return false; }
        if (!_armed) return false;
        _armed = false;
        return true;
    }
}
