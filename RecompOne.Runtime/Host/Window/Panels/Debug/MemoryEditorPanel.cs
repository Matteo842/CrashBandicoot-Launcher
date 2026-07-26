using System.Numerics;
using System.Text;
using ImGuiNET;
using RecompOne.Runtime.Memory;
using System.Globalization;

namespace RecompOne.Runtime.Host.Window;

internal sealed class MemoryEditorPanel : IPanel
{
    public string Name => "Memory Editor";
    public bool IsOpen { get; set; }
    const int BytesPerRow = 16;

    uint _baseAddr;
    string _addrInput = "80000000";
    bool _scrollPending;

    int _editAddr = -1;
    string _editBuf = "";
    bool _editFocusPending;

    public void JumpTo(uint physAddr)
    {
        _baseAddr = physAddr & ~(uint)(BytesPerRow - 1);
        _addrInput = $"{0x80000000u + physAddr:X8}";
        _scrollPending = true;
    }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(640, 480), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(Name, ref open)) { IsOpen = open; ImGui.End(); return; }

        var mem = Runtime.Mem as PSMemory;
        if (mem == null) { ImGui.TextDisabled("No memory"); ImGui.End(); IsOpen = open; return; }

        DrawToolbar();
        ImGui.Separator();
        DrawHexContent(mem);

        IsOpen = open;
        ImGui.End();
    }

    void DrawToolbar()
    {
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputText("##addr", ref _addrInput, 10,
            ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (uint.TryParse(_addrInput, NumberStyles.HexNumber, null, out uint parsed))
            {
                uint phys = parsed & 0x1FFFFFFFu;
                if (phys < 0x200000u) JumpTo(phys);
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Go to address (hex)");
        ImGui.SameLine();
        ImGui.Spacing(); //space is enug
        ImGui.SameLine();
        ImGui.TextDisabled("Click a byte to edit");
    }

    void DrawHexContent(PSMemory mem)
    {
        var ram = mem.Ram;
        int totalRows = (ram.Length + BytesPerRow - 1) / BytesPerRow;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));

        if (!ImGui.BeginChild("##hexscroll", Vector2.Zero, ImGuiChildFlags.None))
        {
            ImGui.PopStyleVar();
            ImGui.EndChild();
            return;
        }

        float rowH = ImGui.GetTextLineHeightWithSpacing();

        if (_scrollPending)
        {
            int targetRow = (int)(_baseAddr / BytesPerRow);
            ImGui.SetScrollY(targetRow * rowH - ImGui.GetWindowHeight() * 0.4f);
            _scrollPending = false;
        }

        float scrollY = ImGui.GetScrollY();
        int firstRow = Math.Max(0, (int)(scrollY / rowH) - 1);
        int visRows = (int)(ImGui.GetWindowHeight() / rowH) + 2;
        int lastRow = Math.Min(totalRows, firstRow + visRows);

        if (firstRow > 0)
            ImGui.Dummy(new Vector2(1f, firstRow * rowH));

        for (int row = firstRow; row < lastRow; row++)
            DrawRow(mem, row);

        float remaining = (totalRows - lastRow) * rowH;
        if (remaining > 0f)
            ImGui.Dummy(new Vector2(1f, remaining));

        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    static readonly StringBuilder _asciiSb = new(BytesPerRow);

    void DrawRow(PSMemory mem, int row)
    {
        var ram = mem.Ram;
        int baseOff = row * BytesPerRow;
        uint virtAddr = 0x80000000u + (uint)baseOff;
        var log = Runtime.RamLog;

        ImGuiEx.TextDisabled($"{virtAddr:X8}  ");
        ImGui.SameLine();

        _asciiSb.Clear();

        for (int col = 0; col < BytesPerRow; col++)
        {
            int idx = baseOff + col;
            byte b = idx < ram.Length ? ram[idx] : (byte)0;

            if (idx == _editAddr)
                DrawEditCell(mem, idx);
            else
                DrawByteCell(log, idx, b);

            if (col < BytesPerRow - 1)
            {
                ImGui.SameLine();
                if (col == 7) ImGui.TextDisabled("  ");
                else ImGui.TextDisabled(" ");
                ImGui.SameLine();
            }

            _asciiSb.Append(b >= 32 && b < 127 ? (char)b : '.');
        }

        ImGui.SameLine();
        ImGuiEx.TextDisabled($"  {_asciiSb}");
    }

    void DrawByteCell(RamLogger log, int idx, byte b)
    {
        float wHeat = log.HeatAt(idx);
        float rHeat = log.ReadHeatAt(idx);

        if (wHeat > 0.01f)
        {
            var wc = log.WriteColor;
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(wc.X, wc.Y, wc.Z, 0.4f + wHeat * 0.6f));
            ImGui.Text($"{b:X2}");
            ImGui.PopStyleColor();
        }
        else if (rHeat > 0.01f)
        {
            var rc = log.ReadColor;
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(rc.X, rc.Y, rc.Z, 0.4f + rHeat * 0.6f));
            ImGui.Text($"{b:X2}");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Text($"{b:X2}");
        }

        if (ImGui.IsItemClicked())
        {
            _editAddr = idx;
            _editBuf = $"{b:X2}";
            _editFocusPending = true;
        }
    }

    void DrawEditCell(PSMemory mem, int idx)
    {
        ImGui.PushID(idx);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.SetNextItemWidth(ImGui.CalcTextSize("FF").X + 2f);

        if (_editFocusPending)
        {
            ImGui.SetKeyboardFocusHere();
            _editFocusPending = false;
        }

        bool commit = ImGui.InputText("##edit", ref _editBuf, 2,
            ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.CharsUppercase |
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll |
            ImGuiInputTextFlags.NoHorizontalScroll);

        if (commit)
        {
            CommitEdit(mem, idx);
            int next = idx + 1;
            if (next < mem.Ram.Length)
            {
                _editAddr = next;
                _editBuf = $"{mem.Ram[next]:X2}";
                _editFocusPending = true;
            }
            else
            {
                _editAddr = -1;
            }
        }
        else if (ImGui.IsItemDeactivated())
        {
            CommitEdit(mem, idx);
            _editAddr = -1;
        }

        ImGui.PopStyleVar();
        ImGui.PopID();
    }

    //the edit needs to be writeen to ram after bf
    void CommitEdit(PSMemory mem, int idx)
    {
        if (byte.TryParse(_editBuf, NumberStyles.HexNumber, null, out byte val))
        {
            if (idx < mem.Ram.Length && mem.Ram[idx] != val)
                mem.WriteU8(0x80000000u + (uint)idx, val);
        }
    }
}
