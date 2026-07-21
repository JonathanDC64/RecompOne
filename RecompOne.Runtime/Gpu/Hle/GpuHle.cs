namespace RecompOne.Runtime.Hle;

public static class GpuHle
{
    public static bool Active { get; set; }
    public static IGpuBackend? Backend { get; set; }

    public static float WideAspect { get; set; }
    public static float OutputAspect { get; set; } = 4f / 3f;
    public static bool NativeResolution { get; set; }

    // Bilinear texture filtering (manual CLUT-aware bilinear in the prim fragment
    // shader). Runtime-switchable; off = nearest (PS1 look). Split like DuckStation:
    // TextureFilter = 3D world polygons; SpriteTextureFilter = 2D rects/sprites/UI.
    public static bool TextureFilter { get; set; }
    public static bool SpriteTextureFilter { get; set; }
    // Anisotropic filtering for world polygons: 1 = off, else 2/4/8/16 taps.
    public static int AnisoLevel { get; set; } = 1;

    // Change the internal-resolution scale live (no restart). Must be called on
    // the GL thread — the settings UI is, since it draws during the render pass.
    public static void SetInternalScale(int scale) => Backend?.SetInternalScale(scale);
    public static float TargetAspect { get; set; } = 4f / 3f;
    public const float BaseAspect = 4f / 3f;

    public struct DispRect { public int X, Y, W, H; public long Stamp; public bool Valid; }

    static readonly DispRect[] _rects = new DispRect[2];
    static long _stamp;

    public static void NotifyDisplay(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int slot = -1;
        for (int i = 0; i < _rects.Length; i++)
            if (_rects[i].Valid && _rects[i].X == x && _rects[i].Y == y) { slot = i; break; }
        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < _rects.Length; i++)
                if (!_rects[i].Valid || _rects[i].Stamp < _rects[slot].Stamp) slot = i;
        }
        _rects[slot] = new DispRect { X = x, Y = y, W = w, H = h, Stamp = ++_stamp, Valid = true };
    }

    public static int RectCount => _rects.Length;

    public static DispRect GetRect(int i) => _rects[i];

    public static int WideMargin(int w)
    {
        if (WideAspect <= 0f) return 0;
        int wide = (int)MathF.Ceiling(w * WideAspect / BaseAspect);
        return Math.Max(0, (wide - w + 1) / 2);
    }
}
