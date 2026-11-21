using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #243 classic box counter (ViewConfig.ClassicBoxCounter), Crash 2/3 style: a
    /// spinning "?" crate and "broken/total" at the top centre, drawn by the game itself.
    /// Nothing new is spawned: once per frame, before GpuUpdate, the fruit counter's
    /// DispC <c>Variable_Display</c> object is borrowed for a few extra
    /// GoolObjectTransform calls (its own FONT_NUMBERS digits, then the BoxsC crate
    /// model placed in front of the camera) and put back exactly as it was.
    /// It slides in and out with the fruit/lives HUD (Triangle, pickups) and also
    /// shows for 1.5 s whenever a box breaks, like DispC <c>Display_Show</c>.
    /// </summary>
    const uint ClassicGhostBytes = 0x160u;
    const byte AnimTypeText = 4;
    // DispC Fruit_Display x = -200.0; Display_Show seeks y to DISPLAY_BOUND_LOWER_Y,
    // Display_Hidden parks it at DISPLAY_BOUND_Y (NTSC 88 / 176, PAL 102 / 190).
    const int FruitDisplayX = -200;
    // DispC Variable_Display: text at parent + (28, 16).
    const int HudTextDx = 28, HudTextDy = 16;
    // BoxsC #anim order: BOX_NORMAL is 0, BOX_PICKUP ("?" crate) 10; vertex anims are 8 bytes.
    const uint BoxAnimNormal = 0, BoxAnimPickup = 10;
    const uint VtxAnimSize = 8;
    // DispC Display_Show: seek 10.0 per 30 Hz frame, 1.5 s, then Display_Hide y += 4.0.
    const double ClassicShowSpeed = 300, ClassicHideSpeed = 120, ClassicHoldSeconds = 1.5;
    // Crash 1 crate models have no back or bottom face (the camera never sees them):
    // sway around the scene crates' own heading and tilt the top toward the camera.
    const double ClassicSwaySeconds = 3.0;
    const double ClassicSwayDegrees = 30;
    const double ClassicViewDegrees = 6;
    // rot.x pitch direction that brings the top toward a camera on the crate's -z side;
    // flip if the crate shows its bottom.
    const int ClassicTiltSign = -1;

    // Layout in DispC 2D units (screen pixels of the 512-wide frame, y up).
    // At the fruit text's own scale a FONT_NUMBERS digit advances ~42 and is ~32 tall,
    // hanging down from the text y.
    const double ClassicDigitAdvance = 42;
    const double ClassicDigitHeight = 32;
    const double ClassicMainScale = 0.6;
    const double ClassicSmallScale = 0.6;
    // The digits hang lower than the crate's middle by this much (2D units, y up).
    const double ClassicTextDrop = 4;
    // FONT_NUMBERS has no '/', so it is drawn as a glyph of our own on the native pixel
    // grid, like the digits' blocky texels: one flat quad per pixel row of the stroke,
    // a 1-pixel dark rim and the digits' orange-to-red gradient row by row. The quads
    // are digit glyphs the game drew (already linked at the HUD's OT slot), rewritten as
    // POLY_F4: a full-size '8' measures the digit box, then a string of tiny '8's
    // supplies the rest. In one OT slot the newest primitive draws first, so the rim
    // goes to the newest ones and the fill to the oldest.
    const double ClassicSlashAdvance = 22;
    const double ClassicSlashThick = 0.2, ClassicSlashLeft = 0.1, ClassicSlashRight = 0.85;
    const double ClassicSlashTop = 0.1, ClassicSlashBottom = 0.06;
    const int ClassicSlashMaxRows = 100;
    static readonly (byte R, byte G, byte B) ClassicSlashTopColor = (255, 176, 24);
    static readonly (byte R, byte G, byte B) ClassicSlashBottomColor = (226, 40, 0);
    static readonly (byte R, byte G, byte B) ClassicSlashOutlineColor = (24, 6, 0);
    // GPU context (c1 context.cur) and its prims tail; one POLY_GT4 is 0x34 bytes.
    const uint GpuContextPtrAddr = 0x8005840Cu;
    const uint GpuContextTailOff = 0x78u;
    const uint PolyGt4Size = 0x34u;
    const double ClassicCrateHalf = 13;
    // Crate model: on-screen height and the box's own size in camera units (world >> 8):
    // a crate is 1 m (GOOL 0x19000) with its origin on the bottom face.
    const double ClassicCratePixels = 26;
    const double ClassicCrateModelSize = 0x19000 >> 8;
    // gool_object.size: OT bias. RGteTransformSvtx: z_idx = (size + 0x800 - proj/2)
    // - z_sum/32, limited to 0..0x7FF, higher drawn later. Aim near the front end with
    // room for the crate's own depth either way.
    const uint ObjSizeOff = 0x138u;
    const int ClassicCrateOtSlot = 0x7C0;

    static readonly Stopwatch _classicClock = Stopwatch.StartNew();
    static double _classicLast;
    static double _classicY = double.NaN;
    static double _classicHoldUntil;
    static double _classicSpin;
    static int _classicBroken = -1;
    static uint _classicLevel = uint.MaxValue;
    static bool _classicGhost;
    static readonly uint[] _classicSave = new uint[ClassicGhostBytes / 4];

    /// <summary>PreGpuUpdate, after the HUD range is closed: every presented frame.</summary>
    static void DrawClassicBoxCounter(CpuContext c, IMemory m)
    {
        double now = _classicClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _classicLast, 0, 0.1);
        _classicLast = now;
        if (!ConfigManager.View.ClassicBoxCounter || !BoxCounter.TryReadRun(out var run))
        {
            _classicBroken = -1;
            _classicY = double.NaN;
            return;
        }
        try
        {
            uint lid = m.ReadU32(Catalog.LevelIdAddr);
            if (!TryFindFruitDisplay(m, out uint fruit, out uint text)) return;
            int fruitY = (int)m.ReadU32(fruit + ObjTransOff + 4) >> 8;
            int shownY = Math.Min(fruitY, 88);
            int hiddenY = Math.Max(fruitY, 176);
            // PAL parks the HUD 14 higher; use whatever the fruit counter rests at.
            if (fruitY > 176) { shownY = 102; hiddenY = 190; }
            else if (fruitY >= 100 && fruitY <= 102) shownY = 102;

            if (lid != _classicLevel || _classicBroken < 0 || double.IsNaN(_classicY))
            {
                _classicLevel = lid;
                _classicBroken = run.Broken;
                _classicY = hiddenY;
                _classicHoldUntil = 0;
            }
            else if (run.Broken != _classicBroken)
            {
                if (run.Broken > _classicBroken) _classicHoldUntil = now + ClassicHoldSeconds;
                _classicBroken = run.Broken;
            }
            bool paused = GamePaused(m);
            if (now < _classicHoldUntil && !paused)
                _classicY = Math.Max(shownY, _classicY - ClassicShowSpeed * dt);
            else if (!paused)
                _classicY = Math.Min(hiddenY, _classicY + ClassicHideSpeed * dt);
            if (!paused)
                _classicSpin = (_classicSpin + dt / ClassicSwaySeconds) % 1.0;

            // Ride the fruit counter down when it shows (Triangle, fruit, lives).
            double y = Math.Min(_classicY, fruitY);
            if (y >= hiddenY - 1) return;
            DrawClassicGhost(c, m, text, y, run);
        }
        catch
        {
            // level loading / objects recycled
        }
    }

    static void DrawClassicGhost(CpuContext c, IMemory m, uint obj, double y, BoxCounter.Reading run)
    {
        uint textAnim = m.ReadU32(obj + ObjAnimSeqOff);
        if (!NativeWideGuestPointer(textAnim) || m.ReadU8(textAnim) != AnimTypeText) return;
        uint boxAnim = FindClassicCrateAnim(m);

        for (uint i = 0; i < ClassicGhostBytes / 4; i++)
            _classicSave[i] = m.ReadU32(obj + i * 4);
        var saved = c.Snapshot();
        _classicGhost = true;
        try
        {
            string number = run.Broken.ToString();
            string total = run.Total.ToString();
            double mainAdv = ClassicDigitAdvance * ClassicMainScale;
            double smallAdv = ClassicDigitAdvance * ClassicSmallScale;
            double width = ClassicCrateHalf * 2 + (HudTextDx - ClassicCrateHalf)
                + number.Length * mainAdv + ClassicSlashAdvance + total.Length * smallAdv;
            double crateX = -width / 2 + ClassicCrateHalf;
            double textX = crateX + HudTextDx;
            double textY = y + HudTextDy;
            // Glyphs hang down from the text y: line the bottoms up.
            double bottom = textY - ClassicDigitHeight * ClassicMainScale;
            double crateY = bottom + ClassicDigitHeight * ClassicMainScale / 2;
            textY -= ClassicTextDrop;
            bottom -= ClassicTextDrop;
            int scaleX = (int)_classicSave[ObjScaleOff / 4];
            int scaleY = (int)_classicSave[(ObjScaleOff + 4) / 4];
            int scaleZ = (int)_classicSave[(ObjScaleOff + 8) / 4];
            uint status = _classicSave[ObjStatusBOff / 4];

            // Scratch below the current frame: fake TEXT anims (header + our string).
            uint sp = (c.SP - 0x200u) & ~7u;
            uint fake = sp + 0x40u;
            c.SP = sp;

            if (boxAnim != 0)
                DrawClassicCrate(c, m, obj, boxAnim, crateX, crateY, status);

            DrawClassicText(c, m, obj, textAnim, fake, number, textX, textY, 0,
                (int)(scaleX * ClassicMainScale), (int)(scaleY * ClassicMainScale), scaleZ, status);
            double x = textX + number.Length * mainAdv;
            double smallY = bottom + ClassicDigitHeight * ClassicSmallScale;
            int sx = (int)(scaleX * ClassicSmallScale), sy = (int)(scaleY * ClassicSmallScale);
            uint tail = PrimsTailAddr(m);
            if (tail != 0)
                DrawClassicSlash(c, m, obj, textAnim, fake, tail, x, smallY, sx, sy, scaleZ, status);
            x += ClassicSlashAdvance;
            DrawClassicText(c, m, obj, textAnim, fake, total, x, smallY, 0, sx, sy, scaleZ, status);
        }
        finally
        {
            _classicGhost = false;
            c.Restore(saved);
            for (uint i = 0; i < ClassicGhostBytes / 4; i++)
                m.WriteU32(obj + i * 4, _classicSave[i]);
        }
    }

    static uint PrimsTailAddr(IMemory m)
    {
        uint ctx = m.ReadU32(GpuContextPtrAddr);
        return NativeWideGuestPointer(ctx) ? ctx + GpuContextTailOff : 0;
    }

    static void DrawClassicSlash(CpuContext c, IMemory m, uint obj, uint textAnim, uint fake,
        uint tail, double x, double y, int sx, int sy, int sz, uint status)
    {
        uint first = m.ReadU32(tail);
        DrawClassicText(c, m, obj, textAnim, fake, "8", x, y, 0, sx, sy, sz, status);
        if (m.ReadU32(tail) == first + PolyGt4Size && TryClassicSlashRows(m, first, out var rows))
        {
            int need = rows.Count * 2 + 2;
            DrawClassicText(c, m, obj, textAnim, fake, new string('8', need - 1), x, y, 0,
                Math.Max(1, sx / 64), Math.Max(1, sy / 64), sz, status);
            if (m.ReadU32(tail) == first + (uint)need * PolyGt4Size)
            {
                WriteClassicSlash(m, first, rows);
                return;
            }
        }
        // Anything short of the full set: hide what was drawn rather than show an '8'.
        uint end = m.ReadU32(tail);
        for (uint p = first; p < end && end - first < 0x10000; p += PolyGt4Size)
            WriteClassicF4(m, p, 0, 0, 0, 0, default);
    }

    /// <summary>
    /// Slash rows on the native pixel grid from the measuring '8' (a POLY_GT4 from
    /// GfxTransformFontChar): for each pixel row, the stroke's left x, right x
    /// (exclusive) and y.
    /// </summary>
    static bool TryClassicSlashRows(IMemory m, uint glyph, out List<(int L, int R, int Y)> rows)
    {
        rows = new List<(int L, int R, int Y)>();
        if (((m.ReadU32(glyph + 4) >> 24) & 0xFC) != 0x3C) return false;
        int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
        for (uint i = 0; i < 4; i++)
        {
            uint xy = m.ReadU32(glyph + 8 + i * 12);
            int x = (short)(xy & 0xFFFF), y = (short)(xy >> 16);
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        double w = x1 - x0, h = y1 - y0;
        if (w < 3 || h < 3) return false;
        int thick = Math.Max(2, (int)Math.Round(w * ClassicSlashThick));
        double leftTop = x0 + w * ClassicSlashRight - thick, leftBottom = x0 + w * ClassicSlashLeft;
        int yt = (int)Math.Round(y0 + h * ClassicSlashTop), yb = (int)Math.Round(y1 - h * ClassicSlashBottom) - 1;
        if (yb <= yt || yb - yt + 1 > ClassicSlashMaxRows) return false;
        for (int y = yt; y <= yb; y++)
        {
            double f = (double)(y - yt) / (yb - yt);
            int l = (int)Math.Round(leftTop + (leftBottom - leftTop) * f);
            rows.Add((l, l + thick, y));
        }
        return true;
    }

    /// <summary>
    /// Rewrite the 2n+2 consecutive glyph primitives from <paramref name="first"/>:
    /// the n oldest become the gradient fill rows, the n+2 newest (drawn first) the
    /// 1-pixel rim: each row one pixel wider on both sides, plus a row above and below.
    /// </summary>
    static void WriteClassicSlash(IMemory m, uint first, List<(int L, int R, int Y)> rows)
    {
        int n = rows.Count;
        for (int i = 0; i < n; i++)
        {
            var (l, r, y) = rows[i];
            double f = n > 1 ? (double)i / (n - 1) : 0;
            var col = (
                (byte)Math.Round(ClassicSlashTopColor.R + (ClassicSlashBottomColor.R - ClassicSlashTopColor.R) * f),
                (byte)Math.Round(ClassicSlashTopColor.G + (ClassicSlashBottomColor.G - ClassicSlashTopColor.G) * f),
                (byte)Math.Round(ClassicSlashTopColor.B + (ClassicSlashBottomColor.B - ClassicSlashTopColor.B) * f));
            WriteClassicF4(m, first + (uint)i * PolyGt4Size, l, y, r, y + 1, col);
            WriteClassicF4(m, first + (uint)(n + i) * PolyGt4Size, l - 1, y, r + 1, y + 1, ClassicSlashOutlineColor);
        }
        var top = rows[0];
        var bottom = rows[n - 1];
        WriteClassicF4(m, first + (uint)(2 * n) * PolyGt4Size, top.L - 1, top.Y - 1, top.R + 1, top.Y, ClassicSlashOutlineColor);
        WriteClassicF4(m, first + (uint)(2 * n + 1) * PolyGt4Size, bottom.L - 1, bottom.Y + 1, bottom.R + 1, bottom.Y + 2, ClassicSlashOutlineColor);
    }

    /// <summary>A glyph POLY_GT4 rewritten in place as a flat POLY_F4 rectangle (OT link kept).</summary>
    static void WriteClassicF4(IMemory m, uint poly, int x0, int y0, int x1, int y1, (byte R, byte G, byte B) col)
    {
        static uint Xy(int x, int y) => ((uint)(ushort)(short)y << 16) | (ushort)(short)x;
        m.WriteU32(poly, (m.ReadU32(poly) & 0x00FFFFFFu) | (5u << 24));
        m.WriteU32(poly + 4, 0x28000000u | col.R | ((uint)col.G << 8) | ((uint)col.B << 16));
        m.WriteU32(poly + 8, Xy(x0, y0));
        m.WriteU32(poly + 12, Xy(x1, y0));
        m.WriteU32(poly + 16, Xy(x0, y1));
        m.WriteU32(poly + 20, Xy(x1, y1));
    }

    static void DrawClassicText(CpuContext c, IMemory m, uint obj, uint textAnim, uint fake,
        string text, double x, double y, int angleZ, int sx, int sy, int sz, uint status)
    {
        // gool_text: header, unk, glyphs_offset (the font stays DispC's FONT_NUMBERS),
        // then the strings; frame 0 picks the first one.
        for (uint i = 0; i < 12; i++)
            m.WriteU8(fake + i, m.ReadU8(textAnim + i));
        for (int i = 0; i < text.Length; i++)
            m.WriteU8(fake + 12 + (uint)i, (byte)text[i]);
        m.WriteU8(fake + 12 + (uint)text.Length, 0);
        m.WriteU8(fake + 13 + (uint)text.Length, 0);

        m.WriteU32(obj + ObjAnimSeqOff, fake);
        m.WriteU32(obj + ObjAnimFrameOff, 0);
        m.WriteU32(obj + ObjStatusBOff, status & ~0x400u);
        m.WriteU32(obj + ObjTransOff, (uint)(int)Math.Round(x * 256));
        m.WriteU32(obj + ObjTransOff + 4, (uint)(int)Math.Round(y * 256));
        m.WriteU32(obj + ObjRotOff, 0);
        m.WriteU32(obj + ObjRotOff + 4, 0);
        m.WriteU32(obj + ObjRotOff + 8, (uint)angleZ);
        m.WriteU32(obj + ObjScaleOff, (uint)sx);
        m.WriteU32(obj + ObjScaleOff + 4, (uint)sy);
        m.WriteU32(obj + ObjScaleOff + 8, (uint)sz);
        CallClassicTransform(c, m, obj);
    }

    /// <summary>
    /// The crate is a real 3D model, so it goes through the camera like any object:
    /// pick the view-space point that projects to (screenX, hudY) and turn it back
    /// into world space with the inverse of ms_rot.
    /// </summary>
    static void DrawClassicCrate(CpuContext c, IMemory m, uint obj, uint boxAnim,
        double screenX, double hudY, uint status)
    {
        int proj = (int)m.ReadU32(NativeWideProjectionAddr);
        if (proj is <= 0 or > 4096) return;
        double rz = proj * ClassicCrateModelSize / ClassicCratePixels;
        if (rz <= proj + 16) rz = proj + 16;
        // DispC 2D y is up; the camera's screen y is down. The origin is the bottom
        // face, half a crate below the centre we want on screen.
        double rx = screenX * rz / proj;
        double ry = -hudY * rz / proj + ClassicCrateModelSize / 2;

        Span<short> rot = stackalloc short[9];
        NativeWideReadMsRot(m, rot);
        double tx = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff);
        double ty = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 4);
        double tz = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 8);
        if (!InvertRot(rot, rx - tx, ry - ty, rz - tz, out double ux, out double uy, out double uz))
            return;
        int camx = (int)m.ReadU32(NativeWideCamTransPrevAddr);
        int camy = (int)m.ReadU32(NativeWideCamTransPrevAddr + 4);
        int camz = (int)m.ReadU32(NativeWideCamTransPrevAddr + 8);

        m.WriteU32(obj + ObjAnimSeqOff, boxAnim);
        m.WriteU32(obj + ObjAnimFrameOff, 0);
        // 0x40000: no near/screen cull; 0x400: no zone matrices (the point is not in
        // a zone); 0x100000: light it with the current zone's object colors.
        m.WriteU32(obj + ObjStatusBOff, (status & ~FlagInvisible) | 0x40000u | 0x400u | 0x100000u);
        m.WriteU32(obj + ObjTransOff, (uint)(camx + (int)Math.Round(ux * 256)));
        m.WriteU32(obj + ObjTransOff + 4, (uint)(camy + (int)Math.Round(uy * 256)));
        m.WriteU32(obj + ObjTransOff + 8, (uint)(camz + (int)Math.Round(uz * 256)));
        ClassicCrateAngles(ux, uy, uz, out int ax, out int ay, out int az);
        m.WriteU32(obj + ObjRotOff, (uint)ax);
        m.WriteU32(obj + ObjRotOff + 4, (uint)ay);
        m.WriteU32(obj + ObjRotOff + 8, (uint)az);
        m.WriteU32(obj + ObjScaleOff, 0x1000);
        m.WriteU32(obj + ObjScaleOff + 4, 0x1000);
        m.WriteU32(obj + ObjScaleOff + 8, 0x1000);
        // In front of all scenery, like a HUD icon (a crate is convex: its own faces
        // need no sorting once back faces are culled).
        int zSum32 = (int)(3 * rz / 32);
        m.WriteU32(obj + ObjSizeOff, (uint)(ClassicCrateOtSlot + zSum32 - 0x800 + proj / 2));
        CallClassicTransform(c, m, obj);
    }

    /// <summary>
    /// Crate rot: sway on rot.y (heading, 0 = how the level's crates face the camera),
    /// pitch on rot.x so the camera looks down on the top by <see cref="ClassicViewDegrees"/>.
    /// </summary>
    static void ClassicCrateAngles(double ux, double uy, double uz, out int ax, out int ay, out int az)
    {
        // Box to camera, world space (y up).
        double len = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        double dx = -ux / len, dy = -uy / len, dz = -uz / len;
        double hl = Math.Sqrt(dx * dx + dz * dz);
        double elevation = Math.Asin(Math.Clamp(dy, -1, 1));
        double tilt = ClassicViewDegrees * Math.PI / 180 - elevation;
        // Pitch only helps when the camera is in front or behind (z); none from the side.
        double toward = hl > 1e-6 ? -dz / hl : 0;
        double sway = Math.Sin(_classicSpin * 2 * Math.PI) * ClassicSwayDegrees * Math.PI / 180;
        static int A12(double rad) => (int)Math.Round(rad * 4096 / (2 * Math.PI)) & 0xFFF;
        ax = A12(ClassicTiltSign * toward * tilt);
        ay = A12(sway);
        az = 0;
    }

    static bool InvertRot(ReadOnlySpan<short> r, double x, double y, double z,
        out double ux, out double uy, out double uz)
    {
        // ms_rot is 4.12 and may carry the aspect scale, so a real inverse, not R^T.
        double a = r[0] / 4096.0, b = r[1] / 4096.0, cc = r[2] / 4096.0;
        double d = r[3] / 4096.0, e = r[4] / 4096.0, f = r[5] / 4096.0;
        double g = r[6] / 4096.0, h = r[7] / 4096.0, k = r[8] / 4096.0;
        double det = a * (e * k - f * h) - b * (d * k - f * g) + cc * (d * h - e * g);
        ux = uy = uz = 0;
        if (Math.Abs(det) < 1e-6) return false;
        ux = ((e * k - f * h) * x + (cc * h - b * k) * y + (b * f - cc * e) * z) / det;
        uy = ((f * g - d * k) * x + (a * k - cc * g) * y + (cc * d - a * f) * z) / det;
        uz = ((d * h - e * g) * x + (b * g - a * h) * y + (a * e - b * d) * z) / det;
        return true;
    }

    static void CallClassicTransform(CpuContext c, IMemory m, uint obj)
    {
        c.A0 = obj;
        Dispatcher.Call(c, m, GoolObjectTransformAddr);
    }

    /// <summary>BoxsC "?" crate model if it is in memory, else the plain crate.</summary>
    static uint FindClassicCrateAnim(IMemory m)
    {
        uint ldat = m.ReadU32(LdatPtrAddr);
        if (!NativeWideGuestPointer(ldat)) return 0;
        uint gool = NativeWideResidentEntry(m, m.ReadU32(ldat + LdatExecMapOff + GoolTypeBox * 4u), GoolEntryType);
        if (gool == 0) return 0;
        uint anims = EntryItem(m, gool, 5);
        if (anims == 0) return 0;
        foreach (uint idx in new[] { BoxAnimPickup, BoxAnimNormal })
        {
            uint anim = anims + idx * VtxAnimSize;
            if (m.ReadU8(anim) != AnimTypeVtx) continue;
            if (NativeWideResidentEntry(m, m.ReadU32(anim + 4), NativeWideSvtxType) != 0)
                return anim;
        }
        return 0;
    }

    /// <summary>The DispC fruit counter (sprite at x -200) and its Variable_Display text.</summary>
    static bool TryFindFruitDisplay(IMemory m, out uint fruit, out uint text)
    {
        fruit = text = 0;
        int left = ObjectPoolMax;
        for (int h = 0; h < HandleCount && left > 0; h++)
        {
            if (FindFruitDisplayIn(m, m.ReadU32(HandlesAddr + (uint)(h * 8) + HandleChildrenOff), ref left, out fruit))
                break;
        }
        if (fruit == 0) return false;
        for (uint child = m.ReadU32(fruit + ObjChildrenOff), n = 0;
             NativeWideGuestPointer(child) && n < 8; child = m.ReadU32(child + ObjSiblingOff), n++)
        {
            uint anim = m.ReadU32(child + ObjAnimSeqOff);
            if (NativeWideGuestPointer(anim) && m.ReadU8(anim) == AnimTypeText)
            {
                text = child;
                return true;
            }
        }
        return false;
    }

    static bool FindFruitDisplayIn(IMemory m, uint obj, ref int left, out uint found)
    {
        found = 0;
        int n = 0;
        while (NativeWideGuestPointer(obj) && n++ < ObjectPoolMax && left-- > 0)
        {
            if (m.ReadU32(obj) == HandleListType)
            {
                if (FindFruitDisplayIn(m, m.ReadU32(obj + HandleChildrenOff), ref left, out found)) return true;
            }
            else
            {
                if (TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypeDisp
                    && ((int)m.ReadU32(obj + ObjTransOff) >> 8) == FruitDisplayX)
                {
                    uint anim = m.ReadU32(obj + ObjAnimSeqOff);
                    if (NativeWideGuestPointer(anim) && m.ReadU8(anim) == AnimTypeSprite)
                    {
                        found = obj;
                        return true;
                    }
                }
                if (FindFruitDisplayIn(m, m.ReadU32(obj + ObjChildrenOff), ref left, out found)) return true;
            }
            obj = m.ReadU32(obj + ObjSiblingOff);
        }
        return false;
    }
}
