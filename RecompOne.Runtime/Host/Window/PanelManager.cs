namespace RecompOne.Runtime.Host.Window;

public static class PanelManager
{
    // Optional game-registered ImGui draw callback, run inside the ImGui frame
    // after panels/windows (e.g. an FPS overlay).
    public static Action? OverlayDraw;

    static readonly List<IPanel> _panels = [];

    public static void Register(IPanel panel) => _panels.Add(panel);
    public static IReadOnlyList<IPanel> Panels => _panels;

    public static T? Get<T>() where T : class, IPanel
    {
        foreach (var p in _panels)
            if (p is T t) return t;
        return null;
    }

    public static void DrawPanels()
    {
        foreach (var panel in _panels)
            if (panel.IsOpen) panel.Draw();
    }

    public static void Shutdown() => _panels.Clear();
}
