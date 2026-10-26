// Crash 1 level data read straight from the player's disc: NSF pages and entries,
// WGEO meshes, zones with their camera paths, texture pages. Port of
// tools/widescreen/wslib (nsf.py, level.py, texture.py).
using System.Buffers.Binary;
using System.Numerics;
using RecompOne.Runtime.Cdrom;

namespace WideEditor;

static class Eid
{
    const string Alpha = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_!";

    public static string Str(uint e)
    {
        Span<char> s = stackalloc char[5];
        int[] shifts = [25, 19, 13, 7, 1];
        for (int i = 0; i < 5; i++) s[i] = Alpha[(int)((e >> shifts[i]) & 63)];
        return new string(s);
    }
}

static class Nsf
{
    static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    static (byte[] Page, int Used) Decompress(byte[] buf, int off)
    {
        int length = (int)U32(buf, off + 4), skip = (int)U32(buf, off + 8);
        int src = off + 12;
        var dst = new byte[0x10000];
        int n = 0;
        while (n < length)
        {
            int p = buf[src++];
            if ((p & 0x80) != 0)
            {
                int data = (p << 8) | buf[src++];
                int span = data & 7, seek = (data >> 3) & 0xFFF;
                span = span == 7 ? 0x40 : span + 3;
                for (int i = 0; i < span; i++, n++)
                    if (n < dst.Length) dst[n] = dst[n - seek];
            }
            else
            {
                for (int i = 0; i < p; i++, n++)
                    if (n < dst.Length) dst[n] = buf[src + i];
                src += p;
            }
        }
        src += skip;
        int rem = 0x10000 - length;
        for (int i = 0; i < rem && n < dst.Length && src + i < buf.Length; i++) dst[n++] = buf[src + i];
        src += rem;
        return (dst, src - off);
    }

    public static IEnumerable<byte[]> Pages(byte[] nsf)
    {
        int off = 0;
        while (off + 2 <= nsf.Length)
        {
            ushort magic = U16(nsf, off);
            if (magic == 0x1234)
            {
                yield return nsf.AsSpan(off, Math.Min(0x10000, nsf.Length - off)).ToArray();
                off += 0x10000;
            }
            else if (magic == 0x1235)
            {
                var (page, used) = Decompress(nsf, off);
                yield return page;
                off = (off + used + 0x7FF) & ~0x7FF;
            }
            else off += 0x800;
        }
    }

    public static IEnumerable<(uint Eid, int Type, byte[][] Items)> Entries(byte[] page)
    {
        if (U16(page, 2) != 0) yield break;
        int count = (int)U32(page, 8);
        for (int i = 0; i < count; i++)
        {
            int eo = (int)U32(page, 16 + 4 * i);
            if (U32(page, eo) != 0x100FFFF) continue;
            uint eid = U32(page, eo + 4);
            int type = (int)U32(page, eo + 8), icount = (int)U32(page, eo + 12);
            var items = new byte[icount][];
            for (int k = 0; k < icount; k++)
            {
                int a = (int)U32(page, eo + 16 + k * 4), b = (int)U32(page, eo + 16 + (k + 1) * 4);
                items[k] = page.AsSpan(eo + a, b - a).ToArray();
            }
            yield return (eid, type, items);
        }
    }
}

/// <summary>What a polygon is drawn with: texture page, colour mode, CLUT, blending.</summary>
readonly record struct Material(bool Textured, bool Semi, int Abr, int Layer, int Segment, int ColorMode,
    int ClutX, int ClutY, bool NoCull);

sealed class Mesh
{
    public required string Name;
    public int Index;
    public Vector3 Trans;
    public int TransX, TransY, TransZ;
    public int PolyCount, VertexCount;
    public bool Backdrop;
    public uint[] Texinfo = [];
    public string[] Tpages = [];
    public Vector3[] Pos = [];       // absolute
    public Vector3[] Rgb = [];
    public int[] Tri = [];           // 3 per polygon
    public int[] Tinf = [], Tpag = [];
    public byte[] Header = [], PolyBytes = [], VertBytes = [];

    public Vector3 Corner(int poly, int k) => Pos[Tri[poly * 3 + k]];
}

sealed class CameraPath
{
    public required string Zone;
    public int Index;
    public List<(Vector3 Pos, int Pitch, int Yaw)> Points = [];
}

sealed class Zone
{
    public required string Name;
    public List<string> Worlds = [];
    public List<string> Neighbours = [];
    public Vector3 Min, Max;
    public List<CameraPath> Paths = [];

    public bool Contains(Vector3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y
        && p.Z >= Min.Z && p.Z <= Max.Z;
}

sealed class LevelData
{
    const uint UvMap = 0x80051774;
    public int Id;
    public string Name = "";
    public List<Mesh> Meshes = [];
    public Dictionary<string, Mesh> MeshByName = [];
    public SortedDictionary<string, Zone> Zones = new(StringComparer.Ordinal);
    public Dictionary<string, ushort[]> Pages = [];   // 128 rows x 256 halfwords
    public Dictionary<string, int> PageLayer = [];
    public List<string> PageOrder = [];
    public byte[] Exe = [];
    public uint ExeTaddr;

    uint ExeU32(uint addr) => BinaryPrimitives.ReadUInt32LittleEndian(Exe.AsSpan((int)(0x800 + addr - ExeTaddr)));

    public static LevelData Load(CueFs disc, int id, string name)
    {
        var L = new LevelData { Id = id, Name = name };
        L.Exe = disc.ReadFileWithoutOverlay("SCUS_949.00");
        L.ExeTaddr = BinaryPrimitives.ReadUInt32LittleEndian(L.Exe.AsSpan(0x18));
        byte[] nsf = disc.ReadFileWithoutOverlay($"S00000{id:X2}.NSF");
        var wgeo = new SortedDictionary<string, byte[][]>(StringComparer.Ordinal);
        var zdat = new List<(string, byte[][])>();
        foreach (var page in Nsf.Pages(nsf))
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(2));
            if (type == 1)
            {
                string eid = Eid.Str(BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(4)));
                var px = new ushort[128 * 256];
                Buffer.BlockCopy(page, 0, px, 0, Math.Min(page.Length, px.Length * 2));
                L.Pages[eid] = px;
                continue;
            }
            foreach (var (e, t, items) in Nsf.Entries(page))
            {
                if (t == 3) wgeo[Eid.Str(e)] = items;
                else if (t == 7) zdat.Add((Eid.Str(e), items));
            }
        }
        foreach (var p in L.Pages.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            L.PageLayer[p] = L.PageOrder.Count;
            L.PageOrder.Add(p);
        }
        foreach (var (n, items) in wgeo)
        {
            var mesh = ParseMesh(n, items);
            mesh.Index = L.Meshes.Count;
            L.Meshes.Add(mesh);
            L.MeshByName[n] = mesh;
        }
        foreach (var (n, items) in zdat)
            L.Zones[n] = ParseZone(n, items);
        return L;
    }

    static int S13(int v) => (v & 0x1000) != 0 ? v - 0x2000 : v;

    static Mesh ParseMesh(string name, byte[][] items)
    {
        byte[] h = items[0];
        uint R(int o) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o));
        var m = new Mesh { Name = name, Header = h, PolyBytes = items[1], VertBytes = items[2] };
        m.TransX = (int)R(0); m.TransY = (int)R(4); m.TransZ = (int)R(8);
        m.Trans = new(m.TransX, m.TransY, m.TransZ);
        m.PolyCount = (int)R(12);
        m.VertexCount = (int)R(16);
        int tic = (int)R(20), tpc = (int)R(24);
        m.Backdrop = R(28) != 0;
        m.Tpages = [.. Enumerable.Range(0, tpc).Select(i => Eid.Str(R(0x20 + i * 4)))];
        m.Texinfo = [.. Enumerable.Range(0, tic).Select(i => R(0x40 + i * 4))];
        m.Pos = new Vector3[m.VertexCount];
        m.Rgb = new Vector3[m.VertexCount];
        for (int i = 0; i < m.VertexCount; i++)
        {
            uint v0 = BinaryPrimitives.ReadUInt32LittleEndian(items[2].AsSpan(i * 8));
            uint v1 = BinaryPrimitives.ReadUInt32LittleEndian(items[2].AsSpan(i * 8 + 4));
            int x = S13((int)((v1 >> 3) & 0x1FFF)) * 8, y = S13((int)((v1 >> 19) & 0x1FFF)) * 8;
            int z = S13((int)((v0 >> 24) + (((v1 >> 1) & 3) << 8) + (((v1 >> 16) & 7) << 10))) * 8;
            m.Pos[i] = new Vector3(x, y, z) + m.Trans;
            m.Rgb[i] = new(v0 & 255, (v0 >> 8) & 255, (v0 >> 16) & 255);
        }
        m.Tri = new int[m.PolyCount * 3];
        m.Tinf = new int[m.PolyCount];
        m.Tpag = new int[m.PolyCount];
        for (int p = 0; p < m.PolyCount; p++)
        {
            uint p0 = BinaryPrimitives.ReadUInt32LittleEndian(items[1].AsSpan(p * 8));
            uint p1 = BinaryPrimitives.ReadUInt32LittleEndian(items[1].AsSpan(p * 8 + 4));
            m.Tri[p * 3] = (int)((p1 >> 20) & 0xFFF);
            m.Tri[p * 3 + 1] = (int)((p1 >> 8) & 0xFFF);
            m.Tri[p * 3 + 2] = (int)((p0 >> 20) & 0xFFF);
            m.Tinf[p] = (int)((p0 >> 8) & 0xFFF);
            m.Tpag[p] = (int)((p0 >> 5) & 7);
            for (int k = 0; k < 3; k++)
                if (m.Tri[p * 3 + k] >= m.VertexCount) m.Tri[p * 3 + k] = 0;
        }
        return m;
    }

    static Zone ParseZone(string name, byte[][] items)
    {
        byte[] h = items[0];
        uint R(int o) => o + 4 <= h.Length ? BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o)) : 0;
        var z = new Zone { Name = name };
        int wc = (int)Math.Min(R(0), 8u);
        for (int i = 0; i < wc; i++) z.Worlds.Add(Eid.Str(R(4 + i * 0x40)));
        int pidx = (int)R(0x204), pcount = (int)R(0x208), ncount = (int)Math.Min(R(0x210), 8u);
        for (int i = 0; i < ncount; i++) z.Neighbours.Add(Eid.Str(R(0x214 + i * 4)));
        var rect = items[1];
        int Ri(int o) => BinaryPrimitives.ReadInt32LittleEndian(rect.AsSpan(o));
        z.Min = new(Ri(0), Ri(4), Ri(8));
        z.Max = z.Min + new Vector3(Ri(12), Ri(16), Ri(20));
        for (int p = 0; p < pcount && pidx + p < items.Length; p++)
        {
            var item = items[pidx + p];
            var path = new CameraPath { Zone = name, Index = p };
            int n = BinaryPrimitives.ReadUInt16LittleEndian(item.AsSpan(30));
            for (int k = 0; k < n && 50 + k * 12 + 12 <= item.Length; k++)
            {
                short S(int o) => BinaryPrimitives.ReadInt16LittleEndian(item.AsSpan(50 + k * 12 + o));
                path.Points.Add((z.Min + new Vector3(S(0), S(2), S(4)), S(6), S(8)));
            }
            z.Paths.Add(path);
        }
        return z;
    }

    public Material MaterialOf(Mesh m, int poly)
    {
        int t = m.Tinf[poly];
        if (t >= m.Texinfo.Length) return default;
        uint col = m.Texinfo[t];
        int mat = (int)(col >> 24);
        bool textured = (mat & 0x80) != 0 && t + 1 < m.Texinfo.Length;
        bool semi = (mat & 0x60) != 0x60;
        int abr = (mat >> 5) & 3;
        if (!textured) return new(false, semi, abr, -1, 0, 0, 0, 0, (mat & 0x10) != 0);
        uint rgn = m.Texinfo[t + 1];
        int layer = m.Tpag[poly] < m.Tpages.Length && PageLayer.TryGetValue(m.Tpages[m.Tpag[poly]], out int l) ? l : -1;
        return new(true, semi, abr, layer, (int)((rgn >> 18) & 3), (int)((rgn >> 20) & 3),
            mat & 15, (int)((rgn >> 6) & 0x7F), (mat & 0x10) != 0);
    }

    /// <summary>UVs of a polygon as TryNativeWideMaterial computes them (animation frame 0, page-local V).</summary>
    public Vector2[] UvsOf(Mesh m, int poly)
    {
        int t = m.Tinf[poly];
        if (t + 1 >= m.Texinfo.Length || (m.Texinfo[t] >> 24 & 0x80) == 0) return [Vector2.Zero, Vector2.Zero, Vector2.Zero];
        uint rgn = m.Texinfo[t + 1];
        int cm = (int)((rgn >> 20) & 3);
        int bu = (int)((rgn >> 10) & 0xF8) >> cm, bv = (int)(rgn & 0x1F) << 2;
        uint uv01 = ExeU32(UvMap + (rgn >> 22) * 8), uv2 = ExeU32(UvMap + (rgn >> 22) * 8 + 4) & 0xFFFF;
        return
        [
            new((bu + (int)(uv01 & 255)) & 255, (bv + (int)((uv01 >> 8) & 255)) & 127),
            new((bu + (int)((uv01 >> 16) & 255)) & 255, (bv + (int)(uv01 >> 24)) & 127),
            new((bu + (int)(uv2 & 255)) & 255, (bv + (int)(uv2 >> 8)) & 127),
        ];
    }

    /// <summary>15-bit texel (0 = transparent) of a material at page-local u, v.</summary>
    public ushort Texel(Material mat, int u, int v)
    {
        if (!mat.Textured || mat.Layer < 0) return 0x7FFF;
        var page = Pages[PageOrder[mat.Layer]];
        u &= 255; v &= 127;
        int seg = mat.Segment * 64;
        switch (mat.ColorMode)
        {
            case 0:
            {
                int idx = (page[v * 256 + ((seg + u / 4) & 255)] >> ((u & 3) * 4)) & 15;
                return page[mat.ClutY * 256 + ((mat.ClutX * 16 + idx) & 255)];
            }
            case 1:
            {
                int idx = (page[v * 256 + ((seg + u / 2) & 255)] >> ((u & 1) * 8)) & 255;
                return page[mat.ClutY * 256 + ((mat.ClutX * 16 + idx) & 255)];
            }
            default:
                return page[v * 256 + ((seg + u) & 255)];
        }
    }

    public Zone? ZoneAt(Vector3 p)
    {
        Zone? best = null;
        float bestVolume = float.MaxValue;
        foreach (var z in Zones.Values)
        {
            if (!z.Contains(p)) continue;
            var s = z.Max - z.Min;
            float volume = s.X * s.Y * s.Z;
            if (volume < bestVolume) { best = z; bestVolume = volume; }
        }
        return best;
    }

    /// <summary>Meshes the native-wide renderer draws for a zone: its own (backdrops included)
    /// and its neighbours' scenery (no backdrops).</summary>
    public List<Mesh> ZoneMeshes(Zone z)
    {
        var names = new List<string>(z.Worlds);
        foreach (var n in z.Neighbours)
        {
            if (!Zones.TryGetValue(n, out var nz)) continue;
            foreach (var w in nz.Worlds)
                if (!names.Contains(w) && MeshByName.TryGetValue(w, out var wm) && !wm.Backdrop) names.Add(w);
        }
        return [.. names.Where(MeshByName.ContainsKey).Select(n => MeshByName[n])];
    }
}
