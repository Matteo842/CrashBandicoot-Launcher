using System.Diagnostics;
using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Interpolated high FPS (prototype, <see cref="ViewConfig.InterpolatedFps"/>).
/// The game logic is not delta-timed at all: every 1/30 s one loop iteration
/// runs exactly like Original 30 FPS (a "step"). All other iterations are
/// draw-only passes: they start from a copy of the guest state taken after
/// the last step, draw every object and the camera between their poses of
/// the last two steps, present, and then the copy is put back. Nothing a
/// draw-only pass does can reach the logic, so physics, collisions and timers
/// are the 30 FPS ones at any refresh rate. The picture is one step (33 ms)
/// behind the logic.
/// CoreLoop presents inside GpuUpdate's VSync wait, before DrawOTag, and shows
/// the buffer of the iteration before. With the state put back the two draw
/// buffers never alternate, so while this mode is engaged steps do not present
/// and each draw-only pass presents its own buffer right after drawing it.
/// </summary>
public static partial class FramePacing
{
    /// <summary>CoreLoop's GpuUpdate call (see <see cref="SaveStates"/>): the only resumable point.</summary>
    const uint InterpSafeRa = 0x80012510u;

    /// <summary>Steps at a ready safe point before the first draw-only pass (two poses + settle).</summary>
    const int InterpWarmSteps = 3;

    /// <summary>Steps at 30 FPS after a draw-only pass got stuck in a guest wait (about 1 s).</summary>
    const int InterpStallCooldown = 30;

    /// <summary>More than a quarter turn in one step: a SET or a fast spinner, do not lerp.</summary>
    const int InterpRotSnap = 0x400;

    static readonly long InterpPeriod = Stopwatch.Frequency / 30;
    static readonly long InterpSlack = Stopwatch.Frequency / 1000;

    struct InterpVec9
    {
        public int Tx, Ty, Tz, Rx, Ry, Rz, Sx, Sy, Sz;
    }

    struct InterpPose
    {
        public InterpVec9 From, To;
        public uint Stamp;
        public uint Type;
        public bool HasFrom;
    }

    static bool _interpOn;
    static bool _interpEngaged;
    static bool _interpRender;
    static bool _interpDirty;
    static int _interpReady;
    static uint _interpStep = 1;
    static uint _interpDone;
    static long _interpStepTs;
    static double _interpT;
    static uint _interpObj;
    static int _interpLog;
    static bool _interpStall;
    static long _interpIterTs;

    // Once-a-second line in logs/interp.txt while engaged.
    static long _diagTs, _diagStepMax, _diagDrawMax;
    static int _diagSteps, _diagDraws, _diagObjLerp, _diagObjSnap, _diagObjNew;
    static int _diagCamLerp, _diagCamHold, _diagCamStale, _diagStalls, _diagWideOff;
    static int _diagWideMin = int.MaxValue, _diagWideMax, _diagPickX, _diagPickY;
    static double _diagTMin = 2, _diagTMax = -1;

    static readonly Dictionary<uint, InterpPose> _interpPoses = new();
    static InterpVec9 _interpCamFrom, _interpCamTo;
    static uint _interpCamStamp;
    static bool _interpCamHasFrom;

    static byte[]? _interpRam, _interpScratch;
    static bool _interpSnap;
    static readonly MemoryStream _interpRegs = new(8192);
    static readonly BinaryWriter _interpW = new(_interpRegs);
    static readonly BinaryReader _interpR = new(_interpRegs);

    // The two display origins the game alternates between, and the last draw origin.
    static int _interpDispAx = -1, _interpDispAy, _interpDispBx = -1, _interpDispBy;
    static bool _interpDispNextB;
    static int _interpDrawX, _interpDrawY;

    /// <summary>The setting is on and an unlocked rate is selected: delta time stays off.</summary>
    public static bool InterpEnabled => _interpOn;

    /// <summary>Steps and draw-only passes are running: steps must not present.</summary>
    public static bool InterpEngaged => _interpEngaged;

    /// <summary>This loop iteration is a draw-only pass on a throwaway guest state.</summary>
    public static bool InterpRenderPass => _interpRender;

    static bool InterpWanted()
    {
        var view = ConfigManager.View;
        // PAL speed slows the logic itself; that is not a refresh cap.
        return view.InterpolatedFps && WantsUnlock && view.FrameRate != ViewConfig.FrameRatePalSpeed;
    }

    static bool InterpReady(CpuContext c, IMemory m)
    {
        if (!_interpOn || !_levelReady || _inNsInit) return false;
        if (c.RA != InterpSafeRa) return false;
        if (LibCdStream.InUse || BiosA.CardFileOpen) return false;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            return AllowsUnlock(m, id) && !IsDemoPlayback(m);
        }
        catch
        {
            return false;
        }
    }

    static void InterpDisengage()
    {
        if (_interpEngaged)
            PaceLog("interp disengaged");
        _interpEngaged = false;
        _interpRender = false;
        _interpDirty = false;
        _interpStall = false;
        _interpIterTs = 0;
        _interpReady = 0;
        _interpSnap = false;
    }

    /// <summary>Guest RAM was replaced (save state) or the level is rebuilt: every pose is stale.</summary>
    static void InterpReset()
    {
        InterpDisengage();
        _interpPoses.Clear();
        _interpCamStamp = 0;
        _interpCamHasFrom = false;
    }

    /// <summary>
    /// End of a draw-only pass: put the guest back to the state after the last
    /// step, then show what the pass drew. Tools and overlays that read RAM
    /// during the present see the real state.
    /// </summary>
    static bool InterpFinishRender(CpuContext c, IMemory m)
    {
        if (!_interpRender) return false;
        _interpRender = false;
        _interpDirty = false;
        if (c.RA != InterpSafeRa || m is not PSMemory psm || !_interpSnap)
        {
            // Registers can only be put back at CoreLoop's own call. Not expected:
            // nothing in a draw-only pass reaches another GpuUpdate.
            PaceLog($"interp: draw-only pass ended at RA=0x{c.RA:X8}, state kept");
            InterpDisengage();
            return false;
        }
        long cost = Stopwatch.GetTimestamp() - _interpIterTs;
        if (cost > _diagDrawMax) _diagDrawMax = cost;
        _diagDraws++;
        InterpRestore(c, psm);
        InterpPresent();
        if (_interpStall)
        {
            // The guest state after the last step waits for vblanks (level change,
            // cutscene skip). Let the original loop do that wait.
            _diagStalls++;
            PaceLog("interp: draw-only pass stalled in a guest wait, backing off");
            InterpDisengage();
            _interpReady = -InterpStallCooldown;
        }
        return true;
    }

    /// <summary>LibEtc.MaybeCatchUpVBlank fired: this iteration has been waiting for vblanks.</summary>
    public static void InterpNoteStall()
    {
        if (_interpRender) _interpStall = true;
    }

    /// <summary>
    /// Start of a logic step. The pad is read now, not at the end of the step
    /// before (33 ms older). Host pad sequences (exit to map) count original
    /// vblanks, two per step, after the sample that shows their current phase.
    /// </summary>
    static void InterpBeginStep(IMemory m)
    {
        try
        {
            BiosB.RefreshPad(m);
            LibPad.Refresh(m);
        }
        catch
        {
            // overlay swap
        }
        ExitToMapInjector.TickLogicFrame();
        ExitToMapInjector.TickLogicFrame();
        // A step is two vblanks; only a step that really waits gets more.
        LibEtc.HoldCatchUp(0);
    }

    /// <summary>
    /// CoreLoop safe point, after any save state work: note the step that just
    /// ended and choose what the next loop iteration is.
    /// </summary>
    static void InterpPlan(CpuContext c, IMemory m, bool wasRender)
    {
        bool on = InterpWanted();
        if (on != _interpOn)
        {
            _interpOn = on;
            InterpReset();
            Reset();
            PaceLog($"interp setting {(on ? "on" : "off")}");
        }
        if (!wasRender)
        {
            _interpDone = _interpStep;
            _interpStep++;
        }
        if (m is not PSMemory psm || !InterpReady(c, m))
        {
            InterpDisengage();
            return;
        }
        if (!wasRender && _interpReady < InterpWarmSteps)
            _interpReady++;
        if (_interpReady < InterpWarmSteps) return;

        long now = Stopwatch.GetTimestamp();
        if (!wasRender)
        {
            if (_interpEngaged && _interpIterTs != 0)
            {
                _diagSteps++;
                if (now - _interpIterTs > _diagStepMax) _diagStepMax = now - _interpIterTs;
            }
            // Every draw-only pass until the next step starts from this state.
            InterpCapture(c, psm);
            if (!_interpEngaged)
            {
                _interpEngaged = true;
                _interpStepTs = now;
                PaceLog("interp engaged");
            }
            // Always one picture per step, also when the display is slower than 30 Hz.
            _interpRender = true;
        }
        else if (now - _interpStepTs >= InterpPeriod - InterpSlack)
        {
            _interpStepTs += InterpPeriod;
            // A hitch is dropped, not caught up with a burst of steps.
            if (now - _interpStepTs > InterpPeriod)
                _interpStepTs = now;
            _interpRender = false;
        }
        else
            _interpRender = true;

        if (_interpRender)
        {
            _interpT = Math.Clamp((now - _interpStepTs) / (double)InterpPeriod, 0.0, 1.0);
            if (_interpT < _diagTMin) _diagTMin = _interpT;
            if (_interpT > _diagTMax) _diagTMax = _interpT;
            // A pass draws in a few ms. Stuck for four vblanks = the guest is waiting.
            LibEtc.HoldCatchUp(4);
        }
        else
            InterpBeginStep(m);
        InterpDiag(now);
        _interpIterTs = Stopwatch.GetTimestamp();
    }

    /// <summary>DrawNativeWideWorld in a draw-only pass: how much 16:9 side scenery this picture gets.</summary>
    static void InterpNoteWide(int triangles, bool active)
    {
        if (!_interpRender) return;
        if (!active) _diagWideOff++;
        if (triangles < _diagWideMin) _diagWideMin = triangles;
        if (triangles > _diagWideMax) _diagWideMax = triangles;
    }

    static void InterpDiag(long now)
    {
        if (_diagTs == 0) _diagTs = now;
        if (now - _diagTs < Stopwatch.Frequency) return;
        _diagTs = now;
        double ms = 1000.0 / Stopwatch.Frequency;
        string line = $"{DateTime.Now:HH:mm:ss} steps={_diagSteps} draws={_diagDraws}"
            + $" obj lerp={_diagObjLerp} snap={_diagObjSnap} new={_diagObjNew}"
            + $" cam lerp={_diagCamLerp} hold={_diagCamHold} stale={_diagCamStale}"
            + $" t={_diagTMin:0.00}..{_diagTMax:0.00}"
            + $" stepMaxMs={_diagStepMax * ms:0.0} drawMaxMs={_diagDrawMax * ms:0.0}"
            + $" dispA=({_interpDispAx},{_interpDispAy}) dispB=({_interpDispBx},{_interpDispBy})"
            + $" pick=({_diagPickX},{_diagPickY}) draw=({_interpDrawX},{_interpDrawY})"
            + $" wideTris={(_diagWideMin == int.MaxValue ? 0 : _diagWideMin)}..{_diagWideMax} wideOff={_diagWideOff}"
            + $" stalls={_diagStalls}";
        _diagSteps = _diagDraws = _diagObjLerp = _diagObjSnap = _diagObjNew = 0;
        _diagCamLerp = _diagCamHold = _diagCamStale = _diagStalls = _diagWideOff = 0;
        _diagWideMin = int.MaxValue;
        _diagWideMax = 0;
        _diagStepMax = _diagDrawMax = 0;
        _diagTMin = 2;
        _diagTMax = -1;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "interp.txt"), line + Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// NSInit inside a draw-only pass: CoreLoop found a level change at the top
    /// of the loop, before any draw-only hook changed anything. That is exactly
    /// what the next step would do, so the pass simply becomes that step.
    /// </summary>
    static void InterpPromoteToStep()
    {
        if (!_interpRender) return;
        PaceLog(_interpDirty
            ? "interp: NSInit in a draw-only pass after it drew, state kept"
            : "interp: draw-only pass became a step (NSInit)");
        InterpDisengage();
    }

    static void InterpCapture(CpuContext c, PSMemory psm)
    {
        psm.CopyGuestRam(ref _interpRam, ref _interpScratch);
        _interpRegs.Position = 0;
        _interpRegs.SetLength(0);
        c.SaveState(_interpW);
        Gte.SaveState(_interpW);
        BiosA.SaveState(_interpW);
        BiosB.SaveState(_interpW);
        LibPad.SaveState(_interpW);
        _interpW.Flush();
        _interpSnap = true;
    }

    static void InterpRestore(CpuContext c, PSMemory psm)
    {
        psm.RestoreGuestRam(_interpRam!, _interpScratch!);
        _interpRegs.Position = 0;
        c.LoadState(_interpR);
        Gte.LoadState(_interpR);
        BiosA.LoadState(_interpR);
        BiosB.LoadState(_interpR);
        LibPad.LoadState(_interpR);
    }

    static void InterpPresent()
    {
        var gpu = Runtime.Gpu;
        if (gpu != null && _interpDispAx >= 0)
        {
            // Show the buffer this pass drew into: the display origin nearest its draw origin.
            int x = _interpDispAx, y = _interpDispAy;
            if (_interpDispBx >= 0
                && Math.Abs(_interpDispBx - _interpDrawX) + Math.Abs(_interpDispBy - _interpDrawY)
                    < Math.Abs(x - _interpDrawX) + Math.Abs(y - _interpDrawY))
            {
                x = _interpDispBx;
                y = _interpDispBy;
            }
            _diagPickX = x;
            _diagPickY = y;
            gpu.WriteGp1(0x05000000u | (((uint)y & 0x3FF) << 10) | ((uint)x & 0x3FF));
        }
        Runtime.PresentInterp();
    }

    /// <summary>LibGpu.PutDispEnv: remember the two display origins of the double buffer.</summary>
    public static void InterpNoteDisp(int x, int y)
    {
        if (!_interpOn) return;
        if ((x == _interpDispAx && y == _interpDispAy) || (x == _interpDispBx && y == _interpDispBy))
            return;
        if (_interpDispNextB)
        {
            _interpDispBx = x;
            _interpDispBy = y;
        }
        else
        {
            _interpDispAx = x;
            _interpDispAy = y;
        }
        _interpDispNextB = !_interpDispNextB;
    }

    /// <summary>LibGpu.PutDrawEnv: where this iteration draws.</summary>
    public static void InterpNoteDraw(int x, int y)
    {
        _interpDrawX = x;
        _interpDrawY = y;
    }

    /// <summary>LibEtc.VSync in a draw-only pass: no wait, no present, no vblank side effects.</summary>
    public static void InterpNoteRenderVSync() => _interpDirty = true;

    static InterpVec9 InterpReadObject(IMemory m, uint obj)
    {
        InterpVec9 v;
        v.Tx = (int)m.ReadU32(obj + ObjTransOff);
        v.Ty = (int)m.ReadU32(obj + ObjTransOff + 4);
        v.Tz = (int)m.ReadU32(obj + ObjTransOff + 8);
        v.Rx = (int)m.ReadU32(obj + ObjRotOff);
        v.Ry = (int)m.ReadU32(obj + ObjRotOff + 4);
        v.Rz = (int)m.ReadU32(obj + ObjRotOff + 8);
        v.Sx = (int)m.ReadU32(obj + ObjScaleOff);
        v.Sy = (int)m.ReadU32(obj + ObjScaleOff + 4);
        v.Sz = (int)m.ReadU32(obj + ObjScaleOff + 8);
        return v;
    }

    static bool InterpJumped(in InterpVec9 a, in InterpVec9 b) =>
        Math.Abs((long)b.Tx - a.Tx) > Teleport
        || Math.Abs((long)b.Ty - a.Ty) > Teleport
        || Math.Abs((long)b.Tz - a.Tz) > Teleport;

    static int InterpLerp(int a, int b, double t) => a + (int)Math.Round(((long)b - a) * t);

    /// <summary>12-bit angle, shortest way round. A jump over a quarter turn is not a motion.</summary>
    static int InterpAngle(int a, int b, double t)
    {
        int d = ((b - a + 0x800) & 0xFFF) - 0x800;
        if (Math.Abs(d) > InterpRotSnap) return b;
        return a + (int)Math.Round(d * t);
    }

    /// <summary>
    /// After GoolObjectUpdate in a step: the pose the original just drew. The
    /// pose of the step before becomes the start of the lerp, unless the pool
    /// slot changed hands or the object was moved by a SET.
    /// </summary>
    static void InterpCapturePose(IMemory m)
    {
        uint obj = _interpObj;
        _interpObj = 0;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            InterpVec9 to = InterpReadObject(m, obj);
            TryReadGoolClass(m, obj, out uint type, out _);
            bool had = _interpPoses.TryGetValue(obj, out InterpPose prev)
                && prev.Stamp == _interpStep - 1 && prev.Type == type;
            InterpPose p;
            p.To = to;
            p.From = had ? prev.To : to;
            p.HasFrom = had && !InterpJumped(prev.To, to);
            p.Stamp = _interpStep;
            p.Type = type;
            _interpPoses[obj] = p;
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// GoolObjectUpdate in a draw-only pass: no GOOL, no physics, no pad. Only
    /// the draw, at the pose between the last two steps. An object without a
    /// pose from the last step was born inside this pass (zone spawn) and is
    /// not shown until the real step has run it.
    /// </summary>
    static bool InterpDrawObject(CpuContext c, IMemory m)
    {
        _interpDirty = true;
        uint obj = c.A0;
        c.V0 = GoolSuccess;
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!_interpPoses.TryGetValue(obj, out InterpPose p) || p.Stamp != _interpDone)
            {
                _diagObjNew++;
                return false;
            }
            if (!p.HasFrom)
                _diagObjSnap++;
            else
            {
                _diagObjLerp++;
                double t = _interpT;
                m.WriteU32(obj + ObjTransOff, (uint)InterpLerp(p.From.Tx, p.To.Tx, t));
                m.WriteU32(obj + ObjTransOff + 4, (uint)InterpLerp(p.From.Ty, p.To.Ty, t));
                m.WriteU32(obj + ObjTransOff + 8, (uint)InterpLerp(p.From.Tz, p.To.Tz, t));
                m.WriteU32(obj + ObjRotOff, (uint)InterpAngle(p.From.Rx, p.To.Rx, t));
                m.WriteU32(obj + ObjRotOff + 4, (uint)InterpAngle(p.From.Ry, p.To.Ry, t));
                m.WriteU32(obj + ObjRotOff + 8, (uint)InterpAngle(p.From.Rz, p.To.Rz, t));
                m.WriteU32(obj + ObjScaleOff, (uint)InterpLerp(p.From.Sx, p.To.Sx, t));
                m.WriteU32(obj + ObjScaleOff + 4, (uint)InterpLerp(p.From.Sy, p.To.Sy, t));
                m.WriteU32(obj + ObjScaleOff + 8, (uint)InterpLerp(p.From.Sz, p.To.Sz, t));
            }
            uint statusB = m.ReadU32(obj + ObjStatusBOff);
            uint seq = m.ReadU32(obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return false;
            if ((statusB & FlagInvisible) != 0) return false;
            if (!GatedObjectShouldDisplay(m, obj, statusB)) return false;
            Dispatcher.Call(c, m, GoolObjectTransformAddr);
        }
        catch (Exception ex)
        {
            if (_interpLog < 4)
            {
                _interpLog++;
                PaceLog($"interp draw obj=0x{obj:X8}: {ex.Message}");
            }
        }
        c.A0 = obj;
        c.V0 = GoolSuccess;
        return false;
    }

    static InterpVec9 InterpReadCamera(IMemory m)
    {
        InterpVec9 v = default;
        v.Tx = (int)m.ReadU32(CamTransAddr);
        v.Ty = (int)m.ReadU32(CamTransAddr + 4);
        v.Tz = (int)m.ReadU32(CamTransAddr + 8);
        v.Rx = (int)m.ReadU32(CamRotAddr);
        v.Ry = (int)m.ReadU32(CamRotAddr + 4);
        v.Rz = (int)m.ReadU32(CamRotAddr + 8);
        return v;
    }

    static bool InterpCamCut(in InterpVec9 a, in InterpVec9 b)
    {
        if (InterpJumped(a, b)) return true;
        static int Turn(int x, int y) => Math.Abs(((y - x + 0x800) & 0xFFF) - 0x800);
        return Turn(a.Rx, b.Rx) > HogCamRotTeleport
            || Turn(a.Ry, b.Ry) > HogCamRotTeleport
            || Turn(a.Rz, b.Rz) > HogCamRotTeleport;
    }

    /// <summary>
    /// PreGfxUpdateMatrices. Step: the camera this step draws with. Draw-only
    /// pass: the camera between the last two steps, written before the
    /// matrices, worlds and objects use it. Returns true in a draw-only pass.
    /// </summary>
    static bool InterpCamera(IMemory m)
    {
        if (!_interpOn) return false;
        try
        {
            if (_interpRender)
            {
                _interpDirty = true;
                if (_interpCamStamp != _interpDone)
                {
                    _diagCamStale++;
                    return true;
                }
                if (_interpCamHasFrom) _diagCamLerp++;
                else _diagCamHold++;
                InterpVec9 a = _interpCamFrom, b = _interpCamTo;
                double t = _interpCamHasFrom ? _interpT : 1.0;
                m.WriteU32(CamTransAddr, (uint)InterpLerp(a.Tx, b.Tx, t));
                m.WriteU32(CamTransAddr + 4, (uint)InterpLerp(a.Ty, b.Ty, t));
                m.WriteU32(CamTransAddr + 8, (uint)InterpLerp(a.Tz, b.Tz, t));
                m.WriteU32(CamRotAddr, (uint)InterpAngle(a.Rx, b.Rx, t));
                m.WriteU32(CamRotAddr + 4, (uint)InterpAngle(a.Ry, b.Ry, t));
                m.WriteU32(CamRotAddr + 8, (uint)InterpAngle(a.Rz, b.Rz, t));
                return true;
            }
            InterpVec9 cur = InterpReadCamera(m);
            if (_interpCamStamp == _interpStep)
            {
                // Second matrix update of the same step: only the end pose moves.
                _interpCamTo = cur;
                return false;
            }
            bool had = _interpCamStamp == _interpStep - 1;
            _interpCamFrom = had ? _interpCamTo : cur;
            _interpCamHasFrom = had && !InterpCamCut(_interpCamTo, cur);
            _interpCamTo = cur;
            _interpCamStamp = _interpStep;
        }
        catch
        {
            // overlay swap
        }
        return false;
    }
}
