using System.Numerics;
using ImGuiNET;
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
        ImGui.Separator();
        ImGui.Spacing();

        if (mods.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted("No mods loaded. Drop mods into the mods folder and restart.");
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
