using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Naughty Dog–style categorized developer menu (cheat / display / debug / system).</summary>
internal sealed class DevMenuPopup : IPanel
{
    public string Name => "Developer Menu";

    const float SidebarWidth = 168f;
    const string DefaultSectionId = "cheats";

    bool _open;
    bool _focusOnce;
    string _selectedId = "";

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            _focusOnce = false;
            if (_open && string.IsNullOrEmpty(_selectedId))
                _selectedId = ConfigManager.View.DevMenuSection;
        }
    }

    /// <summary>Open the menu focused on a specific section id (e.g. "cheats").</summary>
    public void OpenTo(string sectionId)
    {
        if (!string.IsNullOrWhiteSpace(sectionId))
            _selectedId = sectionId;
        IsOpen = true;
    }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        ImGui.SetNextWindowSize(new Vector2(720 * ui, 440 * ui), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(16 * ui, 48 * ui), ImGuiCond.Appearing);

        if (!_focusOnce)
        {
            ImGui.SetNextWindowFocus();
            _focusOnce = true;
        }

        // Semi-transparent ND-ish chrome
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.12f, 0.12f, 0.12f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.55f, 0.55f, 0.55f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.18f, 0.18f, 0.18f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.22f, 0.22f, 0.22f, 0.95f));

        bool open = _open;
        if (!ImGui.Begin(Name, ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings))
        {
            IsOpen = open;
            ImGui.End();
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);
            return;
        }

        var sections = DevMenuRegistry.Sections;
        var current = ResolveSelection(sections);

        DrawSidebar(sections, current, ui);
        ImGui.SameLine();
        DrawContent(current);

        IsOpen = open;
        ImGui.End();
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }

    IDevMenuSection? ResolveSelection(IReadOnlyList<IDevMenuSection> sections)
    {
        if (sections.Count == 0) return null;
        if (string.IsNullOrEmpty(_selectedId))
            _selectedId = ConfigManager.View.DevMenuSection;
        foreach (var s in sections)
            if (s.Id == _selectedId) return s;
        // Fall back to Cheats, then first section
        foreach (var s in sections)
            if (s.Id == DefaultSectionId) { _selectedId = s.Id; return s; }
        _selectedId = sections[0].Id;
        return sections[0];
    }

    void DrawSidebar(IReadOnlyList<IDevMenuSection> sections, IDevMenuSection? current, float ui)
    {
        ImGui.BeginChild("##devmenu-sidebar", new Vector2(SidebarWidth * ui, 0), ImGuiChildFlags.Border);

        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("DEVELOPER");
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();

        foreach (var s in sections)
        {
            bool selected = current == s;
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.92f, 0.45f, 1f));

            string label = selected ? $"> {s.Title}" : $"  {s.Title}";
            if (ImGui.Selectable($"{label}##sec-{s.Id}", selected))
            {
                _selectedId = s.Id;
                ConfigManager.View.DevMenuSection = s.Id;
                ConfigManager.SaveView(PanelManager.Panels);
            }

            if (selected)
                ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGuiEx.TextDisabled($"Toggle: {ConfigManager.View.CheatMenuKey}");

        ImGui.EndChild();
    }

    void DrawContent(IDevMenuSection? current)
    {
        ImGui.BeginChild("##devmenu-content", Vector2.Zero, ImGuiChildFlags.Border);

        if (current == null)
        {
            ImGuiEx.TextDisabled("No sections registered.");
        }
        else
        {
            ImGui.TextUnformatted(current.Title.ToUpperInvariant());
            ImGui.Separator();
            ImGui.Spacing();
            current.Draw();
        }

        ImGui.EndChild();
    }
}
