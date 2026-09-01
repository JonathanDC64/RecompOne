using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string TitleKey => "settings.display";
    public int Order => 5;

    private static readonly string[] Backends = ["auto", "gl45", "gl33", "gl21"];

    public void Draw()
    {
        var fullscreen = ConfigManager.View.Fullscreen;
        if (ImGui.Checkbox(Localization.T("settings.display.fullscreen"), ref fullscreen))
        {
            ConfigManager.View.Fullscreen = fullscreen;
            HostWindow.SetFullscreen(fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        var vsync = ConfigManager.View.VSync;
        if (ImGui.Checkbox(Localization.T("settings.display.vsync"), ref vsync))
        {
            ConfigManager.View.VSync = vsync;
            HostWindow.SetVSync(vsync);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.vsync_hint"));

        var scale = ConfigManager.View.RenderScale;
        if (ImGui.SliderInt(Localization.T("settings.display.render_scale"), ref scale, 1, 8, "%dx"))
        {
            ConfigManager.View.RenderScale = scale;
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show(Localization.T("common.restart_required"));
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("settings.display.render_scale_hint"));

        var lines = Hle.GpuHle.LastDisplayH;
        var width = Hle.GpuHle.LastDisplayW;
        if (lines > 0)
            ImGui.TextDisabled(Localization.T("settings.display.render_scale_lines",
                width, lines, width * scale, lines * scale, scale));

        if (scale != Hle.GlVram.Scale)
            ImGui.TextDisabled(Localization.T("settings.display.restart_pending"));

        var filter = ConfigManager.View.TextureFilter;
        if (ImGui.Checkbox("Bilinear filtering (world)", ref filter))
        {
            ConfigManager.View.TextureFilter = filter;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smooths textures on 3D polygons. Taps are clamped to each primitive's UV box, so textures cannot bleed into their neighbours on the same page. Requires PGXP to tell world polygons from 2D.");

        var filterSprite = ConfigManager.View.SpriteTextureFilter;
        if (ImGui.Checkbox("Bilinear filtering (sprites/UI)", ref filterSprite))
        {
            ConfigManager.View.SpriteTextureFilter = filterSprite;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Usually best left off: the HUD, text and 2D sprites are drawn at native resolution and look softer when filtered.");

        var dither = ConfigManager.View.Dither;
        if (ImGui.Checkbox("Dithering", ref dither))
        {
            ConfigManager.View.Dither = dither;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The PS1's ordered 4x4 dither. Accurate to hardware, but at a high\n" +
                             "internal resolution it shows as a cross-hatch on gradients that a\n" +
                             "real composite signal would have blurred away.");

        ImGui.Separator();

        // PGXP (geometry correction). Sub-options only matter while it is on, so
        // they are indented under it. Plain labels: there are no localisation
        // entries for these keys yet and T() would echo the key back.
        var pgxp = ConfigManager.View.PgxpGeometryCorrection;
        if (ImGui.Checkbox("PGXP geometry correction", ref pgxp))
        {
            ConfigManager.View.PgxpGeometryCorrection = pgxp;
            Pgxp.ApplyFromConfig();
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses the GTE's pre-truncation coordinates to remove polygon\n" +
                             "wobble and correct perspective. Experimental.");

        if (pgxp)
        {
            ImGui.Indent();

            var cpuMode = ConfigManager.View.PgxpCpuMode;
            if (ImGui.Checkbox("CPU mode", ref cpuMode))
            {
                ConfigManager.View.PgxpCpuMode = cpuMode;
                Pgxp.ApplyFromConfig();
                ConfigManager.SaveView(PanelManager.Panels);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Propagates precision through CPU arithmetic, for games that\n" +
                                 "repack vertex words. Costs some performance.");

            var pctTex = ConfigManager.View.PgxpPerspectiveTextures;
            if (ImGui.Checkbox("Perspective-correct textures", ref pctTex))
            {
                ConfigManager.View.PgxpPerspectiveTextures = pctTex;
                Pgxp.ApplyFromConfig();
                ConfigManager.SaveView(PanelManager.Panels);
            }

            var pctCol = ConfigManager.View.PgxpPerspectiveColors;
            if (ImGui.Checkbox("Perspective-correct colours", ref pctCol))
            {
                ConfigManager.View.PgxpPerspectiveColors = pctCol;
                Pgxp.ApplyFromConfig();
                ConfigManager.SaveView(PanelManager.Panels);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off by default: games often tune gouraud fog/lighting to the\n" +
                                 "console's affine interpolation.");

            var cull = ConfigManager.View.PgxpCullingCorrection;
            if (ImGui.Checkbox("Culling correction", ref cull))
            {
                ConfigManager.View.PgxpCullingCorrection = cull;
                Pgxp.ApplyFromConfig();
                ConfigManager.SaveView(PanelManager.Panels);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Computes NCLIP from precise coordinates so sliver triangles\n" +
                                 "are not culled into hairline gaps.");

            ImGui.Unindent();
        }

        ImGui.Separator();

        var index = Array.IndexOf(Backends, ConfigManager.View.GpuBackend);
        if (index < 0) index = 0;
        if (ImGui.Combo(Localization.T("settings.display.backend"), ref index, Backends, Backends.Length))
        {
            ConfigManager.View.GpuBackend = Backends[index];
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show(Localization.T("common.restart_required"));
        }

        ImGui.TextDisabled(Localization.T("settings.display.backend_running", Hle.GpuBackendFactory.Selected));
    }
}