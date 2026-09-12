using System.Diagnostics;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// Primitives allocated by one HUD GoolObjectTransform call. The shift is
    /// decided once per object from its authored trans.x (8.8 from the 4:3
    /// centre), so an icon and its digits keep their spacing, and a spinning
    /// mesh cannot wobble the whole group as its first primitive's bounds move.
    /// </summary>
    sealed class NativeWideHudRange
    {
        public uint Start;
        public uint End;
        /// <summary>FruiC object when this is a flying pickup icon, otherwise 0.</summary>
        public uint Pickup;
        public bool HasT;
        public float T;
        public bool HasShift;
        public int Shift;
    }

    /// <summary>
    /// Fraction of the half-width from the screen centre where HUD elements
    /// start following the wider edges, and where they follow them fully.
    /// Fruit (left) and lives (right) sit well beyond 0.35; the token strip
    /// stays centred; anything parked off-screen keeps moving outward.
    /// </summary>
    const float NativeWideHudCentreBand = 0.15f;
    const float NativeWideHudEdgeBand = 0.35f;

    /// <summary>
    /// Counter icons sit 130 px from the centre (DispC trans 0x8200 in 8.8),
    /// i.e. ±0.51 of the half-width. Pickup icons fly to these positions.
    /// </summary>
    const float NativeWideHudCounter = 0.5f;
    /// <summary>Authored 4:3 half-width in pixels. DispC trans is 8.8 from this centre.</summary>
    const float NativeWideHudHalf = 256f;
    /// <summary>World fruit trans is much larger than one 4:3 screen in 8.8.</summary>
    const int NativeWideHudScreen8_8 = 512 << 8;
    const float NativeWidePickupMinSpan = 0.15f;
    const double NativeWidePickupStaleSeconds = 0.5;

    /// <summary>FruiC. Fruit, life and token pickups; 2D while flying to their counter.</summary>
    const uint GoolTypeFrui = 3u;

    static readonly List<NativeWideHudRange> _nativeWideHudRanges = [];
    static readonly Dictionary<uint, (float Origin, long Last)> _nativeWidePickups = [];
    static uint _nativeWideHudStart;
    static uint _nativeWideHudPickup;
    static bool _nativeWideHudOpen;
    static bool _nativeWideHudHasT;
    static float _nativeWideHudT;

    /// <summary>
    /// GoolObjectTransform entry. Every object closes the previous HUD range
    /// because 8001DE78 only has a compile-time pre hook; a HUD object then
    /// opens its own range at the current prims tail.
    /// </summary>
    static void NoteNativeWideHudTransform(IMemory m, uint obj)
    {
        CloseNativeWideHudRange(m);
        if (!GpuHle.WideFovActive || !IsNativeWideHudObject(m, obj, out bool pickup)) return;
        if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
        _nativeWideHudStart = tail & 0x1FFFFCu;
        _nativeWideHudPickup = pickup ? obj : 0u;
        _nativeWideHudOpen = true;
        _nativeWideHudHasT = TryNativeWideHudT(m, obj, out _nativeWideHudT);
    }

    /// <summary>
    /// Refresh the 4:3 normalised X after a gated display lerp so a flying
    /// icon's shift follows the pose that Transform will actually emit.
    /// </summary>
    static void NoteNativeWideHudAnchor(IMemory m, uint obj)
    {
        if (!_nativeWideHudOpen) return;
        _nativeWideHudHasT = TryNativeWideHudT(m, obj, out _nativeWideHudT);
    }

    /// <summary>
    /// DispC stores pixels from the 4:3 centre in 8.8 (0x8200 = 130). World
    /// objects are far outside that range and keep the primitive-centre fallback.
    /// </summary>
    static bool TryNativeWideHudT(IMemory m, uint obj, out float t)
    {
        t = 0f;
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            int x = (int)m.ReadU32(obj + ObjTransOff);
            if (x <= -NativeWideHudScreen8_8 || x >= NativeWideHudScreen8_8) return false;
            t = x / 256f / NativeWideHudHalf;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// DispC counters (lives, fruit, tokens, boxes), plus the pickup icon that
    /// FruiC turns into a screen-space sprite while it flies to the counter.
    /// World FLAG_2D sprites (torch flames, mist) are not included.
    /// </summary>
    static bool IsNativeWideHudObject(IMemory m, uint obj, out bool pickup)
    {
        pickup = false;
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat)) return false;
            if (type == GoolTypeDisp || cat == GoolCategoryHud) return true;
            pickup = type == GoolTypeFrui && (m.ReadU32(obj + ObjStatusBOff) & Flag2D) != 0;
            return pickup;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the open HUD range at the current prims tail. Also called before
    /// the OT walk so the last transformed object cannot absorb GpuUpdate's
    /// own primitives (fade quads) into its range.
    /// </summary>
    public static void CloseNativeWideHudRange(IMemory m)
    {
        if (!_nativeWideHudOpen) return;
        _nativeWideHudOpen = false;
        try
        {
            if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
            uint end = tail & 0x1FFFFCu;
            // Ranges are consumed by the next DrawOTag. Skipped presents must
            // not let stale entries pile up while the game is not drawing.
            if (_nativeWideHudRanges.Count >= 64) _nativeWideHudRanges.Clear();
            if (end > _nativeWideHudStart)
                _nativeWideHudRanges.Add(new NativeWideHudRange
                {
                    Start = _nativeWideHudStart,
                    End = end,
                    Pickup = _nativeWideHudPickup,
                    HasT = _nativeWideHudHasT,
                    T = _nativeWideHudT,
                });
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>Index of the HUD object owning this OT primitive, or -1.</summary>
    public static int NativeWideHudRangeOf(uint physicalAddress)
    {
        for (int i = 0; i < _nativeWideHudRanges.Count; i++)
        {
            var range = _nativeWideHudRanges[i];
            if (physicalAddress >= range.Start && physicalAddress < range.End)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Horizontal shift for a HUD primitive whose object is <paramref name="rangeIndex"/>.
    /// Counters near the 4:3 edges follow the 16:9 edges by the full margin;
    /// centred elements stay put; the band in between ramps linearly so an
    /// element sliding in from off-screen does not jump.
    /// </summary>
    public static int NativeWideHudShift(int rangeIndex, float centerX, int areaLeft, int areaWidth, int margin)
    {
        if (margin <= 0 || areaWidth <= 0) return 0;
        float half = areaWidth * 0.5f;
        float t = (centerX - (areaLeft + half)) / half;
        if ((uint)rangeIndex >= (uint)_nativeWideHudRanges.Count)
            return NativeWideCounterShift(t, margin);
        var range = _nativeWideHudRanges[rangeIndex];
        if (range.HasT) t = range.T;
        if (!range.HasShift)
        {
            range.Shift = range.Pickup != 0
                ? NativeWidePickupShift(range.Pickup, t, margin)
                : NativeWideCounterShift(t, margin);
            range.HasShift = true;
        }
        return range.Shift;
    }

    static int NativeWideCounterShift(float t, int margin)
    {
        float magnitude = Math.Abs(t);
        float amount = magnitude <= NativeWideHudCentreBand ? 0f
            : magnitude >= NativeWideHudEdgeBand ? 1f
            : (magnitude - NativeWideHudCentreBand) / (NativeWideHudEdgeBand - NativeWideHudCentreBand);
        int shift = (int)MathF.Round(margin * amount);
        return t < 0 ? -shift : shift;
    }

    /// <summary>
    /// A pickup icon appears over the crate or fruit it came from, so it must
    /// not move at first, yet it has to land on the counter that did move.
    /// Blend by its progress from where it first appeared toward either
    /// counter position. FruiC slots are pooled: a slot unseen for a while is
    /// a new pickup with a new origin.
    /// </summary>
    static int NativeWidePickupShift(uint obj, float t, int margin)
    {
        long now = Stopwatch.GetTimestamp();
        long stale = (long)(NativeWidePickupStaleSeconds * Stopwatch.Frequency);
        if (!_nativeWidePickups.TryGetValue(obj, out var seen) || now - seen.Last > stale)
            seen = (t, now);
        _nativeWidePickups[obj] = (seen.Origin, now);
        if (_nativeWidePickups.Count > 64)
        {
            foreach (var key in _nativeWidePickups.Keys.ToArray())
                if (now - _nativeWidePickups[key].Last > stale)
                    _nativeWidePickups.Remove(key);
        }

        float best = 0f;
        for (int dest = -1; dest <= 1; dest += 2)
        {
            float span = dest * NativeWideHudCounter - seen.Origin;
            if (Math.Abs(span) < NativeWidePickupMinSpan)
                span = (span < 0 ? -1 : 1) * NativeWidePickupMinSpan;
            float progress = Math.Clamp((t - seen.Origin) / span, 0f, 1f);
            if (progress > best)
            {
                best = progress;
            }
        }
        // The flight may end at the centred token strip, or cross the centre
        // on its way to a counter. Its current position determines the anchor;
        // the candidate counter only estimates how far the flight has progressed.
        return (int)MathF.Round(NativeWideCounterShift(t, margin) * best);
    }

    static void ClearNativeWideHudRanges()
    {
        _nativeWideHudRanges.Clear();
        _nativeWideHudOpen = false;
        _nativeWideHudHasT = false;
        GpuHle.CurrentHudRange = -1;
    }

    static void ResetNativeWideHud()
    {
        ClearNativeWideHudRanges();
        _nativeWidePickups.Clear();
    }
}
