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
    /// GOOL function-pointer updates go through <see cref="Dispatcher.Call"/>.
    /// Direct jals (physics, cam, worlds, GpuUpdate) are hooked at compile time
    /// in CrashBandicoot.json — MonoMod detours do not exist on Android.
    /// </summary>
    public static void InstallGameHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;

        Dispatcher.CallPre = OnCallPre;
        Dispatcher.CallPost = OnCallPost;
    }

    public static bool PreUpdateObjects(CpuContext c, IMemory m)
    {
        if (_didPreUpdateObjects) return true;
        _didPreUpdateObjects = true;
        _ticksTakenThisLoop = false;
        _crashDidScale = false;
        NoteSaveUiWorld(m);
        if (IsActive(m))
        {
            EnsureGfxHook();
            AdvanceWallClock(m);
            PublishWallStamps(m);
            RefillSpawnBudget();
        }
        else
        {
            _spawnBudget = 0;
            _spawnAcc = 0;
            if (_spawnCredit.Count > 0)
                _spawnCredit.Clear();
        }
        return true;
    }

    static void RefillSpawnBudget()
    {
        _spawnAcc += _exactTicks;
        while (_spawnAcc >= RefTicks)
        {
            _spawnAcc -= RefTicks;
            _spawnBudget += SpawnBurstMax;
        }
        if (_spawnBudget > SpawnBurstMax + SpawnBurstMax / 2)
            _spawnBudget = SpawnBurstMax + SpawnBurstMax / 2;
    }

    static bool OnCallPre(uint addr, CpuContext c, IMemory m) => addr switch
    {
        NsInitAddr => PreNsInit(c, m),
        GoolUpdateObjectsAddr => PreUpdateObjects(c, m),
        GpuUpdateAddr => PreGpuUpdate(c, m),
        GoolObjectTransformAddr => PreTransform(c, m),
        GoolObjectPhysicsAddr => PrePhysics(c, m),
        GoolObjectCreateAddr => PreObjectCreate(c, m),
        GfxUpdateMatricesAddr => PreGfxUpdateMatrices(c, m),
        GfxTransformSvtxAddr or GfxTransformCvtxAddr => PreGfxTransformMesh(c, m),
        GfxTransformWorldsAddr or GfxTransformWorldsFogAddr
            or GfxTransformWorldsLightningAddr
            or GfxTransformWorldsDarkAddr
            or GfxTransformWorldsDark2Addr => PreWorlds(c, m),
        GfxTransformWorldsRippleAddr => PreWorldsRipple(c, m),
        LevelUpdateAddr => PreLevelUpdate(c, m),
        GoolSeekAddr => PreGoolSeek(c, m),
        CamFollowAddr or CamUpdateAddr => PreCamFollow(c, m),
        CamDeathAddr => PreCamDeath(c, m),
        GoolObjectChangeStateAddr => PreChangeState(c, m),
        GoolObjectUpdateAddr => PreGoolObjectUpdate(c, m),
        GoolObjectInterpretAddr => PreGoolInterpret(c, m),
        _ => true,
    };

    static bool PreGoolObjectUpdate(CpuContext c, IMemory m)
    {
        _haveObj = false;
        _crashObj = false;
        _crashHog = false;
        _solidObj = false;
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _haveTransY = false;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _platObj = false;
        _platFirst = false;
        _platChild = false;
        _platCarry = false;
        // Hold / NSInit run GOOL with pacing off. Hoppers Wait then Jump
        // (may OR SOLID_TOP). If we do not record them here, unlock treats
        // them as pillars and i+=1 runs every present until Crash dies and
        // they spawn again while unlocked.
        if (!IsActive(m))
        {
            IsPathHopper(m, c.A0);
            return true;
        }
        SnapshotObject(m, c.A0);
        _solidObj = _haveObj && !_crashObj && !KeepRealDt(m, c.A0);
        if (_crashObj)
        {
            if (!IsLandLockedState(m, _obj))
            {
                if (_crashGateState != uint.MaxValue)
                    _stallFrac = 0;
                _crashGateState = uint.MaxValue;
                _crashLandAcc = 0;
                if (!IsFirstFrame(m, _obj))
                    _crashSpawnUsed = false;
                _simAcc.Remove(_obj);
            }
            else if (!CrashLandShouldUpdate(m, c))
                return false;
            else
            {
                WriteAllTicks(m, RefTicks);
                _objScaled = true;
            }
        }
        _platObj = _haveObj && !_crashObj && !_solidObj && IsPlatformObj(m, c.A0);
        _platFirst = _platObj && IsFirstFrame(m, c.A0);
        _platChild = _platObj && IsPacedPlatformChild(m, c.A0);
        LogObjectClass(m, c.A0);
        if (_solidObj)
        {
            // Trans runs every display frame. >>=2, ReactSolid, setvel, spawn
            // in trans are per-call not per-tick — 34+scale cannot fix that.
            // Run one original 34-tick update per 34 wall ticks; still draw.
            // First frame always runs so box_link / stall init is not delayed.
            if (_simAcc.Count > 96)
                EvictDict(_simAcc, _obj);
            if (IsFirstFrame(m, _obj))
            {
                _simAcc[_obj] = 0;
                WriteAllTicks(m, RefTicks);
                FlushGatedRide(m, _obj);
                SnapshotGatedCarry(m, _obj);
            }
            else if (GamePaused(m))
            {
                // Display still runs every present. Do not accumulate skip
                // t or let wait=0 GOOL fire at refresh rate.
                _gatedSolid = true;
                DrawGatedObject(c, m, _obj);
                c.V0 = GoolSuccess;
                return false;
            }
            else
            {
                _simAcc.TryGetValue(_obj, out double acc);
                acc += _exactTicks;
                if (acc < RefTicks)
                {
                    _simAcc[_obj] = acc;
                    _gatedSolid = true;
                    DrawGatedObject(c, m, _obj);
                    c.V0 = GoolSuccess;
                    return false;
                }
                _simAcc[_obj] = acc - RefTicks;
                WriteAllTicks(m, RefTicks);
                FlushGatedRide(m, _obj);
                SnapshotGatedCarry(m, _obj);
            }
        }
        else if (!(_crashObj && IsLandLockedState(m, _obj)))
        {
            WriteCrashOrObjectTicks(m);
            // Gated objects already run one Update per 34 wall ticks.
            // wait=0 on Crash / path TNT is next present — hold to wall dt.
            if (_haveObj)
                HoldAnimWait(m, c.A0);
            if (_platObj)
            {
                // Original 34-tick spd()/Euler, then Post keeps dt/34.
                // Children of a paced parent only need ticks=34 for spd(force).
                if (!_platChild)
                    SnapshotPlatform(m, c.A0);
                WriteAllTicks(m, RefTicks);
            }
        }
        // Land-lock already runs one Update per 34 wall ticks; that
        // decrement is the stall clock. Adding back here froze Warp_In
        // and Death_Spin (playframes never left STALL).
        if (_crashObj && !IsLandLockedState(m, _obj))
            HoldCrashStall(m, c.A0);
        if (!_crashObj)
            ClampAnimFrame(m, c.A0);
        if (_haveObj)
            ArmSpawnBurst(m, c.A0);
        if (_haveObj && !_crashObj)
            TryFillTemplePlatCollider(m, c.A0);
        return true;
    }

    static void OnCallPost(uint addr, CpuContext c, IMemory m)
    {
        switch (addr)
        {
            case NsInitAddr:
                PostNsInit(c, m);
                return;
            case GpuUpdateAddr:
                PostGpuUpdate(c, m);
                return;
            case GoolObjectPhysicsAddr:
                PostObjectPhysics(c, m);
                return;
            case GfxTransformWorldsAddr:
            case GfxTransformWorldsFogAddr:
            case GfxTransformWorldsLightningAddr:
            case GfxTransformWorldsDarkAddr:
            case GfxTransformWorldsDark2Addr:
                PostWorlds(c, m);
                return;
            case GfxTransformWorldsRippleAddr:
                PostWorldsRipple(c, m);
                return;
            case CamFollowAddr:
            case CamUpdateAddr:
                PostCamFollow(c, m);
                return;
            case GoolObjectTransformAddr:
                RestoreGatedDisplayRot(m, c.A0);
                return;
            case GfxTransformSvtxAddr:
            case GfxTransformCvtxAddr:
                RestoreSvtx(m);
                return;
            case GoolObjectUpdateAddr:
                break;
            default:
                return;
        }
        NoteSpawnUsed();
        if (_haveObj)
        {
            NoteSaveUiObject(m, _obj);
            RestoreGatedDisplayRot(m, _obj);
        }
        if (_solidObj && !_gatedSolid && _haveObj)
        {
            CaptureBound(m, _obj);
            CaptureGateRot(m, _obj);
            PaceGatedCarry(m, _obj);
        }
        if (_crashObj && !_objScaled && _haveObj)
            FinishPacedScale(m);
        else if (_platObj && !_platFirst && !_platChild && _haveObj)
        {
            if (TryReadGoolClass(m, _obj, out uint type, out _) && IsLostCityPusher(m, _obj, type))
                CaptureBound(m, _obj);
            PacePlatform(m);
        }
        else if (!_crashObj && !_solidObj && _haveObj)
        {
            PaceSpriteAnim(m);
            PaceExtraTrans(m);
        }
        if (IsActive(m))
            WriteAllTicks(m, _frameTicks);
    }

    public static bool PreTransform(CpuContext c, IMemory m)
    {
        EnsureGfxHook();
        if (IsActive(m))
        {
            ClampAnimFrame(m, c.A0);
            HoldAnimPose(m, c.A0);
        }
        ApplyGatedDisplayPose(m, c.A0);
        // Crash look is patched in PreGfxTransformMesh on the real SVTX
        // pointer (A0). PreTransform is too early and a second patch here
        // made PreGfx skip the buffer Gfx actually reads.
        if (IsActive(m) && c.A0 == _obj && !_crashObj)
            PatchObjectMesh(c, m, c.A0);
        return true;
    }

    public static bool PrePhysics(CpuContext c, IMemory m)
    {
        if (!IsActive(m)) return true;
        // CODE may have Warp_In → hog spawn (calcpath) or EventHit → death.
        // Snapshot ran on the previous state, so re-read the ride flag.
        if (_crashObj)
            _crashHog = CrashOnWarthog(m, _obj);
        // Death is already a 30 Hz GoolObjectUpdate. Do not 34+scale or
        // skip physics on that step (that stacked to ~4 Hz Force_Fall).
        if (_crashObj && IsLandLockedState(m, _obj))
            return true;
        WriteCrashOrObjectTicks(m);
        // Trans already used wall ticks (hang). Physics needs a 34-tick
        // StopAtWalls step on XZ; Y is reintegrated in FinishPacedScale.
        if (_crashObj && _crashAir)
        {
            _yTrans = (int)m.ReadU32(_obj + ObjTransOff + 4);
            _vyTrans = (int)m.ReadU32(_obj + ObjVelYOff);
            _haveTransY = true;
            WriteAllTicks(m, RefTicks);
        }
        else if (_crashObj && HogNeedsJumpY(m))
        {
            _yTrans = (int)m.ReadU32(_obj + ObjTransOff + 4);
            _vyTrans = (int)m.ReadU32(_obj + ObjVelYOff);
            _haveTransY = true;
        }
        return true;
    }

    public static void PostObjectPhysics(CpuContext c, IMemory m)
    {
        if (_crashObj && IsActive(m))
            FinishPacedScale(m);
    }

    public static bool PreGpuUpdate(CpuContext c, IMemory m)
    {
        BeginGpuUpdate();
        // After the whole object tree: skip-frame plat acc matches the
        // drawn lerp. FinishPacedScale may have used last present's t.
        RideAfterCrash(m);
        PaceFade(m);
        if (_levelReady && !_loggedUnlockGpu)
        {
            _loggedUnlockGpu = true;
            PaceLog("first unlocked GpuUpdate");
        }
        return true;
    }

    public static void PostGpuUpdate(CpuContext c, IMemory m) => FinishGpuUpdate(m);

    public static bool PreNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = true;
        _levelReady = false;
        _saveUiPad = false;
        _holdLocked = 0;
        _warpHold = 0;
        _loggedUnlockGpu = false;
        ResetWaterClock();
        _fadeAcc = 0;
        _fadeHold = false;
        _deathReenterLog = 0;
        _deathFadeLog = 0;
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
        _stampLog = 0;
        _platFrac.Clear();
        _platObj = false;
        _platLog = 0;
        ClearGatedRide();
        _rideLog = 0;
        _pathHoppers.Clear();
        _hogPathFrac = 0;
        _hogTrotFracX = 0;
        _hogTrotFracY = 0;
        _hogTrotFracZ = 0;
        Array.Clear(_hogMemFrac);
        ClearCrashScaleFrac();
        ClearDeathCam();
        _crashGateState = uint.MaxValue;
        _crashSpawnUsed = false;
        _crashLandAcc = 0;
        PaceLog($"NSInit start lid=0x{c.A1:X}");
        return true;
    }

    public static void PostNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = false;
        _clockArmed = false;
        PaceLog("NSInit end");
    }

    static void TryArmUnlock(IMemory m)
    {
        if (_levelReady || !WantsUnlock || _inNsInit) return;
        if (_armedThisGpu) return;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            NoteSaveUiWorld(m);
            if (!Catalog.Levels.AllowsUnlockedFps(id) || _saveUiPad)
            {
                _holdLocked = 0;
                _warpHold = 0;
                return;
            }

            int skip = (int)m.ReadU32(DrawSkipAddr);
            uint crash = m.ReadU32(CrashPtrAddr);
            if (skip > 0 || (crash & 0xFF000000u) != 0x80000000u)
                return;

            uint type = m.ReadU32(crash);
            if (type is 0 or 2) return;

            _armedThisGpu = true;
            if (_holdLocked == 0)
            {
                _holdLocked = 30;
                PaceLog($"first draw lid={id} crash=0x{crash:X8} — 30 FPS × 30");
            }

            _holdLocked--;
            PaceLog($"hold {_holdLocked}");
            if (_holdLocked > 0) return;

            // Warp_In is longer than 30 presents when NS is still paging the
            // warp anim. Unlock mid-cine land-locks it; wait=1 then never
            // finishes and Crash stays a spawn streak. Stay at 30 Hz until
            // Warp_In ends, or 3 more seconds.
            uint st = m.ReadU32(crash + ObjStateOff);
            if (IsWarpInState(st) && _warpHold < 90)
            {
                _warpHold++;
                PaceLog($"warp hold {_warpHold} st={st}");
                return;
            }

            _levelReady = true;
            _clockArmed = false;
            ResetWaterClock();
            PaceLog($"unlock armed lid={id} crash=0x{crash:X8}");
        }
        catch
        {
            // overlay swap
        }
    }

    static void PaceLog(string msg)
    {
        Console.WriteLine("[FramePacing] " + msg);
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "pacing.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    static void EnsureGfxHook()
    {
        if (_gfxHookTried) return;
        if (Dispatcher.Overlays.Count == 0) return;
        _gfxHookTried = true;
        TryHookGfx(GfxTransformSvtxAddr, "func_80018964");
        TryHookGfx(GfxTransformCvtxAddr, "func_80018A40");
        TryHookNamed(GoolObjectInterpretAddr, "func_800201DC", PreGoolInterpret);
        TryHookNamed(GoolObjectChangeStateAddr, "func_8001D698", PreChangeState);
        try
        {
            HookManager.Commit();
        }
        catch (Exception ex)
        {
            PaceLog($"gfx commit fail {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void TryHookGfx(uint addr, string name)
    {
        MethodInfo? mi = null;
        foreach (var ov in Dispatcher.Overlays.Values)
        {
            if (ov.Functions.TryGetValue(addr, out var fn))
            {
                mi = fn.Method;
                break;
            }
        }
        if (mi == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types ?? Type.EmptyTypes; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t is null) continue;
                    var found = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (found == null) continue;
                    mi = found;
                    break;
                }
                if (mi != null) break;
            }
        }
        if (mi == null)
        {
            PaceLog($"no gfx fn {name} 0x{addr:X8} overlays={Dispatcher.Overlays.Count}");
            return;
        }
        HookManager.AddPre(mi, PreGfxTransformMesh);
        HookManager.AddPost(mi, PostGfxTransformMesh);
        PaceLog($"hook gfx {mi.DeclaringType?.Name}.{mi.Name}");
    }

    static void TryHookNamed(uint addr, string name, Func<CpuContext, IMemory, bool> pre)
    {
        MethodInfo? mi = null;
        foreach (var ov in Dispatcher.Overlays.Values)
        {
            if (ov.Functions.TryGetValue(addr, out var fn))
            {
                mi = fn.Method;
                break;
            }
        }
        if (mi == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types ?? Type.EmptyTypes; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t is null) continue;
                    var found = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (found == null) continue;
                    mi = found;
                    break;
                }
                if (mi != null) break;
            }
        }
        if (mi == null)
        {
            PaceLog($"no fn {name} 0x{addr:X8} overlays={Dispatcher.Overlays.Count}");
            return;
        }
        HookManager.AddPre(mi, pre);
        PaceLog($"hook {mi.DeclaringType?.Name}.{mi.Name}");
    }

    /// <summary>
    /// Bound-before-trans can clear collider when stamps match. Wait is
    /// <c>if (!collider) statetime = frametime</c>; Active CarryCollider
    /// (and Temple 0.985 / RWaOC wall mill) need the same pointer. Rewrite after Bound.
    /// Any lid: Cortex Power discs are the same PoPlC without 70deg rot.
    /// </summary>
    public static bool PreGoolInterpret(CpuContext c, IMemory m)
    {
        if (!IsActive(m)) return true;
        TryFillTemplePlatCollider(m, c.A0);
        return true;
    }

    static void TryFillTemplePlatCollider(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _)
                || (type != GoolTypePoPl && !IsGatedRwaocMover(m, obj, type)))
                return;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & FlagSolidTop) == 0) return;
            uint crash = m.ReadU32(CrashPtrAddr);
            if (crash == 0 || (crash & 0xFF000000u) != 0x80000000u) return;
            if (!CrashStandingOnPlat(m, obj, crash) || CrashLeftRide(m, obj, crash)) return;
            m.WriteU32(obj + ObjColliderOff, crash);
            if (_platColLog >= 8) return;
            _platColLog++;
            uint state = m.ReadU32(obj + ObjStateOff);
            PaceLog($"plat col 0x{obj:X8} st={state} spawn={StatePoPlSpawn} wait={StatePoPlWait} crash=0x{crash:X8}");
        }
        catch
        {
            // object freed
        }
    }

    static bool CrashOnSolidTop(IMemory m, uint obj, uint crash)
    {
        int px = (int)m.ReadU32(obj + ObjTransOff);
        int py = (int)m.ReadU32(obj + ObjTransOff + 4);
        int pz = (int)m.ReadU32(obj + ObjTransOff + 8);
        return CrashOnPlatPos(m, obj, crash, px, py, pz);
    }

    /// <summary>
    /// Logic AABB or the skip-frame visual lerp. Interpret leftover t is ~0
    /// so they match; skip Crash can sit on the drawn pose while trans is Q.
    /// </summary>
    static bool CrashStandingOnPlat(IMemory m, uint obj, uint crash)
    {
        if (CrashOnSolidTop(m, obj, crash)) return true;
        if (!TryGatedSolidVisual(obj, out int vx, out int vy, out int vz))
            return false;
        return CrashOnPlatPos(m, obj, crash, vx, vy, vz);
    }

    static bool CrashOnPlatPos(IMemory m, uint plat, uint crash, int px, int py, int pz)
    {
        int cy = (int)m.ReadU32(crash + ObjTransOff + 4);
        if (cy - py <= -HalfMeter) return false;
        int platTop = py + Math.Max((int)m.ReadU32(plat + ObjBoundOff + 4), (int)m.ReadU32(plat + ObjBoundOff + 16));
        int crashBottom = cy + Math.Min((int)m.ReadU32(crash + ObjBoundOff + 4), (int)m.ReadU32(crash + ObjBoundOff + 16));
        if (crashBottom > platTop + EmbedSlop) return false;
        int cx = (int)m.ReadU32(crash + ObjTransOff);
        int cz = (int)m.ReadU32(crash + ObjTransOff + 8);
        return AabbOverlapXzAt(m, plat, px, pz, crash, cx, cz);
    }

    static bool AabbOverlapXz(IMemory m, uint a, uint b)
    {
        int ax = (int)m.ReadU32(a + ObjTransOff);
        int az = (int)m.ReadU32(a + ObjTransOff + 8);
        int bx = (int)m.ReadU32(b + ObjTransOff);
        int bz = (int)m.ReadU32(b + ObjTransOff + 8);
        return AabbOverlapXzAt(m, a, ax, az, b, bx, bz);
    }

    static bool AabbOverlapXzAt(IMemory m, uint a, int ax, int az, uint b, int bx, int bz)
    {
        int a1x = ax + (int)m.ReadU32(a + ObjBoundOff);
        int a2x = ax + (int)m.ReadU32(a + ObjBoundOff + 12);
        int a1z = az + (int)m.ReadU32(a + ObjBoundOff + 8);
        int a2z = az + (int)m.ReadU32(a + ObjBoundOff + 20);
        int b1x = bx + (int)m.ReadU32(b + ObjBoundOff);
        int b2x = bx + (int)m.ReadU32(b + ObjBoundOff + 12);
        int b1z = bz + (int)m.ReadU32(b + ObjBoundOff + 8);
        int b2z = bz + (int)m.ReadU32(b + ObjBoundOff + 20);
        int aminX = a1x < a2x ? a1x : a2x;
        int amaxX = a1x < a2x ? a2x : a1x;
        int aminZ = a1z < a2z ? a1z : a2z;
        int amaxZ = a1z < a2z ? a2z : a1z;
        int bminX = b1x < b2x ? b1x : b2x;
        int bmaxX = b1x < b2x ? b2x : b1x;
        int bminZ = b1z < b2z ? b1z : b2z;
        int bmaxZ = b1z < b2z ? b2z : b1z;
        return aminX <= bmaxX && amaxX >= bminX && aminZ <= bmaxZ && amaxZ >= bminZ;
    }

}
