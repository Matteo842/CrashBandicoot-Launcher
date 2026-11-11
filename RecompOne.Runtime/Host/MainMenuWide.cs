using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// #189: the main menu in 16:9. Its backdrop is a 4:3 painting (zone 0c_pZ, 32x15 tiles of
/// 16x16, 8-bit, 46 palettes). The painting shows N. Sanity Beach, so a 16:9 shot of that
/// spot, filtered to look like the painting (artifacts/issue189/mainmenu), replaces it:
/// <list type="bullet">
/// <item>core: when the game uploads a retail tile or palette, its data is swapped for ours
/// (same VRAM place, same palette slot), so every renderer shows the new painting;</item>
/// <item>bands: 6 extra tile columns a side are written into empty slots of the title's
/// texture pages and drawn next to the game's own tiles, into the 16:9 side bands.</item>
/// </list>
/// Only with the Widescreen option on. Other title screens (Naughty Dog, options, ...) are untouched.
/// </summary>
static class MainMenuWide
{
    const uint LidTitle = 25u;
    /// <summary>c1 title.c <c>title</c>: title_struct; <c>state</c> at +0x35D0 (5 = main menu).</summary>
    const uint TitlePtrAddr = 0x800566E0u;
    const uint TitleStateOff = 0x35D0u;
    const uint TitleMainMenu = 5u;

    // Title tiles: 8-bit, uploaded 8x16 halfwords at (256 + u*8, 256 + v*16).
    const int TileX = 256, TileY = 256, TileWords = 128;
    // mdat 5MapP clut line 0: one 256x1 palette per row from (512, 304).
    const int ClutX = 512, ClutY = 304;

    sealed class Asset
    {
        public readonly uint[] TileHash = new uint[480];
        public readonly ushort[][] Tile = new ushort[480][];
        public readonly uint[] ClutHash = new uint[46];
        public readonly ushort[][] Clut = new ushort[46][];
        public readonly List<Band> Bands = [];
    }

    readonly record struct Band(int Col, int Row, int U, int V, int Clut, ushort[] Data);

    static Asset? _asset;
    static bool _loadFailed;
    static bool _bandsPending, _bandsReady;
    // Session that holds our band tiles (a relaunch in the same process is a new Gpu / memory).
    static Gpu? _ownerGpu;
    static IMemory? _ownerMem;
    // Band slots (8x16 word cells of the title pages, u 0..63 x v 0..15).
    static bool[,]? _slot;

    /// <summary>Main menu backdrop with our band tiles in VRAM: the frame can be shown in 16:9.</summary>
    public static bool Showing(IMemory m)
    {
        if (!_bandsReady || !ReferenceEquals(m, _ownerMem)) return false;
        try
        {
            uint title = m.ReadU32(TitlePtrAddr);
            if (m.ReadU32(Catalog.LevelIdAddr) == LidTitle && (title & 0xFF000000u) == 0x80000000u
                && m.ReadU32(title + TitleStateOff) == TitleMainMenu)
                return true;
        }
        catch
        {
        }
        // Left the main menu: VRAM is the game's again until the next menu upload.
        _bandsReady = false;
        return false;
    }

    /// <summary>This frame draws the band tiles (16:9 view on and the main menu showing).</summary>
    public static bool Drawing(Gpu gpu) => _bandsReady && ReferenceEquals(gpu, _ownerGpu) && GpuHle.WideFovActive;

    static bool Enabled => GpuHle.WideAspect > GpuHle.BaseAspect;

    /// <summary>HleLoadFlush: every CPU→VRAM upload before it is committed.</summary>
    public static void Patch(int x, int y, int w, int h, ushort[] px, int count)
    {
        // Anything else uploaded over a band slot (a demo / level page) takes it back.
        if (_bandsReady && OverlapsSlot(x, y, w, h)) _bandsReady = false;
        if (!Enabled || count < w * h) return;
        if (w == 8 && h == 16 && x >= TileX && y >= TileY && (x - TileX) % 8 == 0 && (y - TileY) % 16 == 0)
        {
            int index = TileIndex((x - TileX) / 8, (y - TileY) / 16);
            var a = Load();
            if (index < 0 || a == null || Hash(px, TileWords) != a.TileHash[index]) return;
            a.Tile[index].CopyTo(px, 0);
            _bandsPending = true;
        }
        else if (w == 256 && h == 1 && x == ClutX && y >= ClutY && y < ClutY + 46)
        {
            var a = Load();
            int c = y - ClutY;
            if (a == null || Hash(px, 256) != a.ClutHash[c]) return;
            a.Clut[c].CopyTo(px, 0);
        }
    }

    /// <summary>After a patched tile upload: put the band tiles in their slots (once per menu load).</summary>
    public static void WriteBands(Gpu gpu)
    {
        if (!_bandsPending) return;
        _bandsPending = false;
        // Once per menu load: every one of the 480 tile uploads would land here.
        if (_bandsReady && ReferenceEquals(gpu, _ownerGpu)) return;
        _bandsReady = false;
        var a = Load();
        if (a == null) return;
        // No "slot empty" check: after a demo / the intro the slots still hold that level's
        // pages, which the menu never draws. Any later upload over a slot drops the bands
        // again (Patch -> OverlapsSlot), so the menu's own data is never drawn over.
        foreach (var b in a.Bands)
            gpu.WriteVramRect(TileX + b.U * 8, TileY + b.V * 16, 8, 16, b.Data);
        _ownerGpu = gpu;
        _ownerMem = Runtime.Mem;
        _bandsReady = true;
    }

    static bool OverlapsSlot(int x, int y, int w, int h)
    {
        var slot = _slot;
        if (slot == null)
        {
            var a = _asset;
            if (a == null) return false;
            slot = new bool[64, 16];
            foreach (var b in a.Bands) slot[b.U, b.V] = true;
            _slot = slot;
        }
        int u0 = Math.Max(0, (x - TileX) / 8), u1 = Math.Min(63, (x + w - 1 - TileX) / 8);
        int v0 = Math.Max(0, (y - TileY) / 16), v1 = Math.Min(15, (y + h - 1 - TileY) / 16);
        if (x + w <= TileX || y + h <= TileY) return false;
        for (int u = u0; u <= u1; u++)
            for (int v = v0; v <= v1; v++)
                if (slot[u, v]) return true;
        return false;
    }

    /// <summary>
    /// The game is about to draw the backdrop's top-left tile (mode-0 page; title.c places
    /// tiles from the draw offset minus 256, 120): draw the band tiles first, at the same
    /// ordering-table position, into the side bands only.
    /// </summary>
    public static bool IsFirstTile(int x, int y, int w, int h, int tpage, int originX, int originY)
        => w == 16 && h == 16 && x == originX && y == originY && (tpage & 0x19F) == 0x94;

    public static void DrawBands(IGpuBackend backend, int originX, int originY)
    {
        var a = _asset;
        if (a == null) return;
        foreach (var b in a.Bands)
        {
            var flags = new PrimFlags
            {
                Textured = true,
                RawTexture = true,
                // 8-bit page holding the slot: x = 256 + 128 * (u / 16), y = 256.
                TPage = (ushort)(((TileX + (b.U / 16) * 128) / 64) | 0x10 | 0x80),
                Clut = (ushort)(((ClutY + b.Clut) << 6) | (ClutX >> 4)),
                WideMode = WidePrimitiveMode.OverlaySides,
            };
            backend.DrawRect(new HleRect
            {
                X = originX + b.Col * 16, Y = originY + b.Row * 16, W = 16, H = 16,
                U = (short)((b.U % 16) * 16), V = (short)(b.V * 16), R = 128, G = 128, B = 128,
            }, flags);
        }
    }

    /// <summary>Retail tile (column, row) of a title upload slot (title.c TitleCalcUvs).</summary>
    static int TileIndex(int u, int v)
    {
        int col, row;
        if (u < 15) { col = u; row = v; }
        else if (u >= 16 && u <= 30) { col = u - 1; row = v; }
        else if (u >= 32 && u <= 46 && v <= 1) { col = 30 + v; row = u - 32; }
        else return -1;
        return row < 15 ? row * 32 + col : -1;
    }

    static uint Hash(ushort[] px, int n)
    {
        uint h = 2166136261u;
        for (int i = 0; i < n; i++) h = (h ^ px[i]) * 16777619u;
        return h;
    }

    /// <summary>Host/Assets/MainMenuWide.bin, made by artifacts/issue189/mainmenu/mkasset.py.</summary>
    static Asset? Load()
    {
        if (_asset != null || _loadFailed) return _asset;
        try
        {
            using var s = typeof(MainMenuWide).Assembly.GetManifestResourceStream("RecompOne.Runtime.Host.Assets.MainMenuWide.bin");
            if (s == null) { _loadFailed = true; return null; }
            using var r = new BinaryReader(s);
            if (new string(r.ReadChars(4)) != "MMW1") { _loadFailed = true; return null; }
            int tiles = r.ReadUInt16(), cluts = r.ReadUInt16(), bands = r.ReadUInt16();
            r.ReadUInt16(); // band margin (86 px at 16:9)
            if (tiles != 480 || cluts != 46) { _loadFailed = true; return null; }
            var a = new Asset();
            for (int i = 0; i < tiles; i++) { a.TileHash[i] = r.ReadUInt32(); a.Tile[i] = Words(r, TileWords); }
            for (int i = 0; i < cluts; i++) { a.ClutHash[i] = r.ReadUInt32(); a.Clut[i] = Words(r, 256); }
            for (int i = 0; i < bands; i++)
            {
                int col = r.ReadSByte(), row = r.ReadByte(), u = r.ReadByte(), v = r.ReadByte(), clut = r.ReadUInt16();
                a.Bands.Add(new Band(col, row, u, v, clut, Words(r, TileWords)));
            }
            _asset = a;
        }
        catch (Exception)
        {
            _loadFailed = true;
        }
        return _asset;
    }

    static ushort[] Words(BinaryReader r, int n)
    {
        var w = new ushort[n];
        for (int i = 0; i < n; i++) w[i] = r.ReadUInt16();
        return w;
    }
}
