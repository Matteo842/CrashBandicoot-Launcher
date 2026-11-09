using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Easter egg (#189): the intro's "BANDICOOT 1.0 REJECT" screen shows this build's
/// version instead of 1.0. The text is drawn in texture page In4UT (8-bit, segment 0,
/// texels u 0-63, v 32-63; palette row 1 cyan, row 2 red). When the game uploads that
/// page to VRAM, the old number is erased and the version is written with the sign's
/// own 1, dot and 0 plus 2-9 drawn in the same style. The disc is not touched.
/// </summary>
static class IntroVersionSign
{
    // NSF page header: magic, type 1 (texture), EID In4UT.
    const ushort PageMagic = 0x1234, PageTexture = 1;
    const uint In4UT = 0x58B89C6Fu;
    // FNV-1a of the retail 64x32 tile (32 halfwords x 32 rows from page row 32).
    const uint RetailTileHash = 0xF3B38374u;
    const int TileRow = 32, TileWords = 32, TileRows = 32;
    // Glyph rows 14-26 of the tile; "BANDICOOT"'s T ends at x 9, the frame starts at x 47.
    const int GlyphTop = 14, GlyphLeft = 12, GlyphRight = 46, GlyphStart = 16, Gap = 2;
    // Palette entries of the text and its dark rim; 0 is the screen's black.
    const byte Text = 1;
    static readonly byte[] Erase = [1, 28, 53, 126, 129];

    static readonly Dictionary<char, string[]> Glyphs = new()
    {
        // 1, dot and 0: the sign's own glyphs.
        ['0'] = ["....#######..", "..####..####.", ".####....###.", "####......###", "####......###", "####....#####", "####..#######", "#############", "####.....####", "###......####", ".###....####.", ".####..#####.", "...#######..."],
        ['1'] = [".....##..", "....###..", "..####...", "######...", "##.###...", "...###...", "...###...", "...###...", "...###...", "...###...", "..######.", ".########", "####....."],
        ['.'] = ["....", "....", "....", "....", "....", "....", "....", "....", "....", "..#.", ".###", "###.", ".#.."],
        ['2'] = ["....#######..", "..####..####.", ".####....###.", ".###.....###.", ".........###.", "........###..", "......####...", "....####.....", "...###.......", "..###........", ".###.....###.", "#############", "############."],
        ['3'] = ["...########..", ".####...####.", ".###.....###.", ".........###.", "........###..", ".....#####...", ".......####..", ".........###.", "..........###", ".##.......###", ".###.....###.", "..####..####.", "....######..."],
        ['4'] = [".........###.", "........####.", ".......#####.", "......######.", ".....###.###.", "....###..###.", "...###...###.", "..###....###.", ".############", "#############", ".........###.", "........#####", ".......######"],
        ['5'] = ["..###########", "..##########.", "..###........", "..###........", ".#########...", ".####...####.", ".........###.", "..........###", "..........###", ".##.......###", ".###.....###.", "..####..####.", "....######..."],
        ['6'] = [".....######..", "...####..###.", "..###........", ".###.........", ".###.######..", ".#####...###.", ".####.....###", ".###......###", ".###......###", ".###......###", "..###....###.", "..####..####.", "....######..."],
        ['7'] = [".############", ".############", ".##.......###", "..........###", ".........###.", "........###..", ".......###...", "......###....", ".....###.....", ".....###.....", "....###......", "....###......", "...#####....."],
        ['8'] = ["...#######...", "..###...###..", ".###.....###.", ".###.....###.", "..###...###..", "...#######...", "..###...####.", ".###......###", ".###......###", ".###......###", ".###.....###.", "..####..####.", "....######..."],
        ['9'] = ["...#######...", ".####...####.", ".###.....###.", ".###......###", ".###......###", ".###.....####", "..####...####", "....######.##", "..........###", ".........###.", ".###.....###.", "..####..###..", "...######...."],
    };

    /// <summary>Called with every CPU→VRAM upload before it is committed.</summary>
    public static void Patch(int w, int h, ushort[] px, int count)
    {
        if (w < TileWords || h < TileRow + TileRows || count < w * h) return;
        if (px[0] != PageMagic || px[1] != PageTexture || (px[2] | (uint)px[3] << 16) != In4UT) return;
        if (TileHash(w, px) != RetailTileHash) return;
        string text = VersionText();
        int width = Gap * (text.Length - 1);
        foreach (char ch in text) width += Glyphs[ch][0].Length;
        if (text.Length == 0 || width > GlyphRight - GlyphLeft + 1) return;

        for (int y = GlyphTop - 2; y <= GlyphTop + 13; y++)
            for (int x = GlyphLeft - 1; x <= GlyphRight + 1; x++)
                if (Array.IndexOf(Erase, Get(w, px, x, y)) >= 0) Set(w, px, x, y, 0);
        int at = Math.Max(GlyphLeft, Math.Min(GlyphStart, GlyphRight + 1 - width));
        foreach (char ch in text)
        {
            var rows = Glyphs[ch];
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    if (rows[y][x] == '#') Set(w, px, at + x, GlyphTop + y, Text);
            at += rows[0].Length + Gap;
        }
    }

    /// <summary>"2.5" from the app version: its leading digits and dots (too wide = sign left as is).</summary>
    static string VersionText()
    {
        var s = new System.Text.StringBuilder();
        foreach (char ch in AppVersion.Version)
        {
            if (ch is not ((>= '0' and <= '9') or '.')) break;
            s.Append(ch);
        }
        return s.ToString().Trim('.');
    }

    static uint TileHash(int w, ushort[] px)
    {
        uint hash = 0x811C9DC5u;
        for (int r = 0; r < TileRows; r++)
            for (int c = 0; c < TileWords; c++)
            {
                ushort v = px[(TileRow + r) * w + c];
                hash = (hash ^ (byte)v) * 0x01000193u;
                hash = (hash ^ (byte)(v >> 8)) * 0x01000193u;
            }
        return hash;
    }

    // 8-bit texels: two per halfword, the even one in the low byte.
    static byte Get(int w, ushort[] px, int u, int v)
    {
        ushort word = px[(TileRow + v) * w + (u >> 1)];
        return (byte)((u & 1) == 0 ? word : word >> 8);
    }

    static void Set(int w, ushort[] px, int u, int v, byte value)
    {
        ref ushort word = ref px[(TileRow + v) * w + (u >> 1)];
        word = (u & 1) == 0 ? (ushort)((word & 0xFF00) | value) : (ushort)((word & 0x00FF) | value << 8);
    }
}
