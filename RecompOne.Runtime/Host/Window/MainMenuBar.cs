using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal static class MainMenuBar
{
    public static void Draw()
    {

        ConfigMenu();
        ModsMenu();
        DebugMenu();
        HelpMenu();
        MenuRegistry.DrawMenus();
        ImGui.EndMainMenuBar();
    }

    static void ConfigMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("Settings"))
        {
            if (ImGui.MenuItem("Settings..."))
                if (PanelManager.Get<SettingsPopup>() is { } popup) popup.IsOpen = true;

            ImGui.Separator();

            bool showBar = !ConfigManager.View.HideTopBar;
            if (ImGui.MenuItem("Show Menu Bar", "F1", showBar))
            {
                ConfigManager.View.HideTopBar = showBar;
                ConfigManager.SaveView(PanelManager.Panels);
            }

            bool gv = ConfigManager.View.GameView;
            if (ImGui.MenuItem("Game fills window", "F2", gv))
            {
                ConfigManager.View.GameView = !gv;
                ConfigManager.SaveView(PanelManager.Panels);
            }

            bool fs = ConfigManager.View.WindowMode == HostWindow.WinFullscreen;
            if (ImGui.MenuItem("Fullscreen", "F11", fs))
            {
                int m = fs ? HostWindow.WinWindowed : HostWindow.WinFullscreen;
                ConfigManager.View.WindowMode = m;
                ConfigManager.View.Fullscreen = m == HostWindow.WinFullscreen;
                HostWindow.ApplyWindowMode(m);
                ConfigManager.SaveView(PanelManager.Panels);
            }
            bool bl = ConfigManager.View.WindowMode == HostWindow.WinBorderless;
            if (ImGui.MenuItem("Borderless", "Alt+Enter", bl))
            {
                int m = bl ? HostWindow.WinWindowed : HostWindow.WinBorderless;
                ConfigManager.View.WindowMode = m;
                ConfigManager.View.Fullscreen = false;
                HostWindow.ApplyWindowMode(m);
                ConfigManager.SaveView(PanelManager.Panels);
            }

            ImGui.EndMenu();
        }
    }
    static void ModsMenu()
    {
        if (!ImGui.BeginMenu("Mods")) return;

        if (ImGui.MenuItem("Mods..."))
            if (PanelManager.Get<Modding.ModsPopup>() is { } popup) popup.IsOpen = true;

        ImGui.EndMenu();
    }

    static void DebugMenu()
    {
        if (!ImGui.BeginMenu("Debug")) return;

        if (ImGui.BeginMenu("GPU"))
        {
            Toggle<OutputPanel>("Output");
            Toggle<VramViewerPanel>("VRAM Viewer");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("CPU"))
        {
            Toggle<CpuStatePanel>("CPU State");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Memory"))
        {
            Toggle<RamMapPanel>("RAM Map");
            Toggle<MemoryEditorPanel>("Memory Editor");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Audio"))
        {
            Toggle<SpuViewerPanel>("SPU Viewer");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("CD"))
        {
            Toggle<CdDebugPanel>("CD Debug");
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("System"))
        {
            Toggle<OverlayEventsPanel>("Overlay Events");
            Toggle<ConsolePanel>("Console");
            ImGui.EndMenu();
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Reset View")) ConfigManager.ResetView(PanelManager.Panels);
        
        ImGui.EndMenu();
    }

    static void HelpMenu()
    {
        if (!ImGui.BeginMenu("Help")) return;
        if (ImGui.MenuItem("About"))
            if (PanelManager.Get<AboutPopup>() is { } about) about.IsOpen = true;

        ImGui.EndMenu();
    }

    static void Toggle<T>(string label) where T : class, IPanel
    {
        var panel = PanelManager.Get<T>();
        if (panel == null) return;
        bool open = panel.IsOpen;
        if (ImGui.MenuItem(label, null, open)) panel.IsOpen = !open;
    }
}
