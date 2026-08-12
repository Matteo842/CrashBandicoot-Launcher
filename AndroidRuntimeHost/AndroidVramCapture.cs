using RecompOne.Runtime.Hle;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// HLE draws into GL VRAM, not the CPU shadow. Capture on the EGL thread
/// (same as the desktop VRAM viewer) so the Android page has real pixels.
/// </summary>
static class AndroidVramCapture
{
    public const int Width = 1024;
    public const int Height = 512;

    static readonly ushort[] _pixels = new ushort[Width * Height];
    static readonly object Gate = new();
    static int _seq;
    static long _lastCapture;

    public static volatile bool Enabled;

    public static void CaptureFromGlThread(IGpuBackend? backend)
    {
        if (!Enabled || backend is not { Ready: true }) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastCapture != 0 &&
            (now - _lastCapture) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 300)
            return;
        _lastCapture = now;

        try
        {
            lock (Gate)
            {
                backend.ReadVram(0, 0, Width, Height, _pixels);
                _seq++;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CrashGPU", $"VRAM capture failed: {ex.Message}");
        }
    }

    public static bool CopyDownsampleArgb(int[] dest, int destW, int destH)
    {
        if (dest.Length < destW * destH) return false;
        lock (Gate)
        {
            if (_seq == 0) return false;
            for (var y = 0; y < destH; y++)
            {
                var srcRow = (y * Height / destH) * Width;
                var dstRow = y * destW;
                for (var x = 0; x < destW; x++)
                {
                    var p = _pixels[srcRow + x * Width / destW];
                    int r = (p & 0x1F) << 3;
                    int g = ((p >> 5) & 0x1F) << 3;
                    int b = ((p >> 10) & 0x1F) << 3;
                    dest[dstRow + x] = unchecked((int)(
                        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
                }
            }
        }
        return true;
    }
}
