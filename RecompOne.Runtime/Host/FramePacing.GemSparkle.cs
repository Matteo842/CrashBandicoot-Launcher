using System.Diagnostics;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // #244 gem sparkle (ViewConfig.GemSparkle, off by default): Crash 2 crystal look.
    // Any SVTX drawn from a gem model (Ge10G..Ge60G, Gc10G) gets a soft additive halo
    // behind it and a few twinkling 4-point stars in front. Level end gems, the map
    // gem icons and the title get both; GemsC gem platforms only get the stars, and
    // only while Gem_Active (owned), so an unowned see-through platform stays as is.
    //
    // Everything is plain PS1 primitives linked into the frame's OT (DR_MODE additive
    // + POLY_G3 fading to black), so every backend draws it like game graphics.

    const int OtSlots = 2048;
    const uint PolyG3Words = 6;       // POLY_G3 after the tag
    const uint DrModeWords = 2;       // DR_MODE: E1 draw mode + E2 texture window
    // E1: drawing to the display area allowed, texture page 0, abr 1 (B + F).
    const uint DrawModeAdditive = 0xE1000420u;
    const uint TexWindowNone = 0xE2000000u;
    // Shapes, in fractions of the gem's screen box (largest side).
    const double GemGlowRadius = 0.95;
    const int GemGlowSegments = 16;
    const double GemGlowStrength = 0.80;
    const int GemStarCount = 3;
    const double GemStarPeriod = 1.1;  // seconds per twinkle
    const double GemStarLength = 0.32; // arm length
    const double GemStarMaxLength = 28; // screen units, so a near gem platform keeps small stars
    // Room left in the frame's prim buffer before we add anything (bytes).
    const uint GemPrimBudget = 0x800u;

    static readonly Stopwatch _gemClock = Stopwatch.StartNew();
    static uint _gemOt, _gemTail, _gemFirst, _gemObj;
    static (byte R, byte G, byte B) _gemColor;
    static bool _gemPending, _gemGlow;

    static readonly (uint Eid, byte R, byte G, byte B)[] GemModels =
    {
        (Eid("Gc10G"), 170, 190, 230), // clear
        (Eid("Ge10G"),  40,  90, 255), // blue
        (Eid("Ge20G"), 255,  40,  40), // red
        (Eid("Ge30G"), 255, 130,  20), // orange
        (Eid("Ge40G"), 200,  60, 255), // purple
        (Eid("Ge50G"),  40, 255,  70), // green
        (Eid("Ge60G"), 255, 225,  40), // yellow
    };

    /// <summary>Crash 1 entry name to EID (6 bits per char, low bit set).</summary>
    static uint Eid(string name)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_!";
        uint v = 0;
        foreach (char ch in name)
            v = (v << 6) | (uint)chars.IndexOf(ch);
        return (v << 1) | 1u;
    }

    /// <summary>GfxTransformSvtx(frame A0, ot A1, obj A2), before any prim is written.</summary>
    static void BeginGemSparkle(CpuContext c, IMemory m)
    {
        _gemPending = false;
        if (!ConfigManager.View.GemSparkle) return;
        try
        {
            uint frame = c.A0;
            if (!NativeWideGuestPointer(frame)) return;
            uint tgeo = m.ReadU32(frame + 4);
            uint eid = tgeo;
            if ((tgeo & 1u) == 0)
            {
                uint entry = EntryFromRef(m, tgeo);
                if (entry == 0) return;
                eid = m.ReadU32(entry + 4);
            }
            int hit = -1;
            for (int i = 0; i < GemModels.Length; i++)
                if (GemModels[i].Eid == eid) { hit = i; break; }
            if (hit < 0) return;

            uint obj = c.A2;
            bool glow = true;
            if (NativeWideGuestPointer(obj) && TryReadGoolClass(m, obj, out uint type, out _))
            {
                if (type == GoolTypeGems)
                {
                    if (m.ReadU32(obj + ObjStateOff) != StateGemActive) return;
                    glow = false;
                }
            }
            uint ot = c.A1;
            uint tail = PrimsTailAddr(m);
            if (!NativeWideGuestPointer(ot) || tail == 0) return;
            uint first = m.ReadU32(tail);
            if (!NativeWideGuestPointer(first)) return;
            _gemOt = ot;
            _gemTail = tail;
            _gemFirst = first;
            _gemObj = obj;
            _gemGlow = glow;
            _gemColor = (GemModels[hit].R, GemModels[hit].G, GemModels[hit].B);
            _gemPending = true;
        }
        catch
        {
            _gemPending = false;
        }
    }

    static void EndGemSparkle(IMemory m)
    {
        if (!_gemPending) return;
        _gemPending = false;
        try
        {
            uint end = m.ReadU32(_gemTail);
            if (end <= _gemFirst || end - _gemFirst > 0x10000) return;
            if (!TryGemScreenBox(m, _gemFirst, end, out int x0, out int y0, out int x1, out int y1))
                return;
            if (!TryGemOtSlots(m, _gemFirst, end, out int nearSlot, out int farSlot))
                return;
            if (!GemPrimRoom(m, end)) return;

            double cx = (x0 + x1) * 0.5, cy = (y0 + y1) * 0.5;
            double size = Math.Max(x1 - x0, y1 - y0);
            if (size < 2) return;
            double t = _gemClock.Elapsed.TotalSeconds;
            uint p = end;

            if (_gemGlow)
            {
                // Behind the gem: the farthest slot it used, drawn before its own prims
                // there (a slot's newest link draws first).
                int slot = farSlot;
                double pulse = 0.85 + 0.15 * Math.Sin(t * 2 * Math.PI * 0.6);
                var center = Scale(_gemColor, GemGlowStrength * pulse);
                double r = size * GemGlowRadius;
                double rx = r, ry = r * 0.92;
                for (int i = 0; i < GemGlowSegments; i++)
                {
                    double a0 = 2 * Math.PI * i / GemGlowSegments;
                    double a1 = 2 * Math.PI * (i + 1) / GemGlowSegments;
                    WriteGemG3(m, p,
                        cx, cy, center,
                        cx + Math.Cos(a0) * rx, cy + Math.Sin(a0) * ry, default,
                        cx + Math.Cos(a1) * rx, cy + Math.Sin(a1) * ry, default);
                    LinkGemPrim(m, slot, p, PolyG3Words);
                    p += (PolyG3Words + 1) * 4;
                }
                WriteGemDrMode(m, p);
                LinkGemPrim(m, slot, p, DrModeWords);
                p += (DrModeWords + 1) * 4;
            }

            // In front of the gem: one slot nearer than its nearest prim.
            int starSlot = Math.Min(nearSlot + 1, OtSlots - 1);
            bool any = false;
            for (int s = 0; s < GemStarCount; s++)
            {
                double phase = t / GemStarPeriod + (double)s / GemStarCount;
                long cycle = (long)Math.Floor(phase);
                double u = phase - cycle;
                double k = Math.Sin(Math.PI * u);
                if (k <= 0.05) continue;
                uint h = GemHash(_gemObj, (uint)s, (uint)cycle);
                // Spot on the gem's box, kept a little inside its outline.
                double sx = x0 + (x1 - x0) * (0.15 + 0.7 * ((h & 0xFF) / 255.0));
                double sy = y0 + (y1 - y0) * (0.10 + 0.8 * (((h >> 8) & 0xFF) / 255.0));
                double len = Math.Max(3.0, Math.Min(size * GemStarLength, GemStarMaxLength) * k);
                double thick = Math.Max(0.8, len * 0.12);
                var white = Blend(_gemColor, (255, 255, 255), 0.75);
                var col = Scale(white, k);
                for (int arm = 0; arm < 4; arm++)
                {
                    double a = Math.PI / 2 * arm;
                    double dx = Math.Cos(a), dy = Math.Sin(a);
                    // Long horizontal/vertical spikes, the vertical ones a bit longer.
                    double l = len * ((arm & 1) == 1 ? 1.0 : 0.75);
                    WriteGemG3(m, p,
                        sx - dy * thick, sy + dx * thick, col,
                        sx + dy * thick, sy - dx * thick, col,
                        sx + dx * l, sy + dy * l, default);
                    LinkGemPrim(m, starSlot, p, PolyG3Words);
                    p += (PolyG3Words + 1) * 4;
                }
                any = true;
            }
            if (any)
            {
                WriteGemDrMode(m, p);
                LinkGemPrim(m, starSlot, p, DrModeWords);
                p += (DrModeWords + 1) * 4;
            }
            m.WriteU32(_gemTail, p);
        }
        catch
        {
            // never break the frame for a cosmetic
        }
    }

    static bool TryGemScreenBox(IMemory m, uint first, uint end,
        out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = int.MaxValue;
        x1 = y1 = int.MinValue;
        int n = 0;
        for (uint p = first; p < end; )
        {
            uint words = m.ReadU32(p) >> 24;
            if (words == 0) return n > 0;
            uint code = m.ReadU32(p + 4) >> 24;
            if ((code & 0xE0) == 0x20)
            {
                bool quad = (code & 0x08) != 0;
                bool tex = (code & 0x04) != 0;
                bool gour = (code & 0x10) != 0;
                int verts = quad ? 4 : 3;
                // Vertex stride in words: xy, + uv when textured, + rgb when gouraud.
                uint stride = 1u + (tex ? 1u : 0u) + (gour ? 1u : 0u);
                uint off = 8;
                for (int v = 0; v < verts; v++)
                {
                    if (off / 4 > words) break;
                    uint xy = m.ReadU32(p + off);
                    int x = (short)(xy & 0xFFFF), y = (short)(xy >> 16);
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                    n++;
                    off += stride * 4;
                }
            }
            p += (words + 1) * 4;
        }
        return n > 0 && x1 - x0 < 1024 && y1 - y0 < 1024;
    }

    /// <summary>OT slots whose list head is one of the gem's prims: nearest and farthest.</summary>
    static bool TryGemOtSlots(IMemory m, uint first, uint end, out int nearSlot, out int farSlot)
    {
        nearSlot = -1;
        farSlot = OtSlots;
        uint lo = first & 0xFFFFFFu, hi = end & 0xFFFFFFu;
        for (int i = 0; i < OtSlots; i++)
        {
            uint head = m.ReadU32(_gemOt + (uint)i * 4) & 0xFFFFFFu;
            if (head < lo || head >= hi) continue;
            // Higher slot = drawn later (z_idx = far - zsum/32).
            if (i > nearSlot) nearSlot = i;
            if (i < farSlot) farSlot = i;
        }
        return nearSlot >= 0;
    }

    /// <summary>
    /// The frame's prim memory is 0x16000 bytes from prims_head (0x1E000 title,
    /// 0x1CC00 intro). Skip the sparkle unless the smallest size still has room.
    /// </summary>
    static bool GemPrimRoom(IMemory m, uint end)
    {
        uint ctx = m.ReadU32(GpuContextPtrAddr);
        if (!NativeWideGuestPointer(ctx)) return false;
        uint head = m.ReadU32(ctx + GpuContextTailOff - 4);
        if (!NativeWideGuestPointer(head) || end < head) return true;
        return end - head + GemPrimBudget < 0x16000u;
    }

    static void LinkGemPrim(IMemory m, int slot, uint prim, uint words)
    {
        uint ot = _gemOt + (uint)slot * 4;
        uint head = m.ReadU32(ot);
        m.WriteU32(prim, (words << 24) | (head & 0xFFFFFFu));
        m.WriteU32(ot, (head & 0xFF000000u) | (prim & 0xFFFFFFu));
    }

    static void WriteGemDrMode(IMemory m, uint p)
    {
        m.WriteU32(p + 4, DrawModeAdditive);
        m.WriteU32(p + 8, TexWindowNone);
    }

    static void WriteGemG3(IMemory m, uint p,
        double xa, double ya, (byte R, byte G, byte B) ca,
        double xb, double yb, (byte R, byte G, byte B) cb,
        double xc, double yc, (byte R, byte G, byte B) cc)
    {
        static uint Rgb((byte R, byte G, byte B) c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16);
        static uint Xy(double x, double y) =>
            ((uint)(ushort)(short)Math.Round(y) << 16) | (ushort)(short)Math.Round(x);
        // 0x32: POLY_G3, semi-transparent.
        m.WriteU32(p + 4, 0x32000000u | Rgb(ca));
        m.WriteU32(p + 8, Xy(xa, ya));
        m.WriteU32(p + 12, Rgb(cb));
        m.WriteU32(p + 16, Xy(xb, yb));
        m.WriteU32(p + 20, Rgb(cc));
        m.WriteU32(p + 24, Xy(xc, yc));
    }

    static (byte R, byte G, byte B) Scale((byte R, byte G, byte B) c, double k) =>
        ((byte)Math.Clamp(c.R * k, 0, 255), (byte)Math.Clamp(c.G * k, 0, 255), (byte)Math.Clamp(c.B * k, 0, 255));

    static (byte R, byte G, byte B) Blend((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double f) =>
        ((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    static uint GemHash(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        return h;
    }
}
