using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Naughty Dog–style developer menu: narrow vertical "book" list.
/// Root = categories; click drills into a section; Back returns to the list.
/// </summary>
internal sealed class DevMenuPopup : IPanel
{
    public string Name => "Developer Menu";

    const float ListWidth = 230f;
    const float SectionWidth = 300f;

    bool _open;
    bool _focusOnce;
    bool _inSection;
    string _sectionId = "";

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            _focusOnce = false;
            if (_open)
                _inSection = false; // F3 → category list
        }
    }

    /// <summary>Open drilled into a specific section (e.g. menu bar → Cheats).</summary>
    public void OpenTo(string sectionId)
    {
        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            _sectionId = sectionId;
            ConfigManager.View.DevMenuSection = sectionId;
            _inSection = true;
        }
        _open = true;
        _focusOnce = false;
    }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        float width = (_inSection ? SectionWidth : ListWidth) * ui;

        // Fixed width; height grows with content (no empty ##selectables — those broke hit-testing).
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(width, 0f),
            new Vector2(width, vp.WorkSize.Y * 0.85f));
        ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(12 * ui, 40 * ui), ImGuiCond.FirstUseEver);

        if (!_focusOnce)
        {
            ImGui.SetNextWindowFocus();
            _focusOnce = true;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10 * ui, 8 * ui));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.14f, 0.14f, 0.14f, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.5f, 0.5f, 0.5f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.16f, 0.16f, 0.16f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.18f, 0.18f, 0.18f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.28f, 0.28f, 0.28f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.34f, 0.34f, 0.3f, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.4f, 0.38f, 0.22f, 0.85f));

        bool open = _open;
        var flags = ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin(Name, ref open, flags))
        {
            IsOpen = open;
            ImGui.End();
            PopChrome();
            return;
        }

        if (_inSection && (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsKeyPressed(ImGuiKey.Backspace)))
            _inSection = false;

        var sections = DevMenuRegistry.Sections;
        if (!_inSection)
            DrawRootList(sections);
        else
            DrawSectionPage(sections, ui);

        IsOpen = open;
        ImGui.End();
        PopChrome();
    }

    static void PopChrome()
    {
        ImGui.PopStyleColor(7);
        ImGui.PopStyleVar(3);
    }

    void DrawRootList(IReadOnlyList<IDevMenuSection> sections)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("DEVELOPER");
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();

        if (sections.Count == 0)
        {
            ImGuiEx.TextDisabled("No sections.");
            return;
        }

        foreach (var s in sections)
        {
            if (BookRow($"{s.Title}...", s.Id))
            {
                _sectionId = s.Id;
                ConfigManager.View.DevMenuSection = s.Id;
                ConfigManager.SaveView(PanelManager.Panels);
                _inSection = true;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGuiEx.TextDisabled($"Toggle: {ConfigManager.View.CheatMenuKey}");
    }

    void DrawSectionPage(IReadOnlyList<IDevMenuSection> sections, float ui)
    {
        if (BookRow("< Back", "back"))
        {
            _inSection = false;
            return;
        }

        ImGui.Separator();
        ImGui.Spacing();

        var section = FindSection(sections, _sectionId);
        if (section == null)
        {
            ImGuiEx.TextDisabled("Section not found.");
            return;
        }

        ImGui.TextUnformatted(section.Title.ToUpperInvariant());
        ImGui.Spacing();

        float maxBody = ImGui.GetMainViewport().WorkSize.Y * 0.55f;
        ImGui.BeginChild("##dev-section-body", new Vector2(0f, maxBody), ImGuiChildFlags.None);
        section.Draw();
        ImGui.EndChild();
    }

    static IDevMenuSection? FindSection(IReadOnlyList<IDevMenuSection> sections, string id)
    {
        foreach (var s in sections)
            if (s.Id == id) return s;
        return null;
    }

    /// <summary>
    /// ND-style row. Label is real Selectable text (defines hitbox + auto-size).
    /// Yellow ">" is painted in the leading gutter on hover.
    /// </summary>
    static bool BookRow(string label, string id)
    {
        // Leading spaces reserve room for the cursor glyph.
        bool clicked = ImGui.Selectable($"  {label}##{id}");
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            var min = ImGui.GetItemRectMin();
            float y = min.Y + (ImGui.GetItemRectSize().Y - ImGui.GetTextLineHeight()) * 0.5f;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(min.X, y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.92f, 0.4f, 1f)),
                ">");
        }
        return clicked;
    }
}
