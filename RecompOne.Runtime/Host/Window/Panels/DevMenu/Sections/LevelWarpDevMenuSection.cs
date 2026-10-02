using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Click a level to load it directly (same path as the warp map's loadlevel).</summary>
internal sealed class LevelWarpDevMenuSection : IDevMenuSection
{
    public string Id => "level-warp";
    public string Title => "Warp to Level";
    public int Order => 6;

    readonly record struct Entry(uint MapSlot, uint LevelId, bool Secret = false);
    sealed record Island(string Name, Entry[] Levels);

    // Warp-map order (IsldC MapSetLevelParams); IDs from levels.scus94900.json.
    static readonly Island[] Islands =
    [
        new("N. SANITY ISLAND",
        [
            new(1, 9), new(2, 12), new(3, 18), new(4, 14), new(5, 15),
            new(6, 10), new(7, 21), new(8, 17), new(9, 26),
        ]),
        new("WUMPA ISLAND",
        [
            new(10, 24), new(11, 23), new(12, 32), new(13, 28), new(14, 20),
            new(15, 19), new(50, 30, Secret: true), new(16, 46), new(17, 33),
        ]),
        new("CORTEX ISLAND",
        [
            new(18, 6), new(19, 3), new(20, 5), new(21, 7), new(22, 8),
            new(23, 22), new(24, 35), new(25, 40), new(40, 42, Secret: true),
            new(26, 29), new(27, 55), new(28, 27), new(29, 41), new(30, 44), new(31, 31),
        ]),
    ];

    static readonly Vector4 IslandColor = new(1f, 0.78f, 0.35f, 1f);

    public void Draw()
    {
        ImGuiEx.TextDisabled("Click to load. Clicking the current level restarts it.");
        if (CheatManager.WarpPending)
            ImGuiEx.TextColored(IslandColor, "Loading...");

        bool hasCurrent = CheatManager.TryGetLevelId(out uint current);

        for (int i = 0; i < Islands.Length; i++)
        {
            var island = Islands[i];
            if (i > 0)
            {
                ImGui.Spacing();
                ImGui.Spacing();
            }
            ImGui.Separator();
            ImGuiEx.TextColored(IslandColor, island.Name);
            ImGui.Spacing();

            foreach (var e in island.Levels)
            {
                string name = Catalog.Levels.TryGet(e.LevelId, out var info) ? info.Name : $"Level {e.LevelId}";
                if (e.Secret)
                    name += "  (secret)";
                bool isCurrent = hasCurrent && current == e.LevelId;
                if (ImGui.Selectable($"  {name}##warp{e.LevelId}", isCurrent))
                    CheatManager.RequestWarp(e.LevelId, e.MapSlot);
            }
        }
    }
}
