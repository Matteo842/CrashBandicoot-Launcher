using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

internal sealed class CheatPopup : IPanel
{
    public string Name => "Cheat";
    public bool IsOpen { get; set; }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vector2(360, 260), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));

        bool open = IsOpen;
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

        ImGui.Spacing();
        ImGuiEx.TextDisabled("Freezes apply while the game is running.");

        IsOpen = open;
        ImGui.End();
    }
}
