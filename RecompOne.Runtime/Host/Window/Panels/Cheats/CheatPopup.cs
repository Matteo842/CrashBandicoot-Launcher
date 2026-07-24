using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

internal sealed class CheatPopup : IPanel
{
    public string Name => "Cheat";

    bool _open;
    bool _focusOnce;

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            _focusOnce = false;
        }
    }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        ImGui.SetNextWindowSize(new Vector2(420 * ui, 300 * ui), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!_focusOnce)
        {
            ImGui.SetNextWindowFocus();
            _focusOnce = true;
        }

        bool open = _open;
        if (!ImGui.Begin(Name, ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Cheat menu (NTSC-U)");
        ImGuiEx.TextDisabled($"Toggle with {ConfigManager.View.CheatMenuKey}");
        ImGui.Separator();
        ImGui.Spacing();

        bool infinite = CheatConfig.InfiniteLives;
        if (ImGui.Checkbox("Infinite Lives", ref infinite))
        {
            CheatConfig.InfiniteLives = infinite;
            CheatConfig.Save();
        }
        ImGuiEx.TextDisabled("Keeps map lives at 99 and freezes the active level lives counter.");

        bool levelSelect = CheatConfig.LevelSelect;
        if (ImGui.Checkbox("Level Select", ref levelSelect))
        {
            CheatConfig.LevelSelect = levelSelect;
            CheatConfig.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("99 Lives (map)", new Vector2(-1, 0)))
            CheatManager.Give99LivesOnMap();

        if (ImGui.Button("Instant Save Menu", new Vector2(-1, 0)))
            CheatManager.OpenInstantSaveMenu();

        IsOpen = open;
        ImGui.End();
    }
}
