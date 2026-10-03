using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Click a level to load it directly (same path as the warp map's loadlevel).</summary>
internal sealed class LevelWarpDevMenuSection : IDevMenuSection
{
    public string Id => "level-warp";
    public string Title => "Warp to Level";
    public int Order => 6;

    static readonly Vector4 IslandColor = new(1f, 0.78f, 0.35f, 1f);

    public void Draw()
    {
        ImGuiEx.TextDisabled("Click to load. Clicking the current level restarts it.");
        ImGuiEx.TextDisabled("Bonus rounds load their level first; leaving the round returns there.");
        if (CheatManager.WarpPending)
            ImGuiEx.TextColored(IslandColor, "Loading...");

        bool hasCurrent = CheatManager.TryGetLevelId(out uint current);

        var groups = LevelWarpList.Groups;
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (i > 0)
            {
                ImGui.Spacing();
                ImGui.Spacing();
            }
            ImGui.Separator();
            ImGuiEx.TextColored(IslandColor, group.Name);
            ImGui.Spacing();

            foreach (var e in group.Entries)
            {
                bool isCurrent = hasCurrent && e.IsCurrent(current);
                if (ImGui.Selectable($"  {e.Label}##warp{i}_{e.LevelId}", isCurrent))
                    e.Warp();
            }
        }
    }
}
