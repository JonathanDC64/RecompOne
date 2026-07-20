using System.Globalization;

namespace RecompOne.Runtime.Config;

public class PanelState
{
    public bool Open { get; set; }
}

public class ViewConfig
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PanelState> Panels { get; set; } = [];

    public bool GetBool(string key, bool fallback = false)
        => Values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public void SetBool(string key, bool value) => Values[key] = value.ToString();

    public int GetInt(string key, int fallback = 0)
        => Values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public void SetInt(string key, int value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public float GetFloat(string key, float fallback = 0f)
        => Values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public void SetFloat(string key, float value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public string GetString(string key, string fallback = "")
        => Values.TryGetValue(key, out var v) ? v : fallback;

    public void SetString(string key, string value) => Values[key] = value;

    public bool HideTopBar
    {
        get => GetBool("HideTopBar");
        set => SetBool("HideTopBar", value);
    }

    public bool Fullscreen
    {
        get => GetBool("Fullscreen");
        set => SetBool("Fullscreen", value);
    }

    // 0 = Windowed, 1 = Fullscreen (exclusive), 2 = Borderless. Defaults from the
    // legacy Fullscreen bool so existing configs keep their fullscreen setting.
    public int WindowMode
    {
        get => GetInt("WindowMode", GetBool("Fullscreen") ? 1 : 0);
        set => SetInt("WindowMode", value);
    }

    public bool NativeResolution
    {
        get => GetBool("NativeResolution");
        set => SetBool("NativeResolution", value);
    }

    public bool PgxpGeometryCorrection
    {
        get => GetBool("PgxpGeometryCorrection");
        set => SetBool("PgxpGeometryCorrection", value);
    }

    public bool PgxpPerspectiveTextures
    {
        get => GetBool("PgxpPerspectiveTextures", true);
        set => SetBool("PgxpPerspectiveTextures", value);
    }

    public bool PgxpCpuMode
    {
        get => GetBool("PgxpCpuMode", true);
        set => SetBool("PgxpCpuMode", value);
    }

    public bool TextureFilter
    {
        get => GetBool("TextureFilter");
        set => SetBool("TextureFilter", value);
    }

    public bool SpriteTextureFilter
    {
        get => GetBool("SpriteTextureFilter");
        set => SetBool("SpriteTextureFilter", value);
    }

    public bool PgxpPerspectiveColors
    {
        get => GetBool("PgxpPerspectiveColors");
        set => SetBool("PgxpPerspectiveColors", value);
    }

    public bool PgxpCullingCorrection
    {
        get => GetBool("PgxpCullingCorrection", true);
        set => SetBool("PgxpCullingCorrection", value);
    }

    public int InternalScale
    {
        get => GetInt("InternalScale", 4);
        set => SetInt("InternalScale", value);
    }

    // Anisotropic filtering for world textures: 1 = off, else 2/4/8/16 taps.
    public int AnisoLevel
    {
        get => GetInt("AnisoLevel", 1);
        set => SetInt("AnisoLevel", value);
    }

}
