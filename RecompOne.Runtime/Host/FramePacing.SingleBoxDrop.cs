using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #170 Japanese box drop (ViewConfig.SingleBoxDrop). Level Completion Screen, GamOC
    /// <c>Level_End_Willy_Lose</c>: NTSC-U drops one box on Crash per missed box,
    /// <c>do { … } while (i &lt; LevelBoxCount - PREVBOXCOUNT)</c>. NTSC-J sets
    /// <c>LevelBoxCount = PREVBOXCOUNT - 1.0</c> first, so the loop runs once, and its
    /// counter shows the full missed count from the start. Indexes are from the
    /// SCUS-94900 GamOC in S000002D.NSF (the goocdump J source is one state and one
    /// var off): state 15, LevelBoxCount r69, counter text value var0 r38.
    /// </summary>
    const uint LidLevelEnd = 45u;
    const uint GoolTypeGamO = 52u;
    const uint StateWillyLose = 15u;
    const uint GamoLevelBoxCountOff = 0x60u + 69u * 4u;
    const uint GamoCounterOff = 0x60u + 38u * 4u;
    /// <summary>c1 GOOL global 70 (gooc PREVBOXCOUNT): BOXCOUNT copied by WillC at the level end.</summary>
    const uint PrevBoxCountAddr = 0x800619A4u;
    const int OneBox = 0x100;

    static uint _boxDropUpdateObj;
    static uint _boxDropObj;
    static int _boxDropMissed;

    static void NoteBoxDropUpdate(uint obj) => _boxDropUpdateObj = obj;

    /// <summary>
    /// After Willy's own update, so the counter text (Willy's child, updated after it)
    /// never sees the loop's per-box <c>var0 = i + 1</c>.
    /// </summary>
    static void ApplySingleBoxDrop(IMemory m)
    {
        uint obj = _boxDropUpdateObj;
        _boxDropUpdateObj = 0;
        if (!ConfigManager.View.SingleBoxDrop || (obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidLevelEnd
                || m.ReadU32(obj + ObjStateOff) != StateWillyLose
                || !TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeGamO)
            {
                if (obj == _boxDropObj) _boxDropObj = 0;
                return;
            }
            if (obj != _boxDropObj)
            {
                _boxDropObj = obj;
                _boxDropMissed = 0;
            }
            // Checked every update: a save state loaded mid-drop restores the full count.
            int prev = (int)m.ReadU32(PrevBoxCountAddr);
            int total = (int)m.ReadU32(obj + GamoLevelBoxCountOff);
            if (total - prev > OneBox)
            {
                _boxDropMissed = (total - prev) >> 8;
                m.WriteU32(obj + GamoLevelBoxCountOff, (uint)(prev + OneBox));
            }
            if (_boxDropMissed > 1)
                m.WriteU32(obj + GamoCounterOff, (uint)_boxDropMissed);
        }
        catch
        {
        }
    }
}
