using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

internal sealed class CheatsDevMenuSection : IDevMenuSection
{
    public string Id => "cheats";
    public string Title => "Cheats";
    public int Order => 0;

    public void Draw()
    {
        ImGuiEx.TextDisabled("NTSC-U (SCUS-94900) — applied every frame while enabled");
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
        ImGuiEx.TextDisabled("Unlocks the level select flag in RAM.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("99 Lives (map)", new Vector2(-1, 0)))
            CheatManager.Give99LivesOnMap();

        if (ImGui.Button("Instant Save Menu", new Vector2(-1, 0)))
            CheatManager.OpenInstantSaveMenu();
    }
}
