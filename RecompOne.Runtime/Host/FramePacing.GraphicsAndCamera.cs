using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// GoolObjectTransform already NSLookup'd the svtx. A0 is the drawn
    /// frame, A2 the object. Patch toward the next item with wall t —
    /// PreTransform is too early (seq+4 still an EID).
    /// </summary>
    public static bool PreGfxTransformMesh(CpuContext c, IMemory m)
    {
        RestoreSvtx(m);
        if (!IsActive(m)) return true;
        if (GamePaused(m)) return true;
        // Full original 33 ms step already landed on the GOOL pose.
        if (_exactTicks >= RefTicks - 0.01) return true;
        uint drawn = c.A0;
        uint obj = c.A2;
        if ((obj & 0xFF000000u) != 0x80000000u)
            obj = _obj;
        if ((obj & 0xFF000000u) != 0x80000000u) return true;
        bool crash = false;
        bool box = false;
        try
        {
            crash = obj == m.ReadU32(CrashPtrAddr);
            box = TryReadGoolClass(m, obj, out uint typ, out _) && typ == GoolTypeBox;
        }
        catch
        {
            // keep going; mesh resolve decides
        }
        try
        {
            PatchDrawnLookAhead(c, m, obj, drawn, crash, box);
        }
        catch
        {
            RestoreSvtx(m);
        }
        return true;
    }

    public static void PostGfxTransformMesh(CpuContext c, IMemory m) => RestoreSvtx(m);

    /// <summary>
    /// CoreLoop: ShaderParams (writes ripple_speed) → GfxUpdateMatrices →
    /// TransformWorlds* → objects → GpuUpdate (++ draw_count). Advance water
    /// here so worlds see wall time, not leftover object ticks.
    /// </summary>
    public static bool PreGfxUpdateMatrices(CpuContext c, IMemory m)
    {
        try
        {
            if ((m.ReadU32(DisplayFlagsAddr) & FlagSpinDeath) == 0)
                ClearDeathCam();
        }
        catch { /* overlay swap */ }
        AdvanceWater(m);
        return true;
    }

    static void ResetWaterClock()
    {
        _waterArmed = false;
        _waterDoneThisLoop = false;
        _worldDrawFrac = 0;
        _rippleFrac = 0;
        _ripplePatched = false;
        _waterLog = 0;
    }

    static void CommitWaterDrawCount(IMemory m)
    {
        if (!_waterArmed || !IsActive(m)) return;
        try { m.WriteU32(DrawCountAddr, _worldDraw); }
        catch { /* overlay swap */ }
    }

    /// <summary>
    /// One wall sample per game loop. 30 original frames per wall second, with
    /// a remainder — 60/120/280/350 must match. Never uses <c>_exactTicks</c>.
    /// </summary>
    static void AdvanceWater(IMemory m)
    {
        if (!IsActive(m))
        {
            _waterArmed = false;
            return;
        }

        if (_waterDoneThisLoop)
        {
            CommitWaterDrawCount(m);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (!_waterArmed)
        {
            try { _worldDraw = m.ReadU32(DrawCountAddr); }
            catch { return; }
            _worldDrawFrac = 0;
            _rippleFrac = 0;
            _waterTs = now;
            _waterArmed = true;
            _waterDoneThisLoop = true;
            CommitWaterDrawCount(m);
            ZeroRippleSpeed(m);
            return;
        }

        double sec = GamePaused(m) && m.ReadU32(Catalog.LevelIdAddr) == LidLostCity
            ? 0 : (now - _waterTs) / (double)Stopwatch.Frequency;
        _waterTs = now;
        if (sec < 0) sec = 0;
        if (sec > HitchSeconds) sec = HitchSeconds;

        double frames = sec * (TicksPerSecond / RefTicks);
        _worldDrawFrac += frames;
        int n = (int)Math.Floor(_worldDrawFrac);
        _worldDrawFrac -= n;
        if (n > 0)
            _worldDraw += (uint)n;

        _waterDoneThisLoop = true;
        CommitWaterDrawCount(m);
        PaceRippleByFrames(m, frames);

        if (_waterLog < 8)
        {
            _waterLog++;
            PaceLog($"water wall {sec * 1000:0.00}ms origFrames={frames:0.000} draw={_worldDraw}");
        }
    }

    public static bool PreWorlds(CpuContext c, IMemory m)
    {
        CommitWaterDrawCount(m);
        return true;
    }

    public static void PostWorlds(CpuContext c, IMemory m) => RestoreRippleSpeed(m);

    public static bool PreWorldsRipple(CpuContext c, IMemory m)
    {
        // A0 == 0 is init: fill tri_wave from period, no present.
        if (c.A0 == 0) return true;
        CommitWaterDrawCount(m);
        ZeroRippleSpeed(m);
        return true;
    }

    public static void PostWorldsRipple(CpuContext c, IMemory m) => RestoreRippleSpeed(m);

    static void ZeroRippleSpeed(IMemory m)
    {
        if (_ripplePatched || !IsActive(m)) return;
        try
        {
            int speed = (int)m.ReadU32(RippleSpeedAddr);
            if (speed == 0) return;
            _savedRippleSpeed = speed;
            m.WriteU32(RippleSpeedAddr, 0);
            _ripplePatched = true;
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// <c>tri_wave[i] += ripple_speed</c> is one original 30 Hz step. Scale by
    /// wall original-frames (not display frames) and leave the guest add at 0.
    /// </summary>
    static void PaceRippleByFrames(IMemory m, double frames)
    {
        try
        {
            if (m.ReadU32(PausedAddr) != 0) return;
            int speed = (int)m.ReadU32(RippleSpeedAddr);
            if (speed == 0) return;
            _savedRippleSpeed = speed;
            _rippleFrac += speed * frames;
            int inc = (int)Math.Floor(_rippleFrac);
            _rippleFrac -= inc;
            if (inc != 0)
            {
                int period = (int)m.ReadU32(RipplePeriodAddr);
                for (uint i = 0; i < 16; i++)
                {
                    uint addr = TriWaveAddr + i * 4;
                    int w = (int)m.ReadU32(addr) + inc;
                    if (w > period)
                        w = -(period - 1);
                    m.WriteU32(addr, (uint)w);
                }
            }
            m.WriteU32(RippleSpeedAddr, 0);
            _ripplePatched = true;
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreRippleSpeed(IMemory m)
    {
        if (!_ripplePatched) return;
        _ripplePatched = false;
        try { m.WriteU32(RippleSpeedAddr, (uint)_savedRippleSpeed); }
        catch { /* overlay swap */ }
    }

    static void WriteAxisDt(IMemory m, uint addr, int from, int vel, int dt)
    {
        int game = (int)m.ReadU32(addr);
        long d = (long)game - from;
        if (d > Teleport || d < -Teleport)
            return;
        m.WriteU32(addr, (uint)(from + (int)((long)vel * dt / 1024)));
    }

    /// <summary>
    /// Same path: scale the progress step so CamFollow / auto-cam move at 30 Hz
    /// wall speed. ZonePathProgressToLoc then builds a consistent pose — no Euler lerp.
    /// Zone/path changes are teleports; leave those alone.
    /// </summary>
    public static bool PreLevelUpdate(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        try
        {
            if (c.A0 != m.ReadU32(CamZoneAddr) || c.A1 != m.ReadU32(CamPathAddr))
                return true;
            int cur = (int)m.ReadU32(CamProgressAddr);
            int req = (int)c.A2;
            int d = req - cur;
            if (d == 0) return true;
            if (d > PathWrap || d < -PathWrap) return true;
            c.A2 = (uint)(cur + ScaleStep(d));
        }
        catch
        {
            // zone swap
        }
        return true;
    }

    /// <summary>
    /// Look-behind / L-R offset and GoolSeek zoom/pan are +N per display
    /// frame. Keep dt/34 of those seeks. Snaps (new path zoom, force x=0)
    /// stay instant. Do not touch cam_progress here.
    /// </summary>
    public static bool PreCamFollow(CpuContext c, IMemory m)
    {
        _inCamFollow = false;
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        try
        {
            _camOffZ = (int)m.ReadU32(CamOffsetZAddr);
            _camOffX = (int)m.ReadU32(CamOffsetXAddr);
            _camOffY = (int)m.ReadU32(CamOffsetYAddr);
            _camZoom = (int)m.ReadU32(CamZoomAddr);
            _inCamFollow = true;
        }
        catch
        {
            // overlay swap
        }
        return true;
    }

    public static void PostCamFollow(CpuContext c, IMemory m)
    {
        if (!_inCamFollow) return;
        _inCamFollow = false;
        try
        {
            BlendCamSeek(m, CamOffsetZAddr, _camOffZ, CamSeekZ);
            BlendCamSeek(m, CamOffsetXAddr, _camOffX, CamSeekX);
            BlendCamSeek(m, CamOffsetYAddr, _camOffY, CamSeekY);
            BlendCamSeek(m, CamZoomAddr, _camZoom, CamSeekZoom);
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// CamDeath GoolTransforms onto the orbit then looks. That is a SET.
    /// Extra presents must not run it (and must not rewrite the pose after).
    /// One original call per 34 wall ticks; first death present always runs.
    /// </summary>
    public static bool PreCamDeath(CpuContext c, IMemory m)
    {
        if (!IsActive(m))
        {
            ClearDeathCam();
            return true;
        }
        if (!_dcamArmed)
        {
            _dcamArmed = true;
            _dcamAcc = 0;
            if (_dcamLog < 8)
            {
                _dcamLog++;
                PaceLog($"death cam start dt={_exactTicks:0.00}");
            }
            return true;
        }
        if (GamePaused(m) || _exactTicks <= 0)
            return false;
        if (_exactTicks >= RefTicks - 0.01)
        {
            _dcamAcc = 0;
            return true;
        }
        _dcamAcc += _exactTicks;
        if (_dcamAcc < RefTicks)
            return false;
        _dcamAcc -= RefTicks;
        return true;
    }

    static void ClearDeathCam()
    {
        _dcamArmed = false;
        _dcamAcc = 0;
    }

    static void BlendCamSeek(IMemory m, uint addr, int from, int maxStep)
    {
        int to = (int)m.ReadU32(addr);
        int d = to - from;
        if (d == 0) return;
        int ad = d < 0 ? -d : d;
        if (ad > maxStep) return;
        int kept = ScaleStep(d);
        m.WriteU32(addr, (uint)(from + kept));
        if (_camLog < 8)
        {
            _camLog++;
            PaceLog($"cam 0x{addr:X8} {from}->{to} kept={kept} dt={_exactTicks:0.00}");
        }
    }

    public static bool PreGoolSeek(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        // PostCamFollow scales the zoom/pan result. Crash trans is 34+scale.
        if (_inCamFollow || _haveObj) return true;
        c.A2 = (uint)ScaleStep((int)c.A2);
        return true;
    }

}
