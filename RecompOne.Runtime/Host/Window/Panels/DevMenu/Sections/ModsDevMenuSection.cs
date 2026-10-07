using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Mods list, asset reload and mod-registered menus (all also in the F1 menu bar).</summary>
internal sealed class ModsDevMenuSection : IDevMenuSection
{
    public string Id => "mods";
    public string Title => "Mods";
    public int Order => 28;

    public void Draw()
    {
        ImGui.TextUnformatted($"{ModLoader.LoadedMods.Count} mod(s) loaded");
        ImGuiEx.TextDisabled("Enable mods in the launcher, then restart.");
        ImGui.Spacing();

        if (ImGui.Button("Mods...", new Vector2(-1, 0)))
            if (PanelManager.Get<ModsPopup>() is { } popup) popup.IsOpen = true;

        if (ImGui.Button("Reload assets", new Vector2(-1, 0)))
            ModLoader.ReloadAssets();
        ImGuiEx.TextDisabled("PNG / disc packs. C# hooks need a restart.");

        if (!MenuRegistry.HasMenus) return;
        ImGui.Spacing();
        ImGui.TextUnformatted("Mod menus");
        MenuRegistry.DrawMenus();
    }
}
