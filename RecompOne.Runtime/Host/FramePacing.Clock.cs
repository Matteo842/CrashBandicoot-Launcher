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
    public static bool IsActive(IMemory? m)
    {
        if (!WantsUnlock || m == null || _inNsInit) return false;
        if (!_levelReady) return false;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            if (!Catalog.Levels.AllowsUnlockedFps(id) || WantsOriginalPad(m))
            {
                DropToOriginalPad(m, id);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Crash flies up, then CardC save/continue. That GOOL is per display
    /// frame — keep the original 30 Hz pad until NSInit.
    /// </summary>
    static bool IsWarpOut(IMemory m)
    {
        uint crash = m.ReadU32(CrashPtrAddr);
        if ((crash & 0xFF000000u) != 0x80000000u) return false;
        return m.ReadU32(crash + ObjStateOff) == StateWarpOut;
    }

    /// <summary>
    /// Warp_Out, Tawna/Brio/Cortex end, or a live CardC object. Sticky so
    /// TryArmUnlock cannot re-arm after 30 presents (0.1 s at uncapped).
    /// </summary>
    static void ArmSaveUiPad()
    {
        if (_saveUiPad) return;
        _saveUiPad = true;
        PaceLog("save UI pad");
    }

    static bool WantsOriginalPad(IMemory m)
    {
        if (_saveUiPad) return true;
        try
        {
            if (IsWarpOut(m))
            {
                ArmSaveUiPad();
                return true;
            }
        }
        catch
        {
            // overlay swap
        }
        return false;
    }

    static void NoteSaveUiObject(IMemory m, uint obj)
    {
        if (_saveUiPad) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(obj) == HandleListType) return;
            if (!TryReadGoolClass(m, obj, out uint type, out _)) return;
            if (type == GoolTypeCard)
            {
                ArmSaveUiPad();
                return;
            }
            if (type != GoolTypeBono) return;
            uint state = m.ReadU32(obj + ObjStateOff);
            if (state is StateTawnaCongrats or StateBrioEscape or StateCortexEscape)
                ArmSaveUiPad();
        }
        catch
        {
            // object freed
        }
    }

    static void NoteSaveUiWorld(IMemory m)
    {
        if (_saveUiPad) return;
        try
        {
            if (IsWarpOut(m))
            {
                ArmSaveUiPad();
                return;
            }
            int left = ObjectPoolMax;
            for (int h = 0; h < HandleCount && left > 0; h++)
            {
                uint handle = HandlesAddr + (uint)(h * 8);
                uint child = m.ReadU32(handle + HandleChildrenOff);
                WalkSaveUi(m, child, ref left);
                if (_saveUiPad) return;
            }
        }
        catch
        {
            // overlay swap
        }
    }

    static void WalkSaveUi(IMemory m, uint obj, ref int left)
    {
        int n = 0;
        while ((obj & 0xFF000000u) == 0x80000000u && n++ < ObjectPoolMax && left-- > 0)
        {
            uint kind = m.ReadU32(obj);
            if (kind == HandleListType)
            {
                WalkSaveUi(m, m.ReadU32(obj + HandleChildrenOff), ref left);
            }
            else
            {
                NoteSaveUiObject(m, obj);
                if (_saveUiPad) return;
                WalkSaveUi(m, m.ReadU32(obj + ObjChildrenOff), ref left);
                if (_saveUiPad) return;
            }
            obj = m.ReadU32(obj + ObjSiblingOff);
        }
    }

    static void DropToOriginalPad(IMemory m, uint id)
    {
        if (!_levelReady) return;
        _levelReady = false;
        _frameTicks = RefTicks;
        _exactTicks = RefTicks;
        WriteAllTicks(m, RefTicks);
        ResetWaterClock();
        PaceLog($"original pad lid={id}");
    }

    public static void Reset()
    {
        _guestTicks = 0;
        _frameTicks = 34;
        _tickFrac = 0;
        _exactTicks = 34;
        _stallFrac = 0;
        _simTs = 0;
        _irqTs = 0;
        _clockArmed = false;
        _haveObj = false;
        _crashObj = false;
        _crashHog = false;
        _hogPathFrac = 0;
        _hogTrotFracX = 0;
        _hogTrotFracY = 0;
        _hogTrotFracZ = 0;
        Array.Clear(_hogMemFrac);
        ClearCrashScaleFrac();
        _solidObj = false;
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _crashGateState = uint.MaxValue;
        _crashSpawnUsed = false;
        _crashLandAcc = 0;
        _lastBound.Clear();
        _animAcc.Clear();
        _animHold.Clear();
        _waitHoldTs.Clear();
        _poseClock.Clear();
        _animPose.Clear();
        _poseHoldLog = 0;
        _gateRot.Clear();
        _dispRotApplied = false;
        _dispTransApplied = false;
        _svtxPatched = false;
        _svtxLog = 0;
        _svtxCrashLog = 0;
        _svtxBoxLog = 0;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _spawnBudget = 0;
        _spawnAcc = 0;
        _spawnCredit.Clear();
        _simAcc.Clear();
        _pathHoppers.Clear();
        _platFrac.Clear();
        _platObj = false;
        _platLog = 0;
        ClearGatedRide();
        _rideLog = 0;
        _objClassLog = 0;
        _stampLog = 0;
        _paceLog = 0;
        _vsyncsInGpu = 0;
        _inGpuUpdate = false;
        _didPresentThisGpu = false;
        _ticksTakenThisLoop = false;
        _loggedUnlockGpu = false;
        _inNsInit = false;
        _levelReady = false;
        _saveUiPad = false;
        _holdLocked = 0;
        _warpHold = 0;
        _lastVsyncHleTs = 0;
        _armedThisGpu = false;
        _gpuFinished = false;
        _didPreUpdateObjects = false;
        _worldDraw = 0;
        _worldDrawFrac = 0;
        _rippleFrac = 0;
        _ripplePatched = false;
        _inCamFollow = false;
        _camLog = 0;
        ClearDeathCam();
        _dcamLog = 0;
        ResetWaterClock();
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "pacing.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} reset{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// PsyQ VSync(0) calls the wait helper twice. The second helper is not the
    /// game's 30 Hz pad — skipping it skipped the only present and froze after
    /// unlock. Treat extra waits as pad only after this GpuUpdate already presented.
    /// </summary>
    public static bool IsPadCall() => _inGpuUpdate && _didPresentThisGpu;

    public static void NoteGpuVSync()
    {
        if (_inGpuUpdate)
            _vsyncsInGpu++;
    }

    public static void NoteGpuPresent() => _didPresentThisGpu = true;

    /// <summary>
    /// MonoMod does not detour on Android, so PreGpuUpdate never runs. PsyQ
    /// VSync(0) is two HLE waits a few microseconds apart; a new GpuUpdate is
    /// anything after a 0.5 ms gap.
    /// </summary>
    public static void NoteSoftwareVblank()
    {
        if (!WantsUnlock) return;
        long now = Stopwatch.GetTimestamp();
        double ms = _lastVsyncHleTs == 0
            ? 999.0
            : (now - _lastVsyncHleTs) * 1000.0 / Stopwatch.Frequency;
        _lastVsyncHleTs = now;
        if (ms > 0.5)
            BeginGpuUpdate();
    }

    /// <summary>
    /// Called from every host present. Arms delta-time after the 30-frame hold
    /// even when GpuUpdate was a direct jal (no MonoMod, no Dispatcher.Call).
    /// </summary>
    public static void OnHostPresent(IMemory? m)
    {
        if (_inGpuUpdate)
            _didPresentThisGpu = true;
        if (m != null)
            TryArmUnlock(m);
    }

    static void BeginGpuUpdate()
    {
        _inGpuUpdate = true;
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        _armedThisGpu = false;
        _gpuFinished = false;
        // Android: GoolUpdateObjects is a direct jal, so PreUpdateObjects never
        // clears this. Reset here so AdvanceUnlocked can take the wall-clock step.
        // Windows already took dt in PreUpdateObjects — leave the flag alone.
        if (!_didPreUpdateObjects)
            _ticksTakenThisLoop = false;
    }

    public static void FinishGpuUpdate(IMemory m)
    {
        if (_gpuFinished) return;
        _gpuFinished = true;
        _inGpuUpdate = false;
        RestoreFadeStep(m);
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        _didPreUpdateObjects = false;
        TryArmUnlock(m);
        PatchTicksPerFrame(m);
        CommitWaterDrawCount(m);
        RestoreRippleSpeed(m);
        _waterDoneThisLoop = false;
    }

    /// <summary>
    /// Sim dt from wall time since the last sim step. The FrameRate combo
    /// only caps refresh — never a per-Hz lookup. A hitch longer than one
    /// original 30 Hz frame is dropped, not caught up.
    /// </summary>
    public static uint AdvanceWallClock(IMemory m)
    {
        long now = Stopwatch.GetTimestamp();
        if (!_clockArmed)
        {
            try { _guestTicks = m.ReadU32(TicksElapsedAddr); }
            catch { _guestTicks = 0; }
            _simTs = now;
            _tickFrac = 0;
            // Full original step so the first unlocked frame does not keep
            // 1/34 of a StopAtWalls correction (falls off crate lips).
            _exactTicks = RefTicks;
            _clockArmed = true;
            _frameTicks = RefTicks;
            _ticksTakenThisLoop = true;
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        // One dt per game loop. Extra PsyQ VSync helpers must not split it.
        if (_ticksTakenThisLoop)
        {
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        double sec = (now - _simTs) / (double)Stopwatch.Frequency;
        if (sec < MinStepSeconds)
        {
            _exactTicks = 0;
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        _simTs = now;
        if (sec > HitchSeconds)
            sec = HitchSeconds;

        _exactTicks = sec * TicksPerSecond;
        _tickFrac += _exactTicks;
        uint dt = (uint)Math.Floor(_tickFrac);
        _tickFrac -= dt;
        if (dt < 1) dt = 1;
        if (dt > RefTicks) dt = RefTicks;

        _frameTicks = dt;
        _guestTicks += dt;
        _ticksTakenThisLoop = true;
        PatchTicksPerFrame(m);
        return _guestTicks;
    }

    public static void PatchTicksPerFrame(IMemory m)
    {
        if (!IsActive(m)) return;
        WriteAllTicks(m, _frameTicks);
    }

    static void WriteAllTicks(IMemory m, uint ticks)
    {
        WriteTicks(m, GfxC1pAddr, ticks);
        WriteTicks(m, GfxC2pAddr, ticks);
        WriteTicks(m, GfxCurAddr, ticks);
    }

    /// <summary>
    /// GOOL waits on frames_elapsed = draw_stamp/34. GpuUpdate copies
    /// ticks_elapsed into draw_stamp after the object tree. If that guest
    /// clock only moves +1 per present, wait=1 lasts 34 displays (~1 Hz at
    /// 60 FPS, faster at 120). Publish wall ticks into ticks_elapsed and
    /// every gfx draw_stamp before GoolUpdateObjects so /34 is 30 Hz at
    /// any refresh.
    /// </summary>
    static void PublishWallStamps(IMemory m)
    {
        try { m.WriteU32(TicksElapsedAddr, _guestTicks); }
        catch { /* overlay swap */ }
        WriteDrawStamp(m, GfxC1pAddr);
        WriteDrawStamp(m, GfxC2pAddr);
        WriteDrawStamp(m, GfxCurAddr);
        if (_stampLog >= 6) return;
        try
        {
            _stampLog++;
            uint fe = m.ReadU32(FramesElapsedAddr);
            PaceLog($"stamp ticks={_guestTicks} fe={fe} wallFe={_guestTicks / RefTicks} dt={_exactTicks:0.00}/{_frameTicks}");
        }
        catch { /* */ }
    }

    static void WriteDrawStamp(IMemory m, uint ptrAddr)
    {
        try
        {
            uint ctx = m.ReadU32(ptrAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return;
            m.WriteU32(ctx + DrawStampOff, _guestTicks);
            m.WriteU32(ctx + SyncStampOff, _guestTicks);
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// Jump trans does <c>vely = spd(vely, 5454)</c> while X is held. That
    /// must share the wall clock with gravity. If these states use 34 ticks
    /// per display frame, hang is 30 Hz × fps (fly until X is released).
    /// Walk/stance stay 34+scale even if leftover fall vy is large.
    /// </summary>
    static bool CrashAirborne(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            // Jump / fall-jump / bounce / air spin. Walk is 2. Hang is in 3, 4, 10.
            return state is 3 or 4 or 5 or 6 or 10 or 11 or 14 or 16 or 18 or 20;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hog Wild / Whole Hog. Spawn sets TRACK_PATH_SIGN and trans does
    /// <c>pathprog = spd(pathprog, 4)</c> then calcpath — not walk physics.
    /// State indices differ by region; the flag does not.
    /// </summary>
    static bool CrashOnWarthog(IMemory m, uint obj)
    {
        try
        {
            if (IsLandLockedState(m, obj)) return false;
            return (m.ReadU32(obj + ObjStatusBOff) & FlagTrackPathSign) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hog jump / bounce. GROUNDLAND is still set on the takeoff present
    /// (CODE writes vely, trans is skipped). vy&gt;0 covers that frame;
    /// clear GROUNDLAND covers the hang. Not a WillC state index.
    /// </summary>
    static bool HogNeedsJumpY(IMemory m)
    {
        if (!_crashHog) return false;
        try
        {
            if ((m.ReadU32(_obj + ObjStatusAOff) & FlagGroundLand) == 0)
                return true;
            return (int)m.ReadU32(_obj + ObjVelYOff) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// WillC spin: Adjust_Time 15, Air_Adjust 16, Spin 17, Spin_Air 18,
    /// Spin_End 19, Spin_Air_End 20. Trans sends EventSpinHit to collider;
    /// crate Bound/PlotObjWalls need Crash inside the AABB.
    /// </summary>
    static bool CrashSpinning(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            return state is 15 or 16 or 17 or 18 or 19 or 20;
        }
        catch
        {
            return false;
        }
    }

    static bool IsLandLockedState(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            if (state == StateForceFall || state == StateWarpIn || state == StateDeathFlat
                || state == StateDeathWarthog
                || (state >= StateDeathFall && state <= StateDeathFast))
                return true;
            // NTSC-U vs J shifts WillC indices; 0x4000 is on the death cine itself.
            return (m.ReadU32(obj + ObjStateFlagsOff) & FlagStateDeathCine) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Warp_In / first-frame must run once so entity is bound before
    /// calcpath. A stuck first-frame flag must not run every present
    /// (that is the FALL_KILL loop).
    /// </summary>
    static bool CrashDeathInitFrame(IMemory m, uint obj)
    {
        if (!_crashObj || !IsLandLockedState(m, obj))
            return false;
        uint st;
        try { st = m.ReadU32(obj + ObjStateOff); }
        catch { return false; }
        bool entered = st != _crashGateState;
        _crashGateState = st;
        bool first = IsFirstFrame(m, obj);
        bool once = first && !_crashSpawnUsed;
        _crashSpawnUsed = first;
        if (entered)
        {
            _stallFrac = 0;
            _deathFadeLog = 0;
            PaceLog($"crash death gate st={st}");
            LogDeathFade(m, obj);
        }
        return entered || once;
    }

    /// <summary>
    /// Death cine / Warp_In / Force_Fall: one original 34-tick interpret
    /// per 34 wall ticks. Extra presents only draw. Wall dt, not an FPS cap.
    /// </summary>
    static bool CrashLandShouldUpdate(IMemory m, CpuContext c)
    {
        if (GamePaused(m))
        {
            _objScaled = true;
            DrawGatedObject(c, m, _obj);
            c.V0 = GoolSuccess;
            return false;
        }
        if (CrashDeathInitFrame(m, _obj))
        {
            _crashLandAcc = 0;
            return true;
        }
        _crashLandAcc += _exactTicks;
        if (_crashLandAcc < RefTicks)
        {
            _objScaled = true;
            DrawGatedObject(c, m, _obj);
            c.V0 = GoolSuccess;
            return false;
        }
        _crashLandAcc -= RefTicks;
        if (_crashObj)
            LogDeathFade(m, _obj);
        return true;
    }

    /// <summary>
    /// GpuUpdate adds fade_step every present. Death_Fall waits
    /// <c>FADECONTROL == -1</c> on the 30 Hz Crash interpret. At 400 Hz that
    /// hits 0 in 8 presents; if DISPLAY_UNK is set it stays 0 and never
    /// becomes the -1 sentinel. One original step per 34 wall ticks.
    /// Overlay still draws: fade_step=0 holds the current brightness.
    /// </summary>
    static void PaceFade(IMemory m)
    {
        RestoreFadeStep(m);
        if (!IsActive(m)) return;
        try
        {
            _fadeAcc += _exactTicks;
            if (_fadeAcc >= RefTicks)
            {
                _fadeAcc -= RefTicks;
                LogDeathFade(m, 0);
                return;
            }
            int fc = (int)m.ReadU32(FadeCounterAddr);
            // -2 → -1 is the done sentinel and does not use fade_step.
            if (fc is -2 or -1) return;
            _fadeStepSaved = m.ReadU32(FadeStepAddr);
            m.WriteU32(FadeStepAddr, 0);
            _fadeHold = true;
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreFadeStep(IMemory m)
    {
        if (!_fadeHold) return;
        _fadeHold = false;
        try { m.WriteU32(FadeStepAddr, _fadeStepSaved); }
        catch { /* overlay swap */ }
    }

    static void LogDeathFade(IMemory m, uint obj)
    {
        if (_deathFadeLog >= 40) return;
        try
        {
            if (obj == 0)
            {
                uint crash = m.ReadU32(CrashPtrAddr);
                if (crash == 0 || (crash & 0xFF000000u) != 0x80000000u) return;
                if (!IsLandLockedState(m, crash)) return;
                obj = crash;
            }
            _deathFadeLog++;
            uint st = m.ReadU32(obj + ObjStateOff);
            int fc = (int)m.ReadU32(FadeCounterAddr);
            int fs = (int)m.ReadU32(FadeStepAddr);
            uint sp = m.ReadU32(obj + ObjSpOff);
            uint wait = 0;
            if ((sp & 0xFF000000u) == 0x80000000u && sp >= 4)
                wait = m.ReadU32(sp - 4) >> 24;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            uint stall = m.ReadU32(obj + ObjAnimCounterOff);
            int y = (int)m.ReadU32(obj + ObjTransOff + 4);
            int anim = (int)m.ReadU32(obj + ObjAnimFrameOff) >> 8;
            PaceLog($"death fade st={st} fc={fc} step={fs} wait={wait} stall={stall} b=0x{b:X} y={y} anim={anim} fe={m.ReadU32(FramesElapsedAddr)}");
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>NTSC-U Warp_In is 40 (gooc dump lists it at 41).</summary>
    static bool IsWarpInState(uint state) =>
        state == StateDeathWarthog || state == StateWarpIn;

    /// <summary>
    /// playframe waits on a stack tag, not GOOL_FLAG_STALL, so ChangeState
    /// still accepts FALL_KILL during Death_Fall. Kill planes / StopAtZone
    /// restart the cine from the first playframes every 30 Hz — never
    /// FadeToBlack. Same-state death reenter is a no-op.
    /// </summary>
    public static bool PreChangeState(CpuContext c, IMemory m)
    {
        if (!IsActive(m)) return true;
        try
        {
            uint obj = c.A0;
            uint crash = m.ReadU32(CrashPtrAddr);
            if (obj != crash || (obj & 0xFF000000u) != 0x80000000u) return true;
            uint next = c.A1;
            uint cur = m.ReadU32(obj + ObjStateOff);
            if (cur != next) return true;
            if (next < StateDeathFall || next > StateDeathFast) return true;
            if (_deathReenterLog < 8)
            {
                _deathReenterLog++;
                PaceLog($"skip death reenter st={next}");
            }
            c.V0 = GoolSuccess;
            return false;
        }
        catch
        {
            return true;
        }
    }

    static void WriteCrashOrObjectTicks(IMemory m)
    {
        if (_crashObj)
        {
            _crashAir = CrashAirborne(m, _obj);
            WriteAllTicks(m, _crashAir ? _frameTicks : RefTicks);
            return;
        }
        WriteAllTicks(m, _solidObj ? RefTicks : _frameTicks);
    }

    static void WriteTicks(IMemory m, uint ptrAddr, uint ticks)
    {
        try
        {
            uint ctx = m.ReadU32(ptrAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return;
            m.WriteU32(ctx + TicksPerFrameOff, ticks);
        }
        catch
        {
            // overlay swap / unmapped
        }
    }

    public static uint TryReadGuestTicksPerFrame(IMemory? m)
    {
        if (m == null) return 0;
        try
        {
            uint ctx = m.ReadU32(GfxC1pAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return 0;
            return m.ReadU32(ctx + TicksPerFrameOff);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Original 30 FPS: one IRQ per present. Unlocked 120/240: extra IRQs so
    /// the sequencer stays at 60 Hz. Never used to advance ticks_elapsed.
    /// </summary>
    public static void PulseVblankIrq()
    {
        if (!IsActive(Runtime.Mem))
        {
            Runtime.DispatchIrq(0);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_irqTs == 0)
        {
            _irqTs = now;
            Runtime.DispatchIrq(0);
            return;
        }

        int n = 0;
        while (now - _irqTs >= IrqPeriod && n < 2)
        {
            _irqTs += IrqPeriod;
            Runtime.DispatchIrq(0);
            n++;
        }

        if (n == 0 && now - _irqTs > IrqPeriod * 8)
            _irqTs = now;
    }

    public static void NoteVblankIrq() => _irqTs = Stopwatch.GetTimestamp();

}
