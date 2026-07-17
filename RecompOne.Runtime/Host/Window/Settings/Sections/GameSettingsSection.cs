using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

// Game-specific (KF2) options, as opposed to the generic display/audio/input
// sections: delta-time target framerate and FMV auto-skip.
internal sealed class GameSettingsSection : ISettingsSection
{
    public string Id => "game";
    public string Title => "Game";
    public int Order => 7;

    bool _customFps;
    int _customFpsValue;

    public void Draw()
    {
        DrawFramerate();

        ImGui.Spacing();

        bool autoSkip = ConfigManager.View.FmvAutoSkip;
        if (ImGui.Checkbox("Skip FMVs automatically", ref autoSkip))
        {
            ConfigManager.View.FmvAutoSkip = autoSkip;
            FmvSkip.AutoSkip = autoSkip;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Skip every movie immediately (including the long unskippable\nsecond new-game FMV). Off = movies play; Start/Cross skips them.");
    }

    // Delta-time / target framerate. The game natively ticks the world at 15fps
    // (menus/FMVs are already 60); raising the target makes the world tick more
    // often while runtime hooks scale every per-frame quantity (movement, turning,
    // gravity, enemies, gauges, timers, effects) so real-time game speed is
    // unchanged. Values snap to 15/30/60 for now (the game's scaling sites are
    // power-of-two shifts); 120+/unlimited needs host vblank-rate work.
    void DrawFramerate()
    {
        int fps = ConfigManager.View.TargetFps;
        string[] names = { "Original (15 FPS)", "30 FPS", "60 FPS", "Custom..." };
        int idx = _customFps ? 3 : fps >= 60 ? 2 : fps >= 30 ? 1 : 0;
        if (!_customFps && fps != 15 && fps != 30 && fps != 60) { _customFps = true; _customFpsValue = fps; idx = 3; }

        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Target framerate", ref idx, names, names.Length))
        {
            if (idx == 3) { _customFps = true; _customFpsValue = fps; }
            else
            {
                _customFps = false;
                SetTargetFps(idx == 0 ? 15 : idx == 1 ? 30 : 60);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delta-time: runs the game world at a higher framerate while keeping\ngame speed identical (movement, enemies, timers, effects all scaled).\nTakes effect immediately.");

        if (_customFps)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(120);
            if (_customFpsValue == 0) _customFpsValue = fps;
            if (ImGui.InputInt("Custom FPS", ref _customFpsValue))
            {
                _customFpsValue = Math.Clamp(_customFpsValue, 1, 480);
                SetTargetFps(_customFpsValue);
            }
            ImGui.TextDisabled("snaps to 15/30/60 for now; 120+/unlimited planned");
            ImGui.Unindent();
        }

        if (Speed.TargetFps != Speed.EffectiveFps)
            ImGui.TextDisabled($"running at {Speed.EffectiveFps} FPS");
    }

    static void SetTargetFps(int fps)
    {
        ConfigManager.View.TargetFps = fps;
        Speed.TargetFps = fps;
        ConfigManager.SaveView(PanelManager.Panels);
    }
}
