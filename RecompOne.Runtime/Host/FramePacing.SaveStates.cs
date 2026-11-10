namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// A save state replaced guest RAM mid-level. Drop every per-object and
    /// per-level cache the way NSInit does, then the same Reset + short 30 FPS
    /// settle as a pause resume; the clock re-reads ticks_elapsed.
    /// </summary>
    internal static void OnStateLoaded()
    {
        // The patched object is gone with the old RAM: forget it, write nothing.
        RestoreNativeWideObjectFrustum(null);
        ResetNativeWideRenderer();
        _nativeWidePickupZones.Clear();
        _fadeAcc = 0;
        _fadeHold = false;
        InterpReset();
        Reset();
        // Reset stashes a ride carry for an FPS switch; the restored Crash never had it.
        DropResetCarry();
    }
}
