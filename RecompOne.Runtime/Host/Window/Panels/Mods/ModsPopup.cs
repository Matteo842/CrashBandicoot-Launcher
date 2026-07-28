using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Modding;

internal sealed class ModsPopup : IPanel
{
    public string Name => "Mods";
    public bool IsOpen { get; set; }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(new Vector2(560, 340), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));

        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        var mods = ModLoader.LoadedMods;
        ImGui.TextUnformatted($"{mods.Count} mod(s) loaded");
        ImGui.SameLine();
        ImGui.TextDisabled($"· {TextureReplacements.Count} tex · {DiscOverlay.RemapCount} disc");

        if (ImGui.Button("Reload assets"))
            ModLoader.ReloadAssets();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read mod.json + PNG/disc packs without restarting.\nC# hooks are not recompiled.");

        if (ModLoader.LastAssetReloadUtc is { } when)
        {
            ImGui.SameLine();
            var local = when.ToLocalTime().ToString("HH:mm:ss");
            ImGui.TextDisabled($"last reload {local} ({ModLoader.LastAssetReloadTextureCount} tex, {ModLoader.LastAssetReloadDiscCount} disc)");
        }

        if (AssetHotReload.IsWatching)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("· watching");
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (mods.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted("No mods loaded. Enable mods in the launcher Mods menu, then restart.");
            ImGui.PopStyleColor();
        }
        else if (ImGui.BeginTable("##mods-table", 4,
                     ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Id");
            ImGui.TableSetupColumn("Version");
            ImGui.TableSetupColumn("Author");
            ImGui.TableHeadersRow();

            foreach (var mod in mods)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(mod.Name);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(mod.Id);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(mod.Version);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(mod.Author);
            }

            ImGui.EndTable();
        }

        IsOpen = open;
        ImGui.End();
    }
}
