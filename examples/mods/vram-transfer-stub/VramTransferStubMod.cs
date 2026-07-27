using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Modding;

/// <summary>
/// Proves <see cref="VramTransferEvent"/> works: stamps a green check into each
/// CPU→VRAM upload (visible on textures) and a HUD badge in the display corner.
/// No game assets bundled — demo pixels only.
/// </summary>
public sealed class VramTransferStubMod : IMod
{
    const int LogLimit = 8;
    const ushort Green = 0x03E0;      // BGR555: max green
    const ushort GreenDark = 0x01A0;
    const ushort Black = 0x0000;

    static readonly string[] CheckRows =
    [
        "................",
        "..............#.",
        ".............##.",
        "............##..",
        "#..........##...",
        "##........##....",
        ".##......##.....",
        "..##....##......",
        "...##..##.......",
        "....####........",
        ".....##.........",
        "................",
        "................",
        "................",
        "................",
        "................",
    ];

    int _logged;
    int _loads;
    int _dispX, _dispY;
    bool _haveDisp;
    bool _announced;

    public void OnLoad()
    {
        Event.AddListener<VramTransferEvent>(OnTransfer);
        Event.AddListener<DispEnvEvent>(OnDisp);
        Event.AddListener<VSyncEvent>(OnVSync);
        Console.WriteLine("[Mods] vram-transfer-stub: green check on Load + HUD badge (top-left)");
    }

    public void OnUnload()
    {
        Event.RemoveListener<VramTransferEvent>(OnTransfer);
        Event.RemoveListener<DispEnvEvent>(OnDisp);
        Event.RemoveListener<VSyncEvent>(OnVSync);
    }

    void OnDisp(DispEnvEvent e)
    {
        _dispX = e.X;
        _dispY = e.Y;
        _haveDisp = true;
    }

    void OnTransfer(VramTransferEvent e)
    {
        if (e.Direction == VramTransfer.Load && e.Pixels != null && e.W >= 16 && e.H >= 16)
        {
            _loads++;
            StampCheck(e.Pixels, e.W, e.H, 0, 0);
            if (!_announced)
            {
                _announced = true;
                Console.WriteLine("[VRAM] stub OK — look for a green ✓ on newly loaded textures + top-left HUD");
            }
        }

        if (_logged >= LogLimit) return;
        _logged++;
        string extra = e.Direction == VramTransfer.Move
            ? $" src=({e.SrcX},{e.SrcY})"
            : $" pixels={e.PixelCount}";
        Console.WriteLine($"[VRAM] {e.Direction} ({e.X},{e.Y}) {e.W}x{e.H}{extra}");
    }

    void OnVSync(VSyncEvent _)
    {
        // Persistent badge in the visible framebuffer corner (soft + HLE/GL).
        int x = (_haveDisp ? _dispX : Runtime.Gpu?.DisplayX ?? 0) + 4;
        int y = (_haveDisp ? _dispY : Runtime.Gpu?.DisplayY ?? 0) + 4;
        BlitBadge(x, y);
    }

    static void StampCheck(ushort[] pixels, int w, int h, int ox, int oy)
    {
        const int s = 16;
        for (int row = 0; row < s && oy + row < h; row++)
        {
            var line = CheckRows[row];
            for (int col = 0; col < s && ox + col < w; col++)
            {
                int i = (oy + row) * w + (ox + col);
                if (i < 0 || i >= pixels.Length) continue;
                // Dark plate behind the check so it reads on any texture.
                pixels[i] = line[col] == '#' ? Green : GreenDark;
            }
        }
    }

    static void BlitBadge(int x, int y)
    {
        const int s = 16;
        var buf = new ushort[s * s];
        for (int i = 0; i < buf.Length; i++) buf[i] = Black;
        StampCheck(buf, s, s, 0, 0);

        var gpu = Runtime.Gpu;
        if (gpu != null)
        {
            for (int row = 0; row < s; row++)
            for (int col = 0; col < s; col++)
            {
                int vx = (x + col) & (Gpu.VramWidth - 1);
                int vy = (y + row) & (Gpu.VramHeight - 1);
                gpu.Vram[vy * Gpu.VramWidth + vx] = buf[row * s + col];
            }
        }

        if (GpuHle.Backend is { Ready: true } be)
            be.WriteVram(x, y, s, s, buf);
    }
}
