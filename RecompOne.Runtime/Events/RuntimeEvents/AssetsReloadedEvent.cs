namespace RecompOne.Runtime.Events;

/// <summary>
/// Fired after <see cref="Modding.ModLoader.ReloadAssets"/> refreshes texture / disc packs.
/// C# hooks are not recompiled — only PNG and disc overlays.
/// </summary>
public sealed class AssetsReloadedEvent : IEvent
{
    public int TextureCount { get; init; }
    public int DiscRemapCount { get; init; }
}
