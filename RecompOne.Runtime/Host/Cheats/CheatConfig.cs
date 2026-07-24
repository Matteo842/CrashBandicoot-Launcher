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

    public static bool LevelSelect
    {
        get => ConfigManager.View.GetBool("Cheat.LevelSelect");
        set => ConfigManager.View.SetBool("Cheat.LevelSelect", value);
    }

    public static void Save() => ConfigManager.SaveView(Window.PanelManager.Panels);
}
