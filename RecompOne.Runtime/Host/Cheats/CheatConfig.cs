using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Cheats;

/// <summary>Persisted cheat toggles (stored in ViewConfig.Values).</summary>
public static class CheatConfig
{
    public static bool InfiniteLives
    {
        get => ConfigManager.View.GetBool("Cheat.InfiniteLives");
        set => ConfigManager.View.SetBool("Cheat.InfiniteLives", value);
    }

    public static bool InfiniteWumpa
    {
        get => ConfigManager.View.GetBool("Cheat.InfiniteWumpa");
        set => ConfigManager.View.SetBool("Cheat.InfiniteWumpa", value);
    }

    public static bool LevelSelect
    {
        get => ConfigManager.View.GetBool("Cheat.LevelSelect");
        set => ConfigManager.View.SetBool("Cheat.LevelSelect", value);
    }

    /// <summary>Default on: first launch after this cheat exists must not require a menu hunt.</summary>
    public static bool GodMode
    {
        get => ConfigManager.View.GetBool("Cheat.GodMode", true);
        set => ConfigManager.View.SetBool("Cheat.GodMode", value);
    }

    public static bool Fly
    {
        get => ConfigManager.View.GetBool("Cheat.Fly");
        set => ConfigManager.View.SetBool("Cheat.Fly", value);
    }

    public static void Save() => ConfigManager.SaveView(Window.PanelManager.Panels);
}
