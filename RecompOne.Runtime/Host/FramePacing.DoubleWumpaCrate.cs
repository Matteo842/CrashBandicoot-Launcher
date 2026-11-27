using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #216 double wumpa crate (ViewConfig.DoubleWumpaCrate), Crash 3 style: the bouncy
    /// wumpa crate (BoxsC <c>S_BOX_FRUIT</c>) gives 2 wumpa per bounce instead of 1.
    /// Each bounce (<c>Box_Fruit_Bounce</c>) and the 10th-bounce break
    /// (<c>Box_Break_Reward</c>, BoxReward 76) spawn one FruiC <c>S_FRUIT_AUTO</c> (or
    /// <c>S_FRUIT_HOP</c> on the ground) with reward 76. Its first update runs
    /// <c>FruitInit</c>: <c>FruitAmount = 1.0</c>, then for AUTO straight into
    /// <c>Fruit_Collect</c>. We set <c>FruitAmount = 2.0</c> there, so the game's own
    /// loop (the same one the 10-wumpa invincible break uses) sends one more
    /// <c>S_FRUIT_GOTO_HUD</c> fruit to the counter. WillC <c>AddFruit</c> still does
    /// the 100-wumpa life. The invincible-hit break (reward 78, ten fruit), 1-ups and
    /// other crates never match (FruitReward 76 + FruitAmount 1.0 from a fruit crate).
    /// </summary>
    const uint BoxSpawnFruit = 6u;
    const uint FruiSpawnHop = 12u;
    const uint FruiSpawnAuto = 13u;
    /// <summary>FruiC process vars: mem1..mem4 at +0x160, FruitAmount, …, FruitReward.</summary>
    const uint FruitAmountOff = 0x160u + 4u * 4u;
    const uint FruitRewardOff = 0x160u + 8u * 4u;
    const uint OneFruit = 0x100u;
    const uint FruitRewardWumpa = 76u << 8;

    /// <summary>Fruit from a wumpa crate, in its first update; 0 = none.</summary>
    static uint _wumpaCrateFruit;

    /// <summary>PreGoolObjectUpdate: FruitInit still sees the crate as <c>parent</c>.</summary>
    static void NoteWumpaCrateFruit(IMemory m, uint obj)
    {
        _wumpaCrateFruit = 0;
        if (!ConfigManager.View.DoubleWumpaCrate) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeFrui) return;
            uint spawn = m.ReadU32(obj + ObjSubtypeOff);
            if (spawn != FruiSpawnAuto && spawn != FruiSpawnHop) return;
            if (!IsFirstFrame(m, obj)) return;
            uint box = m.ReadU32(obj + ObjParentOff);
            if ((box & 0xFF000000u) != 0x80000000u
                || !TryReadGoolClass(m, box, out uint ptype, out _) || ptype != GoolTypeBox
                || m.ReadU32(box + ObjSubtypeOff) != BoxSpawnFruit)
                return;
            _wumpaCrateFruit = obj;
        }
        catch
        {
        }
    }

    /// <summary>
    /// PreChangeState (AUTO: FruitInit → Fruit_Collect in the first update) and
    /// GoolObjectUpdate post (HOP: still hopping in Fruit_Spawn). Once per fruit.
    /// </summary>
    static void DoubleWumpaCrateFruit(IMemory m, uint obj)
    {
        if (_wumpaCrateFruit == 0 || obj != _wumpaCrateFruit) return;
        _wumpaCrateFruit = 0;
        try
        {
            if (m.ReadU32(obj + FruitRewardOff) == FruitRewardWumpa
                && m.ReadU32(obj + FruitAmountOff) == OneFruit)
                m.WriteU32(obj + FruitAmountOff, 2u * OneFruit);
        }
        catch
        {
        }
    }
}
