using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

public static class MenuRegistry
{
    static readonly List<(string Label, Action Draw, string? Parent)> _menus = [];
    static readonly List<Action> _windows = [];

    public static void Register(string label, Action drawItems) => Register(label, drawItems, null);

    public static void Register(string label, Action drawItems, string? parent)
    {
        if (string.IsNullOrEmpty(label) || drawItems == null) return;
        _menus.Add((label, drawItems, parent));
    }

    public static void RegisterWindow(Action draw)
    {
        if (draw == null) return;
        _windows.Add(draw);
    }

    internal static void DrawMenus()
    {
        foreach (var (label, draw, parent) in _menus)
        {
            if (parent != null) continue;
            if (!ImGui.BeginMenu(label)) continue;
            draw();
            ImGui.EndMenu();
        }

        var seen = new HashSet<string>();
        foreach (var (_, _, parent) in _menus)
        {
            if (parent == null || !seen.Add(parent)) continue;
            if (!ImGui.BeginMenu(parent)) continue;
            foreach (var (label, draw, p) in _menus)
            {
                if (p != parent) continue;
                if (!ImGui.BeginMenu(label)) continue;
                draw();
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }
    }

    internal static void DrawWindows()
    {
        foreach (var draw in _windows)
            draw();
    }
}
