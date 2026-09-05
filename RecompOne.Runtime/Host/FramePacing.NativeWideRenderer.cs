using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    const uint NativeWideProjectionAddr = 0x800578D0u;
    const uint NativeWideMatrixAddr = 0x800577E4u;
    const uint NativeWideUvMapAddr = 0x80051774u;
    readonly record struct NativeWidePrimRange(uint Start, uint End);
    static readonly List<NativeWidePrimRange> _nativeWideWorldRanges = [];
    static uint _nativeWideRangeStart;
    static bool _nativeWideRangeOpen;
    static int _nativeWideLogCount;
    static readonly List<NativeWideTriangle> _nativeWidePending = [];
    static readonly NativeWideClipVertex[][] _nativeWideCameraVertices = new NativeWideClipVertex[8][];
    static int _nativeWideDrawX, _nativeWideDrawY;
    enum NativeWideShader { Normal, Fog, Ripple, Tint, FogTint, Lamp }
    static NativeWideShader _nativeWideShader;
    static readonly int[] _nativeWideRipple = new int[16];
    static readonly int[] _nativeWideFarColor = new int[3];
    static readonly int[] _nativeWideLighting = new int[8];
    static int _nativeWideFogFar, _nativeWideFogShift;
    sealed class NativeWideWorld
    {
        public int PolyCount;
        public int VertexCount;
        public int TexinfoCount;
        public int TpageCount;
        public uint Header;
        public uint Polygons;
        public uint Vertices;
        public uint Texinfos;
        public uint Tpages;
        public int X;
        public int Y;
        public int Z;
        public int FogFar;
        public int FogShift;
        public NativeWideClipVertex[]? CameraVertices;
    }

    readonly record struct NativeWideTriangle(
        HleVertex A,
        HleVertex B,
        HleVertex C,
        PrimFlags Flags,
        float Depth);

    readonly record struct NativeWideClipVertex(
        double X,
        double Y,
        double Z,
        double R,
        double G,
        double B,
        double U,
        double V);

    static void BeginNativeWideWorldPass(IMemory m)
    {
        _nativeWideRangeOpen = false;
        _nativeWidePending.Clear();
        GpuHle.NativeWideRendererActive = false;
        if (!GpuHle.WideFovActive) return;
        uint zoneEntry = m.ReadU32(CamZoneAddr);
        uint zone = NativeWideGuestPointer(zoneEntry) ? EntryItem(m, zoneEntry, 0) : 0;
        uint level = m.ReadU32(Catalog.LevelIdAddr);
        uint shaderFlags = NativeWideGuestPointer(zone) ? m.ReadU32(zone + 0x2FCu) : 0;
        bool supported = GpuHle.WideFovActive && level is not (25 or 45 or 56 or 57)
            && NativeWideGuestPointer(zone);
        GpuHle.NativeWideRendererActive = supported;
        _nativeWideShader = (shaderFlags & 0x400u) != 0 ? NativeWideShader.Lamp
            : (shaderFlags & 0x210u) == 0x210u ? NativeWideShader.FogTint
            : (shaderFlags & 0x10u) != 0 ? NativeWideShader.Fog
            : (shaderFlags & 0x100u) != 0 ? NativeWideShader.Ripple
            : (shaderFlags & 0x200u) != 0 ? NativeWideShader.Tint : NativeWideShader.Normal;

        if (!supported) return;
        CaptureNativeWideWorldStart(m);
    }

    static void EndNativeWideWorldPass(IMemory m)
    {
        if (!_nativeWideRangeOpen) return;
        CaptureNativeWideWorldEnd(m);
        // Read the shader inputs after the original pass has prepared them.
        // In particular, water must use this frame's wave, not the previous one.
        try
        {
            if (!RenderNativeWideWorldSides(m))
            {
                _nativeWidePending.Clear();
                GpuHle.NativeWideRendererActive = false;
            }
        }
        catch (Exception ex)
        {
            _nativeWidePending.Clear();
            GpuHle.NativeWideRendererActive = false;
            if (_nativeWideLogCount < 8)
            {
                _nativeWideLogCount++;
                PaceLog($"native-wide renderer {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // World transforms run before GpuUpdate switches the double-buffered draw
    // environment. Submit only after PutDrawEnv, alongside the matching OT.
    public static void DrawNativeWideWorld()
    {
        var gpu = Runtime.Gpu;
        var backend = GpuHle.Backend;
        if (!GpuHle.WideFovActive || gpu == null || backend is not { Ready: true }) return;
        backend.SetDrawEnv(gpu.CurrentHleDrawEnv);
        backend.BeginWideDepth(clearSides: !GpuHle.DrawEnvClearsBackground);
        float dx = gpu.DrawOffsetX - _nativeWideDrawX;
        float dy = gpu.DrawOffsetY - _nativeWideDrawY;
        foreach (var triangle in _nativeWidePending)
        {
            var a = triangle.A; var b = triangle.B; var c = triangle.C;
            a.X += dx; a.Y += dy;
            b.X += dx; b.Y += dy;
            c.X += dx; c.Y += dy;
            backend.DrawTri(a, b, c, triangle.Flags);
        }
        _nativeWidePending.Clear();
    }

    static void CaptureNativeWideWorldStart(IMemory m)
    {
        _nativeWideRangeOpen = false;
        if (!GpuHle.NativeWideRendererActive) return;
        if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
        _nativeWideRangeStart = tail & 0x1FFFFCu;
        _nativeWideRangeOpen = true;
    }

    static void CaptureNativeWideWorldEnd(IMemory m)
    {
        if (!_nativeWideRangeOpen) return;
        _nativeWideRangeOpen = false;
        if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
        uint end = tail & 0x1FFFFCu;
        if (end > _nativeWideRangeStart)
            _nativeWideWorldRanges.Add(new NativeWidePrimRange(_nativeWideRangeStart, end));
    }

    static bool TryReadNativeWidePrimsTail(IMemory m, out uint tail)
    {
        tail = 0;
        uint context = m.ReadU32(GfxCurAddr);
        if (!NativeWideGuestPointer(context)) return false;
        tail = m.ReadU32(context + 0x78u);
        return NativeWideGuestPointer(tail);
    }

    public static bool IsNativeWideWorldPrimitive(uint physicalAddress)
    {
        foreach (var range in _nativeWideWorldRanges)
            if (physicalAddress >= range.Start && physicalAddress < range.End)
                return true;
        return false;
    }

    public static void FinishNativeWideDraw()
    {
        _nativeWideWorldRanges.Clear();
        _nativeWideRangeOpen = false;
        _nativeWidePending.Clear();
        GpuHle.NativeWideRendererActive = false;
    }

    static void ResetNativeWideRenderer()
    {
        FinishNativeWideDraw();
        _nativeWideLogCount = 0;
        _nativeWideBeachGround = null;
        _nativeWideBeachSky = null;
    }

    /// <summary>
    /// Native-wide pass using the original scene's world shader inputs. Enumerates the loaded
    /// WGEO meshes independently of the authored 4:3 visibility list. Submits
    /// each front-facing triangle that reaches
    /// either side band, with original vertex colours, animated UV regions,
    /// TPAGE/CLUT state and camera-space depth. The GL backend discards the 4:3
    /// centre for this pass and resolves overlap with its depth attachment.
    /// </summary>
    static bool RenderNativeWideWorldSides(IMemory m)
    {
        if (!GpuHle.NativeWideRendererActive) return false;
        var gpu = Runtime.Gpu;
        var backend = GpuHle.Backend;
        if (gpu == null || backend is not { Ready: true }) return false;

        uint zoneEntry = m.ReadU32(CamZoneAddr);
        if (!NativeWideGuestPointer(zoneEntry)) return false;
        uint zone = EntryItem(m, zoneEntry, 0);
        if (!NativeWideGuestPointer(zone)) return false;
        int worldCount = (int)m.ReadU32(zone);
        if (worldCount is <= 0 or > 8) return false;
        for (int i = 0; i < 3; i++) _nativeWideFarColor[i] = (int)Gte.ReadControl(21 + i);
        if (_nativeWideShader == NativeWideShader.Ripple)
            for (int i = 0; i < 16; i++) _nativeWideRipple[i] = (int)m.ReadU32(0x1F800048u + (uint)i * 4u);
        if (_nativeWideShader is NativeWideShader.Tint or NativeWideShader.FogTint or NativeWideShader.Lamp)
            for (int i = 0; i < 8; i++) _nativeWideLighting[i] = (int)m.ReadU32(0x1F800048u + (uint)i * 4u);
        if (_nativeWideShader == NativeWideShader.FogTint)
        {
            for (int i = 0; i < 3; i++) _nativeWideFarColor[i] = (int)m.ReadU32(0x1F8000E4u + (uint)i * 4u);
            _nativeWideFogShift = (int)m.ReadU32(zone + 0x2ECu) & 31;
            uint visibility = m.ReadU32(zone + 0x2E8u);
            _nativeWideFogFar = m.ReadU32(Catalog.LevelIdAddr) is 20 or 22
                ? (int)((visibility - (3200u * m.ReadU32(0x80061898u) + 409600u)) >> 8)
                : ((int)(visibility - 204800u) >> 8) - (_nativeWideFogShift == 1 ? 1200 : 0);
        }

        var worlds = new NativeWideWorld[worldCount];
        int totalPolygons = 0;
        for (int wi = 0; wi < worldCount; wi++)
        {
            uint world = zone + 4u + (uint)wi * 0x40u;
            uint header = m.ReadU32(world + 0x10u);
            uint polygons = m.ReadU32(world + 0x14u);
            uint vertices = m.ReadU32(world + 0x18u);
            uint texinfos = m.ReadU32(world + 0x1Cu);
            if (!NativeWideGuestPointer(header) || !NativeWideGuestPointer(polygons)
                || !NativeWideGuestPointer(vertices) || !NativeWideGuestPointer(texinfos))
                return false;
            int polyCount = (int)m.ReadU32(header + 0x0Cu);
            int vertexCount = (int)m.ReadU32(header + 0x10u);
            int texinfoCount = (int)m.ReadU32(header + 0x14u);
            int tpageCount = (int)m.ReadU32(header + 0x18u);
            if (polyCount is <= 0 or > 4096) return false;
            if (vertexCount is <= 0 or > 4096 || texinfoCount is <= 0 or > 4096
                || tpageCount is <= 0 or > 8)
                return false;
            totalPolygons += polyCount;
            worlds[wi] = new NativeWideWorld
            {
                PolyCount = polyCount,
                VertexCount = vertexCount,
                TexinfoCount = texinfoCount,
                TpageCount = tpageCount,
                Header = header,
                Polygons = polygons,
                Vertices = vertices,
                Texinfos = texinfos,
                Tpages = world + 0x20u,
                X = (int)m.ReadU32(world + 4u),
                Y = (int)m.ReadU32(world + 8u),
                Z = (int)m.ReadU32(world + 0x0Cu),
                FogFar = (ushort)m.ReadU32(0x1F800100u + (uint)wi * 0x40u),
                FogShift = (int)(m.ReadU32(0x1F800100u + (uint)wi * 0x40u) >> 16) & 31,
            };
        }

        var matrix = new short[9];
        for (int i = 0; i < matrix.Length; i++)
            matrix[i] = (short)m.ReadU16(NativeWideMatrixAddr + (uint)i * 2u);
        int projection = (int)m.ReadU32(NativeWideProjectionAddr);
        if (projection is <= 0 or > 4096) return false;
        int screenX = (int)Gte.ReadControl(24) >> 16;
        int screenY = (int)Gte.ReadControl(25) >> 16;
        for (int wi = 0; wi < worldCount; wi++)
        {
            var world = worlds[wi];
            var vertices = _nativeWideCameraVertices[wi];
            if (vertices == null || vertices.Length < world.VertexCount)
                _nativeWideCameraVertices[wi] = vertices = new NativeWideClipVertex[world.VertexCount];
            for (int vi = 0; vi < world.VertexCount; vi++)
                TryReadNativeWideCameraVertex(m, world, vi, matrix, 0, 0, out vertices[vi]);
            world.CameraVertices = vertices;
        }

        int displayWidth = 512;
        int displayHeight = 216;
        long newest = long.MinValue;
        for (int i = 0; i < GpuHle.RectCount; i++)
        {
            var rect = GpuHle.GetRect(i);
            if (!rect.Valid || rect.Stamp <= newest) continue;
            newest = rect.Stamp;
            displayWidth = rect.W;
            displayHeight = rect.H;
        }
        if (displayWidth is < 256 or > 1024) displayWidth = 512;
        if (displayHeight is < 160 or > 512) displayHeight = 216;
        float coreHalf = displayWidth * 0.5f;
        float wideHalf = coreHalf + GpuHle.WideMargin(displayWidth);
        float halfHeight = displayHeight * 0.5f + 16f;
        float viewCenterX = gpu.DrawOffsetX + screenX;
        float viewCenterY = gpu.DrawOffsetY + screenY;

        uint drawCount = m.ReadU32(DrawCountAddr);
        var opaque = new List<NativeWideTriangle>(1024);
        var transparent = new List<NativeWideTriangle>(128);
        Span<NativeWideClipVertex> repairVertices = stackalloc NativeWideClipVertex[3];
        int clippedPolygons = 0;
        int candidates = 0;
        for (int wi = 0; wi < worldCount; wi++)
        {
            var world = worlds[wi];
            foreach (var repair in NativeWideSceneRepairs(m, world))
            {
                PrimFlags repairFlags;
                if (repair.Polygon < 0)
                    repairFlags = new PrimFlags { Gouraud = true, WideMode = WidePrimitiveMode.BackdropSides };
                else
                {
                    if (!TryNativeWideMaterial(m, world, repair.Polygon, drawCount, out repairFlags,
                        out _, out _, out _, out _, out _, out _, out _)) continue;
                    repairFlags.WideMode = WidePrimitiveMode.ScenerySides;
                }
                repairVertices[0] = NativeWideRepairToCamera(repair.A, world, matrix);
                repairVertices[1] = NativeWideRepairToCamera(repair.B, world, matrix);
                repairVertices[2] = NativeWideRepairToCamera(repair.C, world, matrix);
                AddNativeWideClippedTriangle(repairVertices, projection, screenX, screenY,
                    gpu.DrawOffsetX, gpu.DrawOffsetY, viewCenterX, viewCenterY, coreHalf, wideHalf, halfHeight,
                    repairFlags, true, opaque, transparent);
            }
            for (int pi = 0; pi < world.PolyCount; pi++)
            {
                candidates++;
                if (!TryNativeWideMaterial(m, world, pi, drawCount, out PrimFlags flags, out bool noCull,
                        out short u0, out short v0, out short u1, out short v1, out short u2, out short v2))
                    continue;
                clippedPolygons += AddNativeWideClippedPolygon(
                    m, world, pi, matrix, projection, screenX, screenY,
                    gpu.DrawOffsetX, gpu.DrawOffsetY,
                    viewCenterX, viewCenterY, coreHalf, wideHalf, halfHeight,
                    flags, noCull, u0, v0, u1, v1, u2, v2,
                    opaque, transparent);
            }
        }

        transparent.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        _nativeWideDrawX = gpu.DrawOffsetX;
        _nativeWideDrawY = gpu.DrawOffsetY;
        _nativeWidePending.AddRange(opaque.Where(t => t.Flags.WideMode == WidePrimitiveMode.BackdropSides));
        _nativeWidePending.AddRange(transparent.Where(t => t.Flags.WideMode == WidePrimitiveMode.BackdropSides));
        _nativeWidePending.AddRange(opaque.Concat(transparent).Where(t => t.Flags.WideMode == WidePrimitiveMode.ScenerySides).OrderByDescending(t => t.Depth));
        _nativeWidePending.AddRange(opaque.Where(t => t.Flags.WideMode == WidePrimitiveMode.WorldSides));
        _nativeWidePending.AddRange(transparent.Where(t => t.Flags.WideMode == WidePrimitiveMode.WorldSides));

        if (_nativeWideLogCount < 8)
        {
            _nativeWideLogCount++;
            PaceLog($"native-wide renderer source={totalPolygons} candidates={candidates} "
                + $"clipped={clippedPolygons} "
                + $"side={opaque.Count}+{transparent.Count} winding={GpuHle.WideWorldFrontSign} "
                + $"samples={GpuHle.WideWorldPositiveSamples}/{GpuHle.WideWorldNegativeSamples}");
        }
        return true;
    }

    static int AddNativeWideClippedPolygon(
        IMemory m,
        NativeWideWorld world,
        int polyIndex,
        short[] matrix,
        int projection,
        int screenX,
        int screenY,
        int drawX,
        int drawY,
        float viewCenterX,
        float viewCenterY,
        float coreHalf,
        float wideHalf,
        float halfHeight,
        PrimFlags flags,
        bool noCull,
        short u0,
        short v0,
        short u1,
        short v1,
        short u2,
        short v2,
        List<NativeWideTriangle> opaque,
        List<NativeWideTriangle> transparent)
    {
        uint poly = world.Polygons + (uint)polyIndex * 8u;
        uint p0 = m.ReadU32(poly);
        uint p1 = m.ReadU32(poly + 4u);
        NativeWidePolygonVertices(p0, p1, out int indexA, out int indexB, out int indexC);
        Span<NativeWideClipVertex> input = stackalloc NativeWideClipVertex[4];
        if (!TryReadNativeWideCameraVertex(m, world, indexA, matrix, u0, v0, out input[0])
            || !TryReadNativeWideCameraVertex(m, world, indexB, matrix, u1, v1, out input[1])
            || !TryReadNativeWideCameraVertex(m, world, indexC, matrix, u2, v2, out input[2]))
            return 0;

        return AddNativeWideClippedTriangle(input[..3], projection, screenX, screenY, drawX, drawY,
            viewCenterX, viewCenterY, coreHalf, wideHalf, halfHeight, flags, noCull, opaque, transparent);
    }

    static int AddNativeWideClippedTriangle(
        ReadOnlySpan<NativeWideClipVertex> input, int projection, int screenX, int screenY,
        int drawX, int drawY, float viewCenterX, float viewCenterY, float coreHalf, float wideHalf,
        float halfHeight, PrimFlags flags, bool noCull,
        List<NativeWideTriangle> opaque, List<NativeWideTriangle> transparent)
    {
        double near = projection * 0.5 + 1.0;
        Span<NativeWideClipVertex> clipped = stackalloc NativeWideClipVertex[4];
        int clippedCount = 0;
        var previous = input[2];
        bool previousInside = previous.Z >= near;
        for (int i = 0; i < 3; i++)
        {
            var current = input[i];
            bool currentInside = current.Z >= near;
            if (currentInside != previousInside)
            {
                double t = (near - previous.Z) / (current.Z - previous.Z);
                clipped[clippedCount++] = NativeWideLerp(previous, current, t);
            }
            if (currentInside)
                clipped[clippedCount++] = current;
            previous = current;
            previousInside = currentInside;
        }
        if (clippedCount < 3) return 0;

        Span<HleVertex> projected = stackalloc HleVertex[4];
        for (int i = 0; i < clippedCount; i++)
            projected[i] = ProjectNativeWideClipVertex(clipped[i], projection, screenX, screenY, drawX, drawY);

        int added = 0;
        for (int i = 1; i + 1 < clippedCount; i++)
        {
            var a = projected[0];
            var b = projected[i];
            var c = projected[i + 1];
            double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < 0.01) continue;
            int winding = area > 0 ? 1 : -1;
            if (!noCull && winding != GpuHle.WideWorldFrontSign) continue;
            float minX = Math.Min(a.X, Math.Min(b.X, c.X));
            float maxX = Math.Max(a.X, Math.Max(b.X, c.X));
            float minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            float maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            if (maxX < viewCenterX - wideHalf || minX > viewCenterX + wideHalf
                || maxY < viewCenterY - halfHeight || minY > viewCenterY + halfHeight)
                continue;
            if (minX >= viewCenterX - coreHalf && maxX <= viewCenterX + coreHalf)
                continue;
            var triangle = new NativeWideTriangle(a, b, c, flags, (a.Z + b.Z + c.Z) / 3f);
            if (flags.SemiTrans) transparent.Add(triangle);
            else opaque.Add(triangle);
            added++;
        }
        return added;
    }

    static bool TryReadNativeWideCameraVertex(
        IMemory m,
        NativeWideWorld world,
        int vertexIndex,
        short[] matrix,
        short u,
        short v,
        out NativeWideClipVertex result)
    {
        if ((uint)vertexIndex >= (uint)world.VertexCount)
        {
            result = default;
            return false;
        }
        if (world.CameraVertices is { } cached)
        {
            result = cached[vertexIndex] with { U = u, V = v };
            return true;
        }
        uint vertex = world.Vertices + (uint)vertexIndex * 8u;
        uint v0 = m.ReadU32(vertex);
        uint v1 = m.ReadU32(vertex + 4u);
        int x = NativeWideSign13((int)((v1 >> 3) & 0x1FFFu)) * 8;
        int y = NativeWideSign13((int)((v1 >> 19) & 0x1FFFu)) * 8;
        int z = NativeWideSign13((int)(v0 >> 24)
            + (int)(((v1 >> 1) & 3u) << 8)
            + (int)(((v1 >> 16) & 7u) << 10)) * 8;
        if (_nativeWideShader == NativeWideShader.Ripple && (v1 & 1u) != 0)
        {
            int wave = (int)(((v1 >> 19) + ((v1 & 0xFFF8u) >> 3)) & 15u);
            y = (short)(y + _nativeWideRipple[wave]);
            x = (short)v1;
        }
        double rx = (((long)matrix[0] * x + (long)matrix[1] * y + (long)matrix[2] * z) >> 12) + world.X;
        double ry = (((long)matrix[3] * x + (long)matrix[4] * y + (long)matrix[5] * z) >> 12) + world.Y;
        double rz = (((long)matrix[6] * x + (long)matrix[7] * y + (long)matrix[8] * z) >> 12) + world.Z;
        int r = (byte)v0, g = (byte)(v0 >> 8), b = (byte)(v0 >> 16);
        int sz = Math.Clamp((int)rz, 0, 0xFFFF);
        if (_nativeWideShader is NativeWideShader.Tint or NativeWideShader.FogTint)
        {
            // Match the tint pass emitted by the current recompiler, which uses
            // the first lighting bank for all three vertices of each polygon.
            int amount = (short)_nativeWideLighting[3];
            r = NativeWideDepthCue(r, _nativeWideLighting[0], amount);
            g = NativeWideDepthCue(g, _nativeWideLighting[1], amount);
            b = NativeWideDepthCue(b, _nativeWideLighting[2], amount);
        }
        if (_nativeWideShader == NativeWideShader.Lamp)
        {
            int distance = Math.Abs((int)m.ReadU32(world.Header) + x - _nativeWideLighting[0])
                + Math.Abs((int)m.ReadU32(world.Header + 4) + y - _nativeWideLighting[1])
                + Math.Abs((int)m.ReadU32(world.Header + 8) + z - _nativeWideLighting[2]);
            int amount = Math.Clamp(distance + (int)((uint)distance >> (_nativeWideLighting[3] & 31))
                - (int)((uint)distance >> (_nativeWideLighting[4] & 31))
                + _nativeWideLighting[(v1 & 1u) == 0 ? 5 : 6], 0, 4095);
            r = NativeWideDepthCue(r, _nativeWideFarColor[0], amount);
            g = NativeWideDepthCue(g, _nativeWideFarColor[1], amount);
            b = NativeWideDepthCue(b, _nativeWideFarColor[2], amount);
        }
        int far = _nativeWideShader == NativeWideShader.FogTint ? _nativeWideFogFar : world.FogFar;
        int shift = _nativeWideShader == NativeWideShader.FogTint ? _nativeWideFogShift : world.FogShift;
        if (_nativeWideShader is NativeWideShader.Fog or NativeWideShader.FogTint && sz > far)
        {
            int amount = (short)((sz - far) << shift);
            r = NativeWideDepthCue(r, _nativeWideFarColor[0], amount);
            g = NativeWideDepthCue(g, _nativeWideFarColor[1], amount);
            b = NativeWideDepthCue(b, _nativeWideFarColor[2], amount);
        }
        result = new NativeWideClipVertex(rx, ry, rz, r, g, b, u, v);
        return true;
    }

    // DPCS, sf=12: preserve the GTE's signed IR0 and intermediate saturation.
    // This does not change the live GTE registers or its vertex/depth cache.
    static int NativeWideDepthCue(int color, int farColor, int amount)
    {
        int delta = Math.Clamp(farColor - (color << 4), -0x8000, 0x7FFF);
        return Math.Clamp((int)(((long)delta * amount + ((long)color << 16)) >> 16), 0, 255);
    }

    static NativeWideClipVertex NativeWideLerp(NativeWideClipVertex a, NativeWideClipVertex b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t,
        a.R + (b.R - a.R) * t,
        a.G + (b.G - a.G) * t,
        a.B + (b.B - a.B) * t,
        a.U + (b.U - a.U) * t,
        a.V + (b.V - a.V) * t);

    static HleVertex ProjectNativeWideClipVertex(
        NativeWideClipVertex vertex,
        int projection,
        int screenX,
        int screenY,
        int drawX,
        int drawY)
    {
        double irx = Math.Clamp(vertex.X, -0x8000, 0x7FFF);
        double iry = Math.Clamp(vertex.Y, -0x8000, 0x7FFF);
        float sz = (float)Math.Min(vertex.Z, 0xFFFF);
        return new HleVertex
        {
            X = drawX + screenX + (float)(projection * irx / sz),
            Y = drawY + screenY + (float)(projection * iry / sz),
            Z = sz,
            HasGteZ = true,
            R = (byte)Math.Clamp((int)Math.Round(vertex.R), 0, 255),
            G = (byte)Math.Clamp((int)Math.Round(vertex.G), 0, 255),
            B = (byte)Math.Clamp((int)Math.Round(vertex.B), 0, 255),
            U = (short)Math.Round(vertex.U),
            V = (short)Math.Round(vertex.V),
        };
    }

    static bool TryNativeWideMaterial(
        IMemory m,
        NativeWideWorld world,
        int polyIndex,
        uint drawCount,
        out PrimFlags flags,
        out bool noCull,
        out short u0,
        out short v0,
        out short u1,
        out short v1,
        out short u2,
        out short v2)
    {
        flags = default;
        noCull = false;
        u0 = v0 = u1 = v1 = u2 = v2 = 0;
        uint poly = world.Polygons + (uint)polyIndex * 8u;
        uint p0 = m.ReadU32(poly);
        uint p1 = m.ReadU32(poly + 4u);
        int tinf = (int)((p0 >> 8) & 0x0FFFu);
        int tpagIndex = (int)((p0 >> 5) & 7u);
        if ((uint)tinf >= (uint)world.TexinfoCount || (uint)tpagIndex >= (uint)world.TpageCount)
            return false;
        uint texinfo = world.Texinfos + (uint)tinf * 4u;
        uint colinfo = m.ReadU32(texinfo);
        byte material = (byte)(colinfo >> 24);
        bool textured = (material & 0x80) != 0;
        bool semi = (material & 0x60) != 0x60;
        noCull = (material & 0x10) != 0;
        flags = new PrimFlags
        {
            Textured = textured,
            SemiTrans = semi,
            RawTexture = false,
            Gouraud = true,
            WideMode = m.ReadU32(world.Header + 0x1Cu) != 0
                ? WidePrimitiveMode.BackdropSides : WidePrimitiveMode.WorldSides,
        };
        if (!textured) return true;

        int period = (int)((p1 >> 5) & 7u);
        int mask = (int)((p1 >> 1) & 0x0Fu);
        int phase = (int)(p0 & 0x1Fu);
        int anim = mask == 0 ? 0 : (phase + (int)(drawCount >> period)) & ((mask << 1) | 1);
        uint rgn = m.ReadU32(texinfo + 4u + (uint)anim * 4u);
        // The zone stores a resolved GPU page word, not a guest pointer.
        uint tpageInfo = m.ReadU32(world.Tpages + (uint)tpagIndex * 4u);
        int colorMode = (int)((rgn >> 20) & 3u);
        int segment = (int)((rgn >> 18) & 3u);
        int baseU = (int)((rgn >> 10) & 0xF8u) >> colorMode;
        int baseV = (int)((rgn & 0x1Fu) << 2) | (int)(tpageInfo & 0x80u);
        int regionIndex = (int)(rgn >> 22);
        uint uv = NativeWideUvMapAddr + (uint)regionIndex * 8u;
        uint uv01 = m.ReadU32(uv);
        ushort uv2 = m.ReadU16(uv + 4u);
        u0 = (byte)(baseU + (byte)uv01);
        v0 = (byte)(baseV + (byte)(uv01 >> 8));
        u1 = (byte)(baseU + (byte)(uv01 >> 16));
        v1 = (byte)(baseV + (byte)(uv01 >> 24));
        u2 = (byte)(baseU + (byte)uv2);
        v2 = (byte)(baseV + (byte)(uv2 >> 8));
        flags.Clut = (ushort)(((uint)material & 0x0Fu) | (rgn & 0x1FC0u) | ((tpageInfo >> 4) & 0xFFF0u));
        flags.TPage = (ushort)((colorMode << 7) | ((int)tpageInfo & 0x1C) | segment | (material & 0x60));
        return true;
    }

    // WGEO bitfields are declared in logical order but stored in reverse field
    // order on PS1. These masks mirror RGteTransformWorlds in Crash's renderer:
    // word 0 = phase/tpage/texinfo/C, word 1 = anim/period/B/A.
    static void NativeWidePolygonVertices(uint p0, uint p1, out int a, out int b, out int c)
    {
        a = (int)((p1 >> 20) & 0x0FFFu);
        b = (int)((p1 >> 8) & 0x0FFFu);
        c = (int)((p0 >> 20) & 0x0FFFu);
    }

    static int NativeWideSign13(int value) => (value & 0x1000) != 0 ? value - 0x2000 : value;
    static bool NativeWideGuestPointer(uint value) => (value & 0xFFE00000u) == 0x80000000u;
}


