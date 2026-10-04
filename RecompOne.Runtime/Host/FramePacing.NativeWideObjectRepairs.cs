using System.Numerics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // Cortex's blimp (level 31, CVTX Bl1vV / TGEO Bl1vG) is not a whole airship
    // but a 96-polygon patch of its top that always faces the boss camera. Its
    // near rows end at the 4:3 edges, below the far rim's outer pieces, so the
    // 16:9 bands showed the sky through a wedge on each side (#90). Continue
    // those rows outward along the slope of the rim piece above them. Like the
    // WGEO repairs this is asset-specific and is not collision geometry.
    const uint NativeWideCortexLevel = 31;
    const int NativeWideBlimpVertices = 100;
    const int NativeWideBlimpPolygons = 96;
    // Near-row boundary from the far rim towards the camera, the polygon owning
    // each boundary edge (its material colours the continuation), and the rim's
    // outer piece as (inner vertex, outer vertex).
    static readonly int[] NativeWideBlimpLeftEdge = [27, 28, 29, 53, 61];
    static readonly int[] NativeWideBlimpLeftOwners = [22, 26, 92, 90];
    static readonly int[] NativeWideBlimpRightEdge = [18, 19, 3, 94, 95];
    static readonly int[] NativeWideBlimpRightOwners = [11, 13, 81, 79];
    const int NativeWideBlimpLeftRimInner = 26, NativeWideBlimpLeftRimOuter = 32;
    const int NativeWideBlimpRightRimInner = 0, NativeWideBlimpRightRimOuter = 6;
    // Starts slightly under the authored edge (about a pixel per model unit
    // there) so integer GTE coordinates cannot leave a crack along the join.
    const float NativeWideBlimpOverlap = 4f;
    // RGteTransformCvtx keeps its shade shift here.
    const uint NativeWideCvtxShiftAddr = 0x1F8000ECu;

    static uint _nativeWideMeshFrame;
    static uint _nativeWideMeshTail;
    static readonly List<NativeWideTriangle> _nativeWideObjectOpaque = new(32);
    static readonly List<NativeWideTriangle> _nativeWideObjectTransparent = new(4);

    /// <summary>Before GfxTransformSvtx/Cvtx: remember the drawn frame and the primitive tail.</summary>
    static void BeginNativeWideObjectRepair(CpuContext c, IMemory m)
    {
        _nativeWideMeshFrame = 0;
        if (!GpuHle.NativeWideRendererActive || _nativeWideView == null) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != NativeWideCortexLevel) return;
            if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
            _nativeWideMeshFrame = c.A0;
            _nativeWideMeshTail = tail;
        }
        catch
        {
            _nativeWideMeshFrame = 0;
        }
    }

    /// <summary>
    /// After GfxTransformSvtx/Cvtx. The GTE still holds the object's rotation
    /// and translation, so additions use exactly the transform of its polygons.
    /// Nothing is added when the object was culled and emitted no primitives.
    /// </summary>
    static void EndNativeWideObjectRepair(IMemory m)
    {
        uint frame = _nativeWideMeshFrame;
        _nativeWideMeshFrame = 0;
        if (frame == 0 || !GpuHle.NativeWideRendererActive || _nativeWideView is not { } view) return;
        try
        {
            if (!TryReadNativeWidePrimsTail(m, out uint tail) || tail == _nativeWideMeshTail) return;
            AddNativeWideBlimpRepairs(m, frame, view);
        }
        catch
        {
            // Entry paged out or overlay swapped; skip this frame's additions.
        }
    }

    static void AddNativeWideBlimpRepairs(IMemory m, uint frame, NativeWideView view)
    {
        if (!NativeWideGuestPointer(frame) || m.ReadU32(frame) != NativeWideBlimpVertices) return;
        uint tgeo = ResolveSvtxEntry(m, m.ReadU32(frame + 4u));
        if (tgeo == 0) return;
        uint header = EntryItem(m, tgeo, 0);
        uint polygons = EntryItem(m, tgeo, 1);
        if (!NativeWideGuestPointer(header) || !NativeWideGuestPointer(polygons)
            || m.ReadU32(header) != NativeWideBlimpPolygons)
            return;

        Span<short> rotation = stackalloc short[9];
        for (int i = 0; i < 4; i++)
        {
            uint pair = Gte.ReadControl(i);
            rotation[i * 2] = (short)pair;
            rotation[i * 2 + 1] = (short)(pair >> 16);
        }
        rotation[8] = (short)Gte.ReadControl(4);
        var translation = new Vector3((int)Gte.ReadControl(5), (int)Gte.ReadControl(6), (int)Gte.ReadControl(7));
        int shift = (int)Math.Min(m.ReadU32(NativeWideCvtxShiftAddr), 7u);

        _nativeWideObjectOpaque.Clear();
        _nativeWideObjectTransparent.Clear();
        if (!AddNativeWideBlimpSide(m, frame, header, polygons, NativeWideBlimpLeftEdge, NativeWideBlimpLeftOwners,
                NativeWideBlimpLeftRimInner, NativeWideBlimpLeftRimOuter, rotation, translation, shift, view)
            || !AddNativeWideBlimpSide(m, frame, header, polygons, NativeWideBlimpRightEdge, NativeWideBlimpRightOwners,
                NativeWideBlimpRightRimInner, NativeWideBlimpRightRimOuter, rotation, translation, shift, view))
            return;
        _nativeWidePending.AddRange(_nativeWideObjectOpaque);
        _nativeWidePending.AddRange(_nativeWideObjectTransparent);
    }

    static bool AddNativeWideBlimpSide(IMemory m, uint frame, uint header, uint polygons,
        int[] edge, int[] owners, int rimInner, int rimOuter, ReadOnlySpan<short> rotation,
        Vector3 translation, int shift, NativeWideView view)
    {
        // The vertex indices are confirmed by the polygons that own each edge.
        for (int i = 0; i < owners.Length; i++)
            if (!NativeWideCvtxPolygonHas(m, polygons, owners[i], edge[i], edge[i + 1]))
                return false;
        Vector3 inner = NativeWideCvtxVertex(m, frame, rimInner);
        Vector3 outer = NativeWideCvtxVertex(m, frame, rimOuter);
        Vector3 slope = outer - inner;
        if (MathF.Abs(slope.X) < 1f) return false;
        slope /= MathF.Abs(slope.X);

        Span<NativeWideClipVertex> triangle = stackalloc NativeWideClipVertex[3];
        for (int i = 0; i < owners.Length; i++)
        {
            uint texinfo = m.ReadU32(header + 0x14u + NativeWideCvtxTexinfo(m, polygons, owners[i]) * 4u);
            // The patch is untextured and opaque; anything else is not this asset.
            if ((texinfo & 0xE0000000u) != 0x60000000u) return false;
            Vector3 a = NativeWideCvtxVertex(m, frame, edge[i]);
            Vector3 b = NativeWideCvtxVertex(m, frame, edge[i + 1]);
            Vector3 aOut = a + slope * MathF.Abs(outer.X - a.X);
            Vector3 bOut = b + slope * MathF.Abs(outer.X - b.X);
            a -= slope * NativeWideBlimpOverlap;
            b -= slope * NativeWideBlimpOverlap;
            var ca = NativeWideCvtxClip(a, m, frame, edge[i], texinfo, shift, rotation, translation);
            var cb = NativeWideCvtxClip(b, m, frame, edge[i + 1], texinfo, shift, rotation, translation);
            var caOut = NativeWideCvtxClip(aOut, m, frame, rimOuter, texinfo, shift, rotation, translation);
            var cbOut = NativeWideCvtxClip(bOut, m, frame, rimOuter, texinfo, shift, rotation, translation);
            var flags = new PrimFlags { Gouraud = true, WideMode = WidePrimitiveMode.WorldExtensionSides };
            triangle[0] = ca; triangle[1] = caOut; triangle[2] = cb;
            AddNativeWideClippedTriangle(triangle, view.Projection, view.ScreenX, view.ScreenY, view.DrawX, view.DrawY,
                view.CenterX, view.CenterY, view.CoreHalf, view.WideHalf, view.HalfHeight, flags, true,
                _nativeWideObjectOpaque, _nativeWideObjectTransparent);
            triangle[0] = caOut; triangle[1] = cbOut; triangle[2] = cb;
            AddNativeWideClippedTriangle(triangle, view.Projection, view.ScreenX, view.ScreenY, view.DrawX, view.DrawY,
                view.CenterX, view.CenterY, view.CoreHalf, view.WideHalf, view.HalfHeight, flags, true,
                _nativeWideObjectOpaque, _nativeWideObjectTransparent);
        }
        return true;
    }

    // GTE input of RGteTransformCvtx: (vertex byte + frame offset - 128) * 4.
    static Vector3 NativeWideCvtxVertex(IMemory m, uint frame, int index)
    {
        uint vertex = frame + 0x38u + (uint)index * 6u;
        return new Vector3(
            (m.ReadU8(vertex) + (int)m.ReadU32(frame + 0x08u) - 128) * 4,
            (m.ReadU8(vertex + 1u) + (int)m.ReadU32(frame + 0x0Cu) - 128) * 4,
            (m.ReadU8(vertex + 2u) + (int)m.ReadU32(frame + 0x10u) - 128) * 4);
    }

    // TGEO polygon: vertex byte offsets (6 bytes each) and the texinfo offset / 4.
    static bool NativeWideCvtxPolygonHas(IMemory m, uint polygons, int polygon, int a, int b)
    {
        uint poly = polygons + (uint)polygon * 8u;
        uint w0 = m.ReadU32(poly), w1 = m.ReadU32(poly + 4u);
        int v0 = (int)(w0 & 0xFFFFu) / 6, v1 = (int)(w0 >> 16) / 6, v2 = (int)(w1 & 0xFFFFu) / 6;
        return (a == v0 || a == v1 || a == v2) && (b == v0 || b == v1 || b == v2);
    }

    static uint NativeWideCvtxTexinfo(IMemory m, uint polygons, int polygon) =>
        (m.ReadU32(polygons + (uint)polygon * 8u + 4u) >> 16) & 0x7FFFu;

    /// <summary>
    /// Camera-space vertex with the colour RGteTransformCvtx gives it: each
    /// channel is interpolated towards black (t &lt; 128) or white by the
    /// texinfo's t value with the GTE's DPCS, then shifted down by the shade shift.
    /// </summary>
    static NativeWideClipVertex NativeWideCvtxClip(Vector3 local, IMemory m, uint frame, int colorVertex,
        uint texinfo, int shift, ReadOnlySpan<short> rotation, Vector3 translation)
    {
        float x = translation.X + (rotation[0] * local.X + rotation[1] * local.Y + rotation[2] * local.Z) / 4096f;
        float y = translation.Y + (rotation[3] * local.X + rotation[4] * local.Y + rotation[5] * local.Z) / 4096f;
        float z = translation.Z + (rotation[6] * local.X + rotation[7] * local.Y + rotation[8] * local.Z) / 4096f;
        uint color = frame + 0x38u + (uint)colorVertex * 6u + 3u;
        Span<int> rgb = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            int t = (int)(texinfo >> (i * 8)) & 0xFF;
            int amount = (t < 128 ? 127 - t : t - 128) << 5;
            rgb[i] = NativeWideDepthCue(m.ReadU8(color + (uint)i), t < 128 ? 0 : 0xFF0, amount) >> shift;
        }
        return new NativeWideClipVertex(x, y, z, rgb[0], rgb[1], rgb[2], 0, 0);
    }
}
