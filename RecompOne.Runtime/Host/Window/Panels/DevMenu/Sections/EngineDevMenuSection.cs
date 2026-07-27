using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Cheats;
using RecompOne.Runtime.Host.Diagnostics;

namespace RecompOne.Runtime.Host.Window;

internal sealed class EngineDevMenuSection : IDevMenuSection
{
    public string Id => "engine";
    public string Title => "Engine";
    public int Order => 40;

    public void Draw()
    {
        bool hud = ConfigManager.View.ShowDevHud;
        if (ImGui.Checkbox("Show FPS / Mem HUD", ref hud))
        {
            ConfigManager.View.ShowDevHud = hud;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Top-right overlay (independent of this menu)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Host process");
        ImGui.Text($"FPS: {HostDiagnostics.Fps:0.00}");
        ImGui.Text($"Working Set: {HostDiagnostics.FormatBytes(HostDiagnostics.WorkingSetBytes)}");
        ImGui.Text($"Private Bytes: {HostDiagnostics.FormatBytes(HostDiagnostics.PrivateBytes)}");
        ImGui.Text($"GC Heap: {HostDiagnostics.FormatBytes(HostDiagnostics.GcHeapBytes)}");
        var (g0, g1, g2) = HostDiagnostics.GcCollections;
        ImGui.Text($"GC collections: gen0={g0}  gen1={g1}  gen2={g2}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Known pools (explains ~100–300 MB)");
        ImGuiEx.TextDisabled("Managed / estimated GPU — not a full process accounting");
        ImGui.Spacing();

        long accounted = HostDiagnostics.FormatKnownPools(out var rows);
        if (ImGui.BeginTable("##pools", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Pool");
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Note");
            ImGui.TableHeadersRow();
            foreach (var (name, bytes, note) in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(bytes > 0 ? HostDiagnostics.FormatBytes(bytes) : "—");
                ImGui.TableNextColumn();
                ImGuiEx.TextDisabled(note);
            }
            ImGui.EndTable();
        }
        ImGui.Text($"Accounted (non-zero rows): {HostDiagnostics.FormatBytes(accounted)}");
        ImGuiEx.TextDisabled("+ JIT of game.recomp.dll, .NET/Silk/ImGui, display RTs, driver overhead");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Guest watches (SCUS-94900)");
        DrawGuest("ticks_elapsed", HostDiagnostics.GuestTicksElapsed);
        DrawGuest("frames_elapsed", HostDiagnostics.GuestFramesElapsed);
        DrawGuest("vblank counter", HostDiagnostics.GuestVblankCounter);
        DrawGuest("title_state", HostDiagnostics.GuestTitleState);
        DrawGuest("pads[0]", HostDiagnostics.GuestPads0);

        if (CheatManager.TryGetLevelId(out uint levelId))
            ImGui.Text($"level_id: {levelId}  (0x{CheatManager.LevelIdAddr:X8})");
        else
            ImGui.TextDisabled("level_id: —");

        ImGuiEx.TextDisabled($"GL scale active: {Hle.GlVram.Scale}x   RamLogger: {(Runtime.RamLog.IsAllocated ? "on" : "off")}");
    }

    static void DrawGuest(string label, uint va)
    {
        if (HostDiagnostics.TryReadGuestU32(va, out var v))
            ImGui.Text($"{label}: {v}  (0x{va:X8})");
        else
            ImGui.TextDisabled($"{label}: —");
    }
}
