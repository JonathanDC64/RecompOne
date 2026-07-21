using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string Title => "Display";
    public int Order => 5;

    public void Draw()
    {
        // ---- Window ---------------------------------------------------------
        ImGui.SeparatorText("Window");

        string[] winModeNames = { "Windowed", "Fullscreen", "Borderless" };
        int winMode = Math.Clamp(ConfigManager.View.WindowMode, 0, 2);
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Window mode", ref winMode, winModeNames, winModeNames.Length))
        {
            ConfigManager.View.WindowMode = winMode;
            ConfigManager.View.Fullscreen = winMode == HostWindow.WinFullscreen; // keep legacy key in sync
            HostWindow.ApplyWindowMode(winMode);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Windowed / Fullscreen (exclusive) / Borderless (fullscreen window,\n"
                + "no display-mode change — smoother alt-tab). Alt+Enter toggles\n"
                + "windowed <-> borderless.");

        bool native = ConfigManager.View.NativeResolution;
        if (ImGui.Checkbox("Native resolution", ref native))
        {
            ConfigManager.View.NativeResolution = native;
            Hle.GpuHle.NativeResolution = native;
            // Apply live: native renders at 1x, otherwise at the internal scale.
            Hle.GpuHle.SetInternalScale(native ? 1 : Math.Clamp(ConfigManager.View.InternalScale, 1, MaxScale));
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Render at the PS1's native resolution (no supersampling).\nTakes effect immediately.");

        if (!ConfigManager.View.NativeResolution)
        {
            int scale = Math.Clamp(ConfigManager.View.InternalScale, 1, MaxScale);
            int scaleIdx = scale - 1;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Internal resolution", ref scaleIdx, ScaleNames, ScaleNames.Length))
            {
                int newScale = scaleIdx + 1;
                ConfigManager.View.InternalScale = newScale;
                Hle.GpuHle.SetInternalScale(newScale); // live — no restart
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Supersample the 3D render (higher = sharper, more GPU).\nTakes effect immediately.");
        }

        // ---- Texture filtering ---------------------------------------------
        ImGui.SeparatorText("Texture filtering");

        bool texFilter = ConfigManager.View.TextureFilter;
        if (ImGui.Checkbox("World textures (bilinear)", ref texFilter))
        {
            ConfigManager.View.TextureFilter = texFilter;
            Hle.GpuHle.TextureFilter = texFilter;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Smooths 3D world polygon textures (CLUT-aware bilinear) instead of\n"
                + "the blocky nearest look; also softens baked-in dithering.\n"
                + "Runtime-switchable.");

        bool sprFilter = ConfigManager.View.SpriteTextureFilter;
        if (ImGui.Checkbox("Sprites / UI (bilinear)", ref sprFilter))
        {
            ConfigManager.View.SpriteTextureFilter = sprFilter;
            Hle.GpuHle.SpriteTextureFilter = sprFilter;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Bilinear for 2D sprites / billboards / UI (affine primitives),\n"
                + "controlled separately from world polygons. Off = crisp nearest\n"
                + "(recommended for pixel UI/text).");

        // Anisotropic — independent of the base filter; reduces the grainy
        // shimmer on distant/oblique world textures (minification aliasing).
        int[] anisoLevels = { 1, 2, 4, 8, 16 };
        string[] anisoNames = { "Off", "2x", "4x", "8x", "16x" };
        int anisoIdx = Math.Max(0, Array.IndexOf(anisoLevels, ConfigManager.View.AnisoLevel));
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Anisotropic", ref anisoIdx, anisoNames, anisoNames.Length))
        {
            ConfigManager.View.AnisoLevel = anisoLevels[anisoIdx];
            Hle.GpuHle.AnisoLevel = anisoLevels[anisoIdx];
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Multi-samples distant/oblique world textures along the receding\n"
                + "axis to remove grainy shimmer. Combines with Nearest or Bilinear.\n"
                + "World polygons only. Higher = smoother but more GPU cost.");

        // ---- Geometry (PGXP) ------------------------------------------------
        ImGui.SeparatorText("Geometry (PGXP)");

        bool pgxp = ConfigManager.View.PgxpGeometryCorrection;
        if (ImGui.Checkbox("Enable PGXP geometry correction", ref pgxp))
        {
            ConfigManager.View.PgxpGeometryCorrection = pgxp;
            Pgxp.Enabled = pgxp;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sub-pixel vertex precision (removes PS1 polygon wobble).\nTakes effect immediately.");

        if (ConfigManager.View.PgxpGeometryCorrection)
        {
            ImGui.Indent();

            bool cpuMode = ConfigManager.View.PgxpCpuMode;
            if (ImGui.Checkbox("CPU mode (arithmetic tracking)", ref cpuMode))
            {
                ConfigManager.View.PgxpCpuMode = cpuMode;
                Pgxp.CpuMode = cpuMode;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Track vertex precision through CPU arithmetic (fixes residual\nhairline seams on CPU-repacked vertices). Small CPU cost.");

            bool pctTex = ConfigManager.View.PgxpPerspectiveTextures;
            if (ImGui.Checkbox("Perspective-correct textures", ref pctTex))
            {
                ConfigManager.View.PgxpPerspectiveTextures = pctTex;
                Pgxp.PerspectiveTextures = pctTex;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Interpolate texture coordinates with depth (removes affine texture warping).");

            bool cull = ConfigManager.View.PgxpCullingCorrection;
            if (ImGui.Checkbox("Culling correction", ref cull))
            {
                ConfigManager.View.PgxpCullingCorrection = cull;
                Pgxp.CullingCorrection = cull;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cull polygons using precise coordinates — keeps sliver 'stitch'\ntriangles alive, fixing hairline black gaps between polygons.");

            bool pctCol = ConfigManager.View.PgxpPerspectiveColors;
            if (ImGui.Checkbox("Perspective-correct colors", ref pctCol))
            {
                ConfigManager.View.PgxpPerspectiveColors = pctCol;
                Pgxp.PerspectiveColors = pctCol;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Interpolate vertex colors with depth. Changes original shading\n(KF2 uses vertex colors for distance fog) — usually best left off.");

            ImGui.Unindent();
        }
    }

    const int MaxScale = 9;
    static readonly string[] ScaleNames =
    {
        "1x Native", "2x Native", "3x Native (720p)", "4x Native", "5x Native (1080p)",
        "6x Native (1440p)", "7x Native", "8x Native (4K)", "9x Native",
    };
}
