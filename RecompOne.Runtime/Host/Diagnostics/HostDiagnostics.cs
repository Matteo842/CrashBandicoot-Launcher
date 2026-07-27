using System.Diagnostics;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host.Diagnostics;

/// <summary>Host-side memory / FPS helpers for the Developer Menu System section and HUD.</summary>
internal static class HostDiagnostics
{
    static readonly Process _process = Process.GetCurrentProcess();
    static readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    static int _frames;
    static double _fps = 60;
    static double _lastFpsSampleMs;

    // Guest watches (SCUS-94900) — read-only
    public const uint GuestTicksElapsed = 0x80034520;
    public const uint GuestFramesElapsed = 0x80060E04;
    public const uint GuestVblankCounter = 0x800549F0;
    public const uint GuestTitleState = 0x800618D4;
    public const uint GuestPads0 = 0x8005E71C;

    public static double Fps => _fps;

    public static void TickFrame()
    {
        _frames++;
        double now = _fpsClock.Elapsed.TotalMilliseconds;
        double elapsed = now - _lastFpsSampleMs;
        if (elapsed >= 500)
        {
            _fps = _frames * 1000.0 / elapsed;
            _frames = 0;
            _lastFpsSampleMs = now;
        }
    }

    public static long WorkingSetBytes
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }
    }

    public static long PrivateBytes
    {
        get
        {
            _process.Refresh();
            return _process.PrivateMemorySize64;
        }
    }

    public static long GcHeapBytes => GC.GetTotalMemory(false);

    public static (int gen0, int gen1, int gen2) GcCollections =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public static long FormatKnownPools(out List<(string Name, long Bytes, string Note)> rows)
    {
        var list = new List<(string Name, long Bytes, string Note)>();
        long total = 0;

        void Add(string name, long bytes, string note)
        {
            list.Add((name, bytes, note));
            if (bytes > 0) total += bytes;
        }

        int ramMapCells = RamLogger.Width * RamLogger.Height;
        bool ramLogOn = Runtime.RamLog.IsAllocated;
        Add("RamLogger timestamps", ramLogOn ? ramMapCells * sizeof(uint) * 2L : 0,
            ramLogOn ? "allocated (RAM Map used)" : "lazy — not allocated yet");

        bool ramTexOn = HostWindow.RamMapBuffersAllocated;
        Add("RAM map CPU buffers", ramTexOn ? ramMapCells * 4L * 2 : 0,
            ramTexOn ? "front+back RGBA" : "lazy — not allocated yet");

        long psRam = Runtime.Mode == RunMode.Devkit
            ? MemoryMap.DevkitRamSize
            : MemoryMap.RetailRamSize;
        Add("PS main RAM", psRam, Runtime.Mode == RunMode.Devkit ? "Devkit 8MB" : "Retail 2MB");

        Add("VRAM shadow (CPU)", Gpu.VramWidth * Gpu.VramHeight * sizeof(ushort), "1024×512 ×2");
        Add("SPU RAM", Spu.RamSize, "512 KB");
        Add("GL vertex pool", 0x40000L * 32, "MaxVerts × ~32 B (+ matching VBO)");

        int scale = GlVram.Scale;
        long mainVram = (long)GlVram.Width * GlVram.Height * 2; // RGB5A1
        long stageVram = (long)VramShadow.Width * VramShadow.Height * 2;
        Add($"GL VRAM main ({scale}x)", mainVram, "GPU texture estimate");
        Add("GL VRAM stage (1x)", stageVram, "GPU texture estimate");

        bool ramMapOpen = Window.PanelManager.Get<Window.RamMapPanel>()?.IsOpen == true;
        bool vramOpen = Window.PanelManager.Get<Window.VramViewerPanel>()?.IsOpen == true;
        Add("RAM Map panel open", 0, ramMapOpen ? "yes (uploads heat map)" : "no");
        Add("VRAM Viewer open", 0, vramOpen ? "yes (extra upload)" : "no");

        rows = list;
        return total;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";
        return $"{mb / 1024.0:0.##} GB";
    }

    public static bool TryReadGuestU32(uint va, out uint value)
    {
        value = 0;
        var mem = Runtime.Mem;
        if (mem == null) return false;
        try
        {
            value = mem.ReadU32(va);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
