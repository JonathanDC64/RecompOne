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
        bool fullscreen = ConfigManager.View.Fullscreen;
        if (ImGui.Checkbox("Fullscreen", ref fullscreen))
        {
            ConfigManager.View.Fullscreen = fullscreen;
            HostWindow.SetFullscreen(fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        bool native = ConfigManager.View.NativeResolution;
        if (ImGui.Checkbox("Native resolution", ref native))
        {
            ConfigManager.View.NativeResolution = native;
            Hle.GpuHle.NativeResolution = native;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (!ConfigManager.View.NativeResolution)
        {
            int scale = Math.Clamp(ConfigManager.View.InternalScale, 1, 8);
            string[] scaleNames = { "1x Native", "2x Native", "3x Native (720p)", "4x Native", "5x Native (1080p)", "6x Native (1440p)", "7x Native", "8x Native" };
            int scaleIdx = scale - 1;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Internal resolution", ref scaleIdx, scaleNames, scaleNames.Length))
            {
                ConfigManager.View.InternalScale = scaleIdx + 1;
                ConfigManager.SaveView(PanelManager.Panels);
            }
        }

        int wantScale = ConfigManager.View.NativeResolution ? 1 : Math.Clamp(ConfigManager.View.InternalScale, 1, 8);
        if (wantScale != Hle.GlVram.Scale)
            ImGui.TextDisabled("restart required");

        bool texFilter = ConfigManager.View.TextureFilter;
        if (ImGui.Checkbox("Texture filtering (bilinear, experimental)", ref texFilter))
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
        if (ImGui.Checkbox("Sprite texture filtering (bilinear, experimental)", ref sprFilter))
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

        bool pgxp = ConfigManager.View.PgxpGeometryCorrection;
        if (ImGui.Checkbox("PGXP Geometry Correction (experimental)", ref pgxp))
        {
            ConfigManager.View.PgxpGeometryCorrection = pgxp;
            Pgxp.Enabled = pgxp;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sub-pixel vertex precision (removes PS1 polygon wobble).\nExperimental: occasional hairline cracks remain. Takes effect immediately.");

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
                ImGui.SetTooltip("Track vertex precision through CPU arithmetic (DuckStation PGXP CPU\nmode). Fixes the residual hairline seams on CPU-repacked vertices.\nSmall CPU cost. Takes effect immediately.");

            bool pctTex = ConfigManager.View.PgxpPerspectiveTextures;
            if (ImGui.Checkbox("Perspective correct textures", ref pctTex))
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
                ImGui.SetTooltip("Cull polygons using precise coordinates. Keeps sliver 'stitch'\ntriangles alive - fixes hairline black gaps between polygons.");

            bool pctCol = ConfigManager.View.PgxpPerspectiveColors;
            if (ImGui.Checkbox("Perspective correct colors", ref pctCol))
            {
                ConfigManager.View.PgxpPerspectiveColors = pctCol;
                Pgxp.PerspectiveColors = pctCol;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Interpolate vertex colors with depth. Changes the original shading\n(KF2 uses vertex colors for distance fog) - usually best left off.");

            ImGui.Unindent();
        }
    }
}
