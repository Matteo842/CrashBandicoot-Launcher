namespace RecompOne.Runtime.Modding;

public interface IMod
{
    /// <summary>called once after the mod assembly is loaded</summary>
    void OnLoad();

    /// <summary>called when the mod is unloaded.</summary>
    void OnUnload() { }
}
