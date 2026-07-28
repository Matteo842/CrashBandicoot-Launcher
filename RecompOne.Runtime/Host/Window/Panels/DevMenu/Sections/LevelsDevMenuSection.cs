using ImGuiNET;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Read-only level status. Cheat toggles live under Cheats.</summary>
internal sealed class LevelsDevMenuSection : IDevMenuSection
{
    public string Id => "levels";
    public string Title => "Levels";
    public int Order => 5;

    public void Draw()
    {
        ImGui.TextUnformatted("Current level");
        if (CheatManager.TryGetLevelId(out uint id))
        {
            ImGui.Text($"ID: {id}  (0x{id:X})");
            if (Catalog.Levels.TryGet(id, out var info))
            {
                ImGui.Text($"{info.Name}");
                ImGuiEx.TextDisabled($"slug: {info.Slug}  kind: {info.Kind}");
            }
            else if (CheatManager.IsOnTitleMenuMap())
                ImGuiEx.TextDisabled("Title / menus / warp map / game over");
        }
        else
            ImGuiEx.TextDisabled("ID: — (no guest RAM)");

        ImGuiEx.TextDisabled($"VA 0x{Catalog.LevelIdAddr:X8}");
        ImGui.Spacing();
        ImGuiEx.TextDisabled("Level Select and other cheats are under Cheats.");
    }
}
