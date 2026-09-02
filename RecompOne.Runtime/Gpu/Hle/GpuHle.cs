namespace RecompOne.Runtime.Hle;

public static class GpuHle
{
    public static bool Active { get; set; }
    public static IGpuBackend? Backend { get; set; }

    public static float WideAspect { get; set; }
    public static float OutputAspect { get; set; } = 4f / 3f;

    public static float SourceAspect { get; set; } = 4f / 3f;
    public static int LastDisplayW { get; set; }
    public static int LastDisplayH { get; set; }
    public static float TargetAspect { get; set; } = 4f / 3f;

    // Radial distance fog. The game's depth cue fades by forward-Z, which barely
    // touches geometry beside the camera, so a wide view shows its radial cull
    // boundary. These let a game project ask for the same fade computed radially.
    // Needs PGXP: the view depth reaches the shader as the PGXP W value.
    public static bool RadialFog { get; set; }
    public static float RadialFogNear { get; set; } = 20000f;
    public static float RadialFogFar { get; set; } = 24000f;
    public static float ProjH { get; set; } = 200f;
    public static float ProjCentreX { get; set; } = 160f;
    public static float RadialFogR { get; set; } = 5f;
    public static float RadialFogG { get; set; } = 5f;
    public static float RadialFogB { get; set; } = 5f;
    public const float BaseAspect = 4f / 3f;

    public struct DispRect
    {
        public int X, Y, W, H;
        public long Stamp;
        public bool Valid;
    }

    private static readonly DispRect[] _rects = new DispRect[2];
    private static long _stamp;

    public static void NotifyDisplay(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var slot = -1;
        for (var i = 0; i < _rects.Length; i++)
            if (_rects[i].Valid && _rects[i].X == x && _rects[i].Y == y)
            {
                slot = i;
                break;
            }

        if (slot < 0)
        {
            slot = 0;
            for (var i = 1; i < _rects.Length; i++)
                if (!_rects[i].Valid || _rects[i].Stamp < _rects[slot].Stamp)
                    slot = i;
        }

        _rects[slot] = new DispRect { X = x, Y = y, W = w, H = h, Stamp = ++_stamp, Valid = true };
        RectVersion++;
    }

    public static long RectVersion { get; private set; }

    public static int RectCount => _rects.Length;

    public static DispRect GetRect(int i)
    {
        return _rects[i];
    }

    public static int WideMargin(int w)
    {
        if (WideAspect <= 0f) return 0;
        var source = SourceAspect > 0f ? SourceAspect : BaseAspect;
        var wide = (int)MathF.Ceiling(w * WideAspect / source);
        return Math.Max(0, (wide - w + 1) / 2);
    }
}