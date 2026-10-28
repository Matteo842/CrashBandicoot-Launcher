using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Settings page drawn inside the pause card (<see cref="PauseMenuPopup"/>): section tabs
/// across the top like the launcher Settings sheet, the selected section below. Not a
/// window of its own.
/// </summary>
internal sealed class SettingsView
{
    string _selectedId = "";
    /// <summary>Frames the old-style tab bar needs to show a tab picked with L1/R1.</summary>
    int _stepFrames;

    /// <summary>Previous / next section, wrapping like the launcher sheet (L1/R1).</summary>
    public void StepTab(int direction)
    {
        var sections = SettingsRegistry.Sections;
        if (ResolveSelection(sections) is not { } current) return;
        int index = 0;
        for (int i = 0; i < sections.Count; i++)
            if (sections[i] == current) index = i;
        _selectedId = sections[(index + direction + sections.Count) % sections.Count].Id;
        _stepFrames = 2;
    }

    /// <summary>Draws the tabs and the selected section, <paramref name="height"/> px tall.</summary>
    public void Draw(float ui, float height)
    {
        var sections = SettingsRegistry.Sections;
        var current = ResolveSelection(sections);
        float top = ImGui.GetCursorPosY();

        if (current != null)
        {
            var titles = new string[sections.Count];
            int index = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                titles[i] = sections[i].Title;
                if (sections[i] == current) index = i;
            }
            bool stepping = _stepFrames > 0;
            int picked = CrashUi.Tabs("settings-tabs", titles, index, ui, select: stepping);
            if (stepping)
                _stepFrames--;
            else if (picked != index)
            {
                current = sections[picked];
                _selectedId = current.Id;
            }
            ImGui.Dummy(new Vector2(0f, 2f * ui));
        }

        float used = ImGui.GetCursorPosY() - top;
        DrawContent(current, Math.Max(40f * ui, height - used));
    }

    ISettingsSection? ResolveSelection(IReadOnlyList<ISettingsSection> sections)
    {
        if (sections.Count == 0) return null;
        foreach (var s in sections)
            if (s.Id == _selectedId) return s;
        _selectedId = sections[0].Id;
        return sections[0];
    }

    static void DrawContent(ISettingsSection? current, float height)
    {
        // Flattened: D-pad / arrows go from the tabs straight to the options, no "enter box" step.
        ImGui.BeginChild("##settings-content", new Vector2(0f, height), ImGuiChildFlags.Border,
            ImGuiWindowFlags.NavFlattened);

        if (current == null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted("No settings available.");
            ImGui.PopStyleColor();
        }
        else
        {
            // The tab already names the section, so no title row here.
            current.Draw();
            foreach (var ext in SettingsRegistry.GetExtensions(current.Id))
                ext();
        }

        ImGui.EndChild();
    }
}
