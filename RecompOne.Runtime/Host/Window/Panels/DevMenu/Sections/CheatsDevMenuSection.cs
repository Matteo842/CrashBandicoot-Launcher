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

        bool wumpa = CheatConfig.InfiniteWumpa;
        if (ImGui.Checkbox("Infinite Wumpa", ref wumpa))
        {
            CheatConfig.InfiniteWumpa = wumpa;
            CheatConfig.Save();
        }
        ImGuiEx.TextDisabled("Freezes Wumpa at 99 when a single active lives slot is found.");

        bool levelSelect = CheatConfig.LevelSelect;
        if (ImGui.Checkbox("Level Select", ref levelSelect))
        {
            CheatConfig.LevelSelect = levelSelect;
            CheatConfig.Save();
        }
        ImGuiEx.TextDisabled("Unlocks the level select flag in RAM — use on the warp map.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("99 Lives (map)", new Vector2(-1, 0)))
            CheatManager.Give99LivesOnMap();

        if (ImGui.Button("2nd Mask (map)", new Vector2(-1, 0)))
            CheatManager.Give2ndMaskOnMap();
        ImGuiEx.TextDisabled("Aku Aku ×2 on the warp map.");

        if (ImGui.Button("99 Wumpa (level)", new Vector2(-1, 0)))
        {
            if (!CheatManager.Give99Wumpa())
                NoticePopup.Show("No unique active level slot — try mid-level.");
        }

        if (ImGui.Button("Instant Save Menu", new Vector2(-1, 0)))
            CheatManager.OpenInstantSaveMenu();
    }
}
