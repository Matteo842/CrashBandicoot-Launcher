using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #77 New gem only (ViewConfig.NewGemOnly). Level Completion Screen, GamOC
    /// <c>Level_End_Gem_Path</c> (state 0 in the SCUS-94900 GamOC too): after Willy's
    /// Event33 sets LevelEndWaitState (r83) it walks every gem and key already owned,
    /// <c>if (ITEMPOOL &amp; bit) { playnull(0.5s); spawn old gem }</c>, and only then
    /// spawns the new gem, which flies to the counter and sends Event33 back to Willy
    /// (Level_End_Willy_Win saves the gem bit). With the option on, the pools read 0
    /// during this one object's update, so every old-gem test fails at once and the
    /// new gem follows after the single 0.5 s wait. The bits are put back right after
    /// its update; no other object sees the change and the save is untouched.
    /// </summary>
    const uint StateGemPath = 0u;
    const uint GamoLevelEndWaitOff = 0x60u + 83u * 4u;
    const uint GemParadePool1Addr = 0x80061988u;
    const uint GemParadePool2Addr = 0x800619ACu;

    static bool _gemParadeHidden;
    static uint _gemParadePool1;
    static uint _gemParadePool2;

    /// <summary>Pre GoolObjectUpdate: hide the owned gems from the gem path walk.</summary>
    static void HideGemParade(IMemory m, uint obj)
    {
        RestoreGemParade(m);
        if (!ConfigManager.View.NewGemOnly) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidLevelEnd
                || m.ReadU32(obj + ObjStateOff) != StateGemPath
                || m.ReadU32(obj + GamoLevelEndWaitOff) != 1u
                || !TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeGamO)
                return;
            _gemParadePool1 = m.ReadU32(GemParadePool1Addr);
            _gemParadePool2 = m.ReadU32(GemParadePool2Addr);
            m.WriteU32(GemParadePool1Addr, 0);
            m.WriteU32(GemParadePool2Addr, 0);
            _gemParadeHidden = true;
        }
        catch
        {
        }
    }

    /// <summary>Post GoolObjectUpdate: put the owned gems back (OR keeps any new bit).</summary>
    static void RestoreGemParade(IMemory m)
    {
        if (!_gemParadeHidden) return;
        _gemParadeHidden = false;
        try
        {
            m.WriteU32(GemParadePool1Addr, m.ReadU32(GemParadePool1Addr) | _gemParadePool1);
            m.WriteU32(GemParadePool2Addr, m.ReadU32(GemParadePool2Addr) | _gemParadePool2);
        }
        catch
        {
        }
    }
}
