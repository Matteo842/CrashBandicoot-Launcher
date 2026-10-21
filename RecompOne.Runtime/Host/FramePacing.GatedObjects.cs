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
    /// Objects already using authored frame steps share the world's phase.
    /// Independent per-object accumulators could update time()-driven paths
    /// twice on one draw_count, then skip the next, producing stop/start motion.
    /// Crash's continuous update is not gated by this clock.
    /// </summary>
    static bool GatedShouldInterpret(IMemory m, uint obj)
    {
        uint frame = CurrentGateFrame(out double phase);
        _simAcc[obj] = phase;
        EvictDictDown(_simAcc, obj, ObjectPoolMax);
        if (_gateFrame.TryGetValue(obj, out uint previous) && previous == frame)
            return false;
        _gateFrame[obj] = frame;
        EvictDictDown(_gateFrame, obj, ObjectPoolMax);
        UnstickGoolWait(m, obj, expire: false);
        return true;
    }

    /// <summary>
    /// A crate hit by SpinHit / Event23 / JumpedOn changes state inside
    /// Crash's update (FIRST_FRAME is set). Original runs that break CODE
    /// (BoxBreak → StackBreak, crate above gets gravity) on the next 30 Hz
    /// frame. Running it on the next present compressed the stack chain
    /// into a few ms while Crash still sends SpinHit every present. Same
    /// object, so keep its gate and AABB — wait for the next gate frame.
    /// </summary>
    static bool IsCrateBreakStep(IMemory m, uint obj)
    {
        if (!_gateFrame.ContainsKey(obj)) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeBox)
                return false;
            uint state = m.ReadU32(obj + ObjStateOff);
            return state is StateBoxBreak or StateBoxBreakReward;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>NTSC-U <c>pads[0]</c>: tapped, held, held_prev, tapped_prev.</summary>
    const uint PadsAddr = 0x8005E71Cu;
    /// <summary>Square | Circle — WillC <c>PAD_SQUARE|PAD_O</c> spin.</summary>
    const uint PadSpinMask = 0xA0u;
    const uint ObjInterrupterOff = 0x7Cu;
    /// <summary>Crate in the air (GRAVITY): wall tick it took off.</summary>
    static readonly Dictionary<uint, uint> _crateFlight = new();
    /// <summary>Inside Crash's GoolObjectUpdate (his trans sends SpinHit).</summary>
    static bool _inCrashUpdate;
    static bool _padSpinMasked;
    static uint _padSaveTapped, _padSaveHeld, _padSaveTappedPrev;

    static bool IsBoxObj(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try { return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypeBox; }
        catch { return false; }
    }

    static bool IsPoObConveyor(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypePoOb
                && m.ReadU32(obj + ObjStateOff) == StatePoObConveyor;
        }
        catch { return false; }
    }

    static bool IsPoObSpring(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypePoOb
                && m.ReadU32(obj + ObjStateOff) == StatePoObSpring;
        }
        catch { return false; }
    }

    static bool IsTurtleShell(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypeTurt
                && m.ReadU32(obj + ObjStateOff) == StateTurtleShell;
        }
        catch { return false; }
    }

    /// <summary>Turtle shell: gate interprets in a row that saw Crash's touch.</summary>
    static readonly Dictionary<uint, int> _turtleTouch = new();

    /// <summary>
    /// Turtle shell bounce (#145). Crash touches the shell on his landing
    /// frame and again on the bounce frame (that floor test sends
    /// EventJumpedOn). Original: two 30 Hz frames, so the shell trans reads
    /// <c>collider</c> twice and squashes twice (scale y 0.9). Unlocked, those
    /// two touches are two presents a few ms apart. When no gate falls
    /// between them the shell reads one touch and squashes only to 0.95.
    /// Every other bounce looked half as deep at 60 FPS. A touch read by a
    /// single gate gets the second read the original gives it.
    /// </summary>
    static void FillTurtleShellTouch(IMemory m, uint obj)
    {
        if (!IsTurtleShell(m, obj) || !TryReadCrash(m, out uint crash)) return;
        try
        {
            if (m.ReadU32(obj + ObjColliderOff) == crash)
            {
                _turtleTouch[obj] = Math.Min(_turtleTouch.GetValueOrDefault(obj) + 1, 2);
                EvictDictDown(_turtleTouch, obj, ObjectPoolMax);
                return;
            }
            if (_turtleTouch.GetValueOrDefault(obj) == 1)
            {
                m.WriteU32(obj + ObjColliderOff, crash);
                _turtleTouch[obj] = 2;
                return;
            }
            _turtleTouch.Remove(obj);
        }
        catch
        {
            // object freed
        }
    }

    static void NoteCrateFlight(IMemory m, uint obj)
    {
        try
        {
            if ((m.ReadU32(obj + ObjStatusBOff) & FlagGravity) == 0)
            {
                _crateFlight.Remove(obj);
                return;
            }
            if (_crateFlight.ContainsKey(obj)) return;
            _crateFlight[obj] = _guestTicks;
            EvictDictDown(_crateFlight, obj, ObjectPoolMax);
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>
    /// BoxCheckSpinEvent: a crate in the air with no BoxBelow uses the
    /// −2 m spin range while Square/Circle is held. That is how the crate
    /// above a spun one falls into range. Original asks it one full frame
    /// after the hit (touch and break CODE are each a 30 Hz frame), so a
    /// normal tap is already released. At high FPS the crate above was asked
    /// ~10 ms after the hit with the button still down (Generator Room:
    /// middle broke, top then landed on Crash's head). Hide the spin bits
    /// from that crate's ESR for its first original frame of flight; a long
    /// hold after that still breaks it, like 30 FPS.
    /// </summary>
    static void MaskSpinForYoungCrate(IMemory m, uint obj)
    {
        if (_padSpinMasked || !_inCrashUpdate || !IsBoxObj(m, obj)) return;
        try
        {
            if (!TryReadCrash(m, out uint crash)
                || m.ReadU32(obj + ObjInterrupterOff) != crash)
                return;
            NoteCrateFlight(m, obj);
            if (!_crateFlight.TryGetValue(obj, out uint takeoff)
                || unchecked(_guestTicks - takeoff) >= RefTicks)
                return;
            _padSaveTapped = m.ReadU32(PadsAddr);
            _padSaveHeld = m.ReadU32(PadsAddr + 4);
            _padSaveTappedPrev = m.ReadU32(PadsAddr + 12);
            if (((_padSaveTapped | _padSaveHeld | _padSaveTappedPrev) & PadSpinMask) == 0)
                return;
            m.WriteU32(PadsAddr, _padSaveTapped & ~PadSpinMask);
            m.WriteU32(PadsAddr + 4, _padSaveHeld & ~PadSpinMask);
            m.WriteU32(PadsAddr + 12, _padSaveTappedPrev & ~PadSpinMask);
            _padSpinMasked = true;
        }
        catch
        {
            // pad / object unmapped
        }
    }

    static void RestorePadSpin(IMemory m)
    {
        if (!_padSpinMasked) return;
        _padSpinMasked = false;
        try
        {
            m.WriteU32(PadsAddr, _padSaveTapped);
            m.WriteU32(PadsAddr + 4, _padSaveHeld);
            m.WriteU32(PadsAddr + 12, _padSaveTappedPrev);
        }
        catch
        {
            // unmapped
        }
    }

    public static void PostGoolInterpret(CpuContext c, IMemory m) => RestorePadSpin(m);

    /// <summary>
    /// CardC save UI (Tawna / Cortex bonus save: memory card, password,
    /// continue). <c>buttonpress</c> tests <c>pads[0].tapped</c>; <c>dirpress</c>
    /// (PAD_N/S/E/W) is a move_states edge of <c>held</c> vs <c>held_prev</c>.
    /// Crash's PadUpdate rewrites both every present, but the menu is gated to
    /// one original interpret per 34 wall ticks, so at 120 FPS most X / △ /
    /// d-pad presses were gone before it ran and saving looked broken. Each
    /// CardC run gets its own 30 Hz pad instead: tapped = every tap since its
    /// last run (a short ring of PadUpdate samples), held_prev / tapped_prev =
    /// what it saw on that run. Restored after its Update. Ring size is a
    /// power of two and covers one gate at the 4000 Hz MinStepSeconds cap.
    /// The island map traveler (IsldC, gated, buttonbuffer X/O) uses the same
    /// latch; see <c>RunSkippedMapPad</c>.
    /// </summary>
    const int CardTapRing = 256;
    static readonly uint[] _cardTaps = new uint[CardTapRing];
    static uint _cardTapSeq;

    struct CardPad
    {
        public uint Seq;
        public uint Held;
        public uint Taps;
    }

    static readonly Dictionary<uint, CardPad> _cardPad = new();
    static bool _cardPadApplied;
    static uint _cardSaveTapped, _cardSaveHeldPrev, _cardSaveTappedPrev;
    static int _cardPadLog;
    /// <summary>This GoolObjectUpdate ran PadUpdate (crash's run or land-lock skip).</summary>
    static bool _padRan;
    /// <summary>Map traveler whose Update is running: latch on its first interpret.</summary>
    static uint _padLatchOwner;

    /// <summary>After crash's PadUpdate: one ring entry per pad sample.</summary>
    static void NoteCardPadTaps(IMemory m)
    {
        try
        {
            uint taps = m.ReadU32(PadsAddr);
            _cardTapSeq++;
            _cardTaps[_cardTapSeq & (CardTapRing - 1)] = taps;
        }
        catch
        {
            // pad unmapped
        }
    }

    /// <summary>
    /// Before a CardC Update that runs (gate or first frame). A newborn starts
    /// with no taps and no d-pad edge: the press that spawned it belongs to
    /// its creator (Ask_Overwrite yes/no would take the X again).
    /// </summary>
    static void ApplyCardTaps(IMemory m, uint obj)
    {
        if (!_haveObj || _crashObj || obj != _obj || !IsCardUi(m, obj)) return;
        ApplyPadLatch(m, obj, owner: false);
    }

    /// <summary>
    /// <paramref name="owner"/>: the gated map traveler, called after its own
    /// PadUpdate (first interpret of its Update). Its fresh tap is not in the
    /// ring yet (sampled at Post), so add it and count that sample as seen.
    /// </summary>
    static void ApplyPadLatch(IMemory m, uint obj, bool owner)
    {
        if (_cardPadApplied) return;
        try
        {
            uint tapped = m.ReadU32(PadsAddr);
            uint held = m.ReadU32(PadsAddr + 4);
            uint heldPrevReal = m.ReadU32(PadsAddr + 8);
            uint tappedPrevReal = m.ReadU32(PadsAddr + 12);
            uint taps = 0, heldPrev = held, tapsPrev = 0;
            if (owner)
            {
                taps = tapped;
                heldPrev = heldPrevReal;
                tapsPrev = tappedPrevReal;
            }
            bool newborn = _newObj && obj == _obj;
            if (!newborn && _cardPad.TryGetValue(obj, out CardPad last))
            {
                int behind = unchecked((int)(_cardTapSeq - last.Seq));
                int n = Math.Clamp(behind, 0, CardTapRing);
                for (int i = 0; i < n; i++)
                    taps |= _cardTaps[unchecked(_cardTapSeq - (uint)i) & (CardTapRing - 1)];
                heldPrev = last.Held;
                tapsPrev = last.Taps;
            }
            uint seen = owner ? unchecked(_cardTapSeq + 1) : _cardTapSeq;
            _cardPad[obj] = new CardPad { Seq = seen, Held = held, Taps = taps };
            EvictDictDown(_cardPad, obj, ObjectPoolMax);
            _cardSaveTapped = tapped;
            _cardSaveHeldPrev = heldPrevReal;
            _cardSaveTappedPrev = tappedPrevReal;
            m.WriteU32(PadsAddr, taps);
            m.WriteU32(PadsAddr + 8, heldPrev);
            m.WriteU32(PadsAddr + 12, tapsPrev);
            _cardPadApplied = true;
            if ((taps != 0 || held != heldPrev) && _cardPadLog < 24)
            {
                _cardPadLog++;
                PaceLog($"card pad obj=0x{obj:X8} st={m.ReadU32(obj + ObjStateOff)} taps=0x{taps:X} held=0x{held:X} prev=0x{heldPrev:X}");
            }
        }
        catch
        {
            // pad / object unmapped
        }
    }

    /// <summary>GAMEFLAGS 0x100000: GpuUpdate runs CardUpdate (memory card I/O).</summary>
    const uint FlagCardIo = 0x100000u;
    static uint _cardIoFrame = uint.MaxValue;
    static bool _cardIoMasked;

    /// <summary>
    /// CardC tests CARDSTATUS once per 30 Hz step; GpuUpdate steps the card
    /// state machine (CardUpdate) once per present. The menu relies on 1:1:
    /// Look_For_Card has to see CHECKING (0x8) for one frame to go through
    /// Check_Formatted, the only state that clears the new-card flag 0x20
    /// (<c>memcardfn(2)</c>). At 1200 FPS the check was already over, so it went
    /// straight to Read_Card, which saw 0x20 and went back to Look_For_Card,
    /// which re-armed detection (<c>memcardfn(10)</c>): "searching / reading
    /// card directory" forever. Run the card I/O on the first GpuUpdate of each
    /// gate frame — after that frame's gated CardC step, as on the PS1 — and
    /// hide the flag from the other presents' GpuUpdate.
    /// </summary>
    static void GateCardIo(IMemory m)
    {
        if (!IsActive(m)) return;
        try
        {
            uint flags = m.ReadU32(DisplayFlagsAddr);
            if ((flags & FlagCardIo) == 0) return;
            uint frame = CurrentGateFrame(out _);
            if (frame != _cardIoFrame)
            {
                _cardIoFrame = frame;
                return;
            }
            m.WriteU32(DisplayFlagsAddr, flags & ~FlagCardIo);
            _cardIoMasked = true;
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreCardIo(IMemory m)
    {
        if (!_cardIoMasked) return;
        _cardIoMasked = false;
        try
        {
            m.WriteU32(DisplayFlagsAddr, m.ReadU32(DisplayFlagsAddr) | FlagCardIo);
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreCardTaps(IMemory m)
    {
        if (!_cardPadApplied) return;
        _cardPadApplied = false;
        try
        {
            m.WriteU32(PadsAddr, _cardSaveTapped);
            m.WriteU32(PadsAddr + 8, _cardSaveHeldPrev);
            m.WriteU32(PadsAddr + 12, _cardSaveTappedPrev);
        }
        catch
        {
            // unmapped
        }
    }

    /// <summary>gool_process.anim_stamp (frames_elapsed of the last Update).</summary>
    const uint ObjAnimStampOff = 0xFCu;
    static bool _crashStampLagged;
    static uint _crashStampSave;

    /// <summary>
    /// GoolObjectUpdate: <c>anim_stamp == crash->anim_stamp</c> takes the
    /// Bound-before-trans path, and that Bound calls GoolCollide which sets
    /// <c>crash->collider</c> to the crate unconditionally. Crates are in an
    /// earlier handle than Crash, so at 30 FPS Crash's stamp is always last
    /// frame's: crates bound in physics (no GoolCollide) and Crash picks the
    /// nearest crate himself in PlotObjWalls. At high FPS Crash already ran
    /// in this 30 Hz frame (and the gate clock can be offset from
    /// frames_elapsed), so the last crate of a stack — the top one — became
    /// his collider. SpinHit then went to the top, each crate forwarded it
    /// down, the bottom accepted, and <c>eventaccepted</c> after the forward
    /// broke all three in one present (Generator Room). Give a running
    /// crate the original view: Crash's stamp is the previous frame.
    /// Heavy Machinery conveyors too (#108): that same-stamp GoolCollide
    /// ignores Crash's state, so the belt kept carrying a burnt Crash (no
    /// COLLIDABLE / TRANS_MOTION, so no walls) through the hot pipe. Original
    /// only gets the belt touch from Crash's own physics (PlotObjWalls), which
    /// a dead Crash no longer runs. It also let two overlapping belts both
    /// push him (double speed); PlotObjWalls keeps only the nearest one.
    /// Heavy Machinery springs (#149): <c>Power_Spring</c> trans bounces
    /// <c>collider</c>, the touch Crash's floor test wrote the frame before.
    /// Same-stamp Bound tests the AABBs instead, and Crash standing on top
    /// does not overlap the spring, so it cleared collider and he stood
    /// on the spring without a bounce in that gate phase.
    /// Turtle shells (#145): <c>Turtle_Shell</c> trans squashes the shell
    /// (<c>scaley -= 0.05</c>) only while <c>collider</c> is Crash above it.
    /// Same-stamp Bound cleared that touch, so the bounce squash was missing
    /// or smaller than at 30 FPS, depending on the gate phase.
    /// </summary>
    static void LagCrashStampForEntity(IMemory m, uint obj)
    {
        if (_crashStampLagged
            || !(IsBoxObj(m, obj) || IsPoObConveyor(m, obj) || IsPoObSpring(m, obj)
                || IsTurtleShell(m, obj))
            || !TryReadCrash(m, out uint crash)) return;
        // Completion screen boxes are the GamOC Willy's children: the PS1 ran
        // them after him, same stamp. Nothing to lag.
        if (IsScreenMain(m, crash)) return;
        try
        {
            uint fe = m.ReadU32(FramesElapsedAddr);
            uint stamp = m.ReadU32(crash + ObjAnimStampOff);
            if (stamp != fe) return;
            _crashStampSave = stamp;
            m.WriteU32(crash + ObjAnimStampOff, unchecked(fe - 1));
            _crashStampLagged = true;
        }
        catch
        {
            // unmapped
        }
    }

    static void RestoreCrashStamp(IMemory m)
    {
        if (!_crashStampLagged) return;
        _crashStampLagged = false;
        if (!TryReadCrash(m, out uint crash)) return;
        try
        {
            m.WriteU32(crash + ObjAnimStampOff, _crashStampSave);
        }
        catch
        {
            // unmapped
        }
    }

    static uint CurrentGateFrame(out double phase)
    {
        uint elapsed = _waterArmed
            ? unchecked(_guestTicks - _worldDrawGuest0) : _guestTicks;
        phase = elapsed % RefTicks + _tickFrac;
        return unchecked((_waterArmed ? _worldDrawBase : 0u) + elapsed / RefTicks);
    }

    static void SeedObjectGate(uint obj)
    {
        _gateFrame[obj] = CurrentGateFrame(out double phase);
        _simAcc[obj] = phase;
    }

    static bool TryGetGatePhase(IMemory m, uint obj, out double phase)
    {
        if (!_simAcc.TryGetValue(obj, out phase)) return false;
        if (GamePaused(m)) return true;
        uint frame = CurrentGateFrame(out double currentPhase);
        // Crash may update before the platform in this traversal. Finish the
        // previous motion segment until the platform consumes the new tick;
        // never rewind its old pose to phase zero at the tick boundary.
        phase = _gateFrame.TryGetValue(obj, out uint previous) && previous != frame
            ? RefTicks : currentPhase;
        return true;
    }

    // A pooled address is not an object identity. Never inherit the previous
    // occupant's collision bounds, poses, wait tags or fractional motion.
    static void ResetObjectPacing(uint obj)
    {
        _lastBound.Remove(obj);
        _animAcc.Remove(obj);
        _spriteSteps.Remove(obj);
        _animHold.Remove(obj);
        _waitHoldTs.Remove(obj);
        _poseClock.Remove(obj);
        _animPose.Remove(obj);
        _gateRot.Remove(obj);
        _spawnCredit.Remove(obj);
        _simAcc.Remove(obj);
        _gateFrame.Remove(obj);
        _crateFlight.Remove(obj);
        _turtleTouch.Remove(obj);
        _pathHoppers.Remove(obj);
        _platFrac.Remove(obj);
        if (_rideObj == obj) ClearGatedRide();
    }

    /// <summary>
    /// playframe: elapsed_since = frames_elapsed - stamp (signed). A rewound
    /// draw_stamp leaves that negative and CODE never leaves wait=1.
    /// </summary>
    static void UnstickGoolWait(IMemory m, uint obj, bool expire)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            uint sp = m.ReadU32(obj + ObjSpOff);
            if ((sp & 0xFF000000u) != 0x80000000u || sp < 4) return;
            uint tagAddr = sp - 4;
            uint top = m.ReadU32(tagAddr);
            uint wait = top >> 24;
            if (wait == 0) return;
            uint fe = m.ReadU32(FramesElapsedAddr) & 0x00FFFFFFu;
            int elapsed = (int)fe - (int)(top & 0x00FFFFFFu);
            if (expire || elapsed < 0)
                m.WriteU32(tagAddr, fe);
        }
        catch
        {
            // stack not mapped
        }
    }

    /// <summary>
    /// Iron crates are walls via <c>PlotObjWalls</c> + SOLID_SIDES. Skipping
    /// GoolObjectUpdate also skips GoolObjectBound, so they vanish from
    /// <c>object_bounds</c> and Crash walks through. Write the AABB only —
    /// do not call Bound (same-stamp GoolCollide kills siblings mid-walk).
    /// </summary>
    static void RegisterGatedBound(IMemory m, uint obj, uint statusB)
    {
        if ((statusB & FlagCollidable) == 0) return;
        if ((statusB & (FlagSolidSides | FlagSolidTop)) == 0) return;
        int n = (int)m.ReadU32(ObjectBoundCountAddr);
        if ((uint)n >= (uint)ObjectBoundMax) return;
        uint slot = ObjectBoundsAddr + (uint)n * 28u;
        bool fromSnap = _lastBound.TryGetValue(obj, out BoundSnap snap);
        if (fromSnap)
        {
            WriteBoundSnap(m, slot, snap);
        }
        else
        {
            int tx = (int)m.ReadU32(obj + ObjTransOff);
            int ty = (int)m.ReadU32(obj + ObjTransOff + 4);
            int tz = (int)m.ReadU32(obj + ObjTransOff + 8);
            m.WriteU32(slot, (uint)(tx + (int)m.ReadU32(obj + ObjBoundOff)));
            m.WriteU32(slot + 4, (uint)(ty + (int)m.ReadU32(obj + ObjBoundOff + 4)));
            m.WriteU32(slot + 8, (uint)(tz + (int)m.ReadU32(obj + ObjBoundOff + 8)));
            m.WriteU32(slot + 12, (uint)(tx + (int)m.ReadU32(obj + ObjBoundOff + 12)));
            m.WriteU32(slot + 16, (uint)(ty + (int)m.ReadU32(obj + ObjBoundOff + 16)));
            m.WriteU32(slot + 20, (uint)(tz + (int)m.ReadU32(obj + ObjBoundOff + 20)));
        }
        TranslateGatedRideBound(m, obj, slot, fromSnap);
        m.WriteU32(slot + 24, obj);
        m.WriteU32(ObjectBoundCountAddr, (uint)(n + 1));
    }

    static void WriteBoundSnap(IMemory m, uint slot, BoundSnap snap)
    {
        m.WriteU32(slot, (uint)snap.X1);
        m.WriteU32(slot + 4, (uint)snap.Y1);
        m.WriteU32(slot + 8, (uint)snap.Z1);
        m.WriteU32(slot + 12, (uint)snap.X2);
        m.WriteU32(slot + 16, (uint)snap.Y2);
        m.WriteU32(slot + 20, (uint)snap.Z2);
    }

    static void SyncRiddenBound(IMemory m)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        if (_rideObj == 0 || !_lastBound.TryGetValue(_rideObj, out BoundSnap snap)) return;
        int count = Math.Clamp((int)m.ReadU32(ObjectBoundCountAddr), 0, ObjectBoundMax);
        for (int i = 0; i < count; i++)
        {
            uint slot = ObjectBoundsAddr + (uint)i * 28u;
            if (m.ReadU32(slot + 24) != _rideObj) continue;
            // Rebuild from the saved unshifted bound, never add a second
            // translation to a bound already published by the draw hook.
            WriteBoundSnap(m, slot, snap);
            TranslateGatedRideBound(m, _rideObj, slot, fromSnap: true);
            return;
        }
    }

    /// <summary>
    /// Copy the AABB GoolObjectBound just wrote. Extra gated frames reuse it
    /// instead of reconstructing col/yaw (that shifted the box and Crash
    /// walked through).
    /// </summary>
    static void CaptureBound(IMemory m, uint obj)
    {
        try
        {
            int n = (int)m.ReadU32(ObjectBoundCountAddr);
            if (n < 0) return;
            if (n > ObjectBoundMax) n = ObjectBoundMax;
            for (int i = n - 1; i >= 0; i--)
            {
                uint slot = ObjectBoundsAddr + (uint)i * 28u;
                if (m.ReadU32(slot + 24) != obj) continue;
                if (_lastBound.Count > 96)
                    EvictDict(_lastBound, obj);
                _lastBound[obj] = new BoundSnap
                {
                    X1 = (int)m.ReadU32(slot),
                    Y1 = (int)m.ReadU32(slot + 4),
                    Z1 = (int)m.ReadU32(slot + 8),
                    X2 = (int)m.ReadU32(slot + 12),
                    Y2 = (int)m.ReadU32(slot + 16),
                    Z2 = (int)m.ReadU32(slot + 20),
                    AtQ = GemBoundAfterTrans(m, obj)
                };
                // Real update frames need the same collision pose as skipped
                // frames. The stored snapshot remains at the authored start.
                TranslateGatedRideBound(m, obj, slot, fromSnap: true);
                return;
            }
            _lastBound.Remove(obj);
        }
        catch
        {
            // object freed / overlay swap
        }
    }

    /// <summary>
    /// Authored-step platforms with a solid top, including river leaves/plants.
    /// Sprite effects and closed plants cannot carry Crash.
    /// </summary>
    static bool IsGatedRideSolid(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (IsRiverRideSurface(m, obj)) return true;
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat)
                || !(IsGatedTempleSolid(m, obj, type) || IsGatedRiverObject(m, type, cat)))
                return false;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & Flag2D) != 0) return false;
            return (b & FlagSolidTop) != 0;
        }
        catch
        {
            return false;
        }
    }

    static bool IsRiverRideSurface(IMemory m, uint obj)
    {
        try
        {
            uint level = m.ReadU32(Catalog.LevelIdAddr);
            if ((level != 15 && level != 24) || !_gateFrame.ContainsKey(obj)) return false;
            if (m.ReadU32(obj) is 0 or 2) return false;
            uint flags = m.ReadU32(obj + ObjStatusBOff);
            if ((flags & (FlagSolidTop | FlagCollidable | Flag2D)) != (FlagSolidTop | FlagCollidable))
                return false;
            // Leaves can be miscellaneous GOOL objects. The actual gated solid
            // top decides whether they carry, not a platform-class whitelist.
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat)) return false;
            return type != GoolTypeBox && cat != GoolCategoryBox && cat != GoolCategoryPlayer;
        }
        catch { return false; }
    }

    static bool TryReadCrash(IMemory m, out uint crash)
    {
        crash = 0;
        try
        {
            crash = m.ReadU32(CrashPtrAddr);
            return crash != 0 && (crash & 0xFF000000u) == 0x80000000u;
        }
        catch
        {
            return false;
        }
    }

    static bool CrashCanRide(IMemory m, uint crash)
    {
        try
        {
            return !IsLandLockedState(m, crash) && !CrashOnWarthog(m, crash);
        }
        catch
        {
            return false;
        }
    }

    static bool CrashLeftRide(IMemory m, uint obj, uint crash)
    {
        if (!CrashCanRide(m, crash)) return true;
        try
        {
            if ((m.ReadU32(obj + ObjStatusBOff) & FlagSolidTop) == 0)
                return true;
            bool airborne = CrashAirborne(m, crash);
            if (airborne && (int)m.ReadU32(crash + ObjVelYOff) > 0)
                return true; // Jump takeoff, not a platform reversal.
            // Grounded on a path plat / gem: stay on the ride (1.9.4 rule).
            // The support test below is for wobbling river leaves; on Temple
            // Ruins PoPlC descending steeply it failed with Crash grounded,
            // dropped the carry mid-ride and the plat slid out from under him.
            if (!airborne && !IsRiverRideSurface(m, obj))
                return false;
            // Touchdown present: still the fall state, bound lifted (#162).
            int slop = LandingSlop(m, crash);
            if (_rideObj == obj && _gateRot.TryGetValue(obj, out GatePose pose))
            {
                // The clock has advanced, but carry has not been applied yet.
                // Test support at the platform pose Crash actually rode last,
                // then move both together. Testing the new pose loses contact
                // on a downward wobble or a change of direction.
                return !CrashOnPlatPos(m, obj, crash,
                    LerpPos(pose.Px, pose.Qx, _ridePhase),
                    LerpPos(pose.Py, pose.Qy, _ridePhase),
                    LerpPos(pose.Pz, pose.Qz, _ridePhase), slop);
            }
            return !CrashStandingOnPlat(m, obj, crash, slop);
        }
        catch
        {
            return true;
        }
    }

    static void ClearGatedRide()
    {
        _rideObj = 0;
        _rideDidSnap = false;
        _rideWasStanding = false;
        _ridePhase = 0;
        _rideRemX = 0;
        _rideRemY = 0;
        _rideRemZ = 0;
        _rideAppX = 0;
        _rideAppY = 0;
        _rideAppZ = 0;
    }

    /// <summary>Ride carry Reset dropped before it was applied (FPS switch).</summary>
    static int _resetCarryX, _resetCarryY, _resetCarryZ;
    static bool _resetCarryPending;

    /// <summary>
    /// Reset (FPS hotkey / combo) dropped the ride mid-step. The plat already
    /// sits at the end of its 30 Hz step and the next loop is native, so the
    /// part of that carry not yet spread was lost: Crash slid back a little on
    /// every switch until he fell off the A→B discs. Keep it for the next loop.
    /// Host state only — Android resets from the UI thread.
    /// </summary>
    static void StashRideForReset()
    {
        if (_rideObj == 0) return;
        _resetCarryX += (int)Math.Round(_rideRemX) - _rideAppX;
        _resetCarryY += (int)Math.Round(_rideRemY) - _rideAppY;
        _resetCarryZ += (int)Math.Round(_rideRemZ) - _rideAppZ;
        _resetCarryPending = true;
    }

    /// <summary>
    /// Game thread, before the object tree: the plat's next step (native or
    /// the first unlocked interpret) starts from where its last CarryCollider
    /// put Crash. Not in a jump, and not while the pause menu holds the plat.
    /// </summary>
    static void ApplyResetCarry(IMemory m)
    {
        if (!_resetCarryPending || GamePaused(m)) return;
        int x = _resetCarryX, y = _resetCarryY, z = _resetCarryZ;
        DropResetCarry();
        if (!TryReadCrash(m, out uint crash) || !CrashCanRide(m, crash) || CrashAirborne(m, crash))
            return;
        try
        {
            m.WriteU32(crash + ObjTransOff, (uint)((int)m.ReadU32(crash + ObjTransOff) + x));
            m.WriteU32(crash + ObjTransOff + 4, (uint)((int)m.ReadU32(crash + ObjTransOff + 4) + y));
            m.WriteU32(crash + ObjTransOff + 8, (uint)((int)m.ReadU32(crash + ObjTransOff + 8) + z));
            PaceLog($"reset carry {x},{y},{z}");
        }
        catch
        {
            // object freed
        }
    }

    static void DropResetCarry()
    {
        _resetCarryPending = false;
        _resetCarryX = 0;
        _resetCarryY = 0;
        _resetCarryZ = 0;
    }

    static void SnapshotGatedCarry(IMemory m, uint obj)
    {
        _rideDidSnap = false;
        _rideWasStanding = false;
        if (!IsGatedRideSolid(m, obj) || !TryReadCrash(m, out uint crash))
            return;
        try
        {
            _rideSnapX = (int)m.ReadU32(crash + ObjTransOff);
            _rideSnapY = (int)m.ReadU32(crash + ObjTransOff + 4);
            _rideSnapZ = (int)m.ReadU32(crash + ObjTransOff + 8);
            _rideWasStanding = !CrashLeftRide(m, obj, crash);
            _rideDidSnap = true;
        }
        catch
        {
            _rideDidSnap = false;
        }
    }

    /// <summary>
    /// Finish the last 30 Hz carry before a new interpret so CarryCollider
    /// sees Crash at the end of the previous step, not still mid-lerp.
    /// Must snap to Q here: <c>_simAcc</c> is already the next leftover.
    /// </summary>
    static void FlushGatedRide(IMemory m, uint obj)
    {
        if (_rideObj != obj) return;
        if (!TryReadCrash(m, out uint crash) || CrashLeftRide(m, obj, crash))
        {
            ClearGatedRide();
            return;
        }
        ApplyRideTarget(m, _rideRemX, _rideRemY, _rideRemZ);
        _ridePhase = 1;
        _rideDidSnap = false;
    }

    /// <summary>
    /// Trans already wrote the full 30 Hz carry (including 0.985). Undo it
    /// and add rem × (acc/34) — the same leftover clock as the drawn lerp
    /// and skip AABB. Spreading rem over 34 ticks from this present's dt
    /// put Crash ahead of the floor (leftover t is 0..dt, not dt). At 30
    /// FPS dt is 34 so this is a no-op and CarryCollider stays original.
    /// </summary>
    static void PaceGatedCarry(IMemory m, uint obj)
    {
        if (!_rideDidSnap)
            return;
        _rideDidSnap = false;
        if (!IsGatedRideSolid(m, obj) || !TryReadCrash(m, out uint crash))
        {
            if (_rideObj == obj)
                ClearGatedRide();
            return;
        }
        try
        {
            bool left = !CrashCanRide(m, crash)
                || (m.ReadU32(obj + ObjStatusBOff) & FlagSolidTop) == 0
                || (CrashAirborne(m, crash) && (int)m.ReadU32(crash + ObjVelYOff) > 0);
            if (left || (!_rideWasStanding && !CrashStandingOnPlat(m, obj, crash)))
            {
                if (_rideWasStanding)
                {
                    m.WriteU32(crash + ObjTransOff, (uint)_rideSnapX);
                    m.WriteU32(crash + ObjTransOff + 4, (uint)_rideSnapY);
                    m.WriteU32(crash + ObjTransOff + 8, (uint)_rideSnapZ);
                }
                if (_rideObj == obj) ClearGatedRide();
                return;
            }
            if (_exactTicks >= RefTicks - 0.01)
            {
                if (_rideObj == obj)
                    ClearGatedRide();
                return;
            }
            int nx = (int)m.ReadU32(crash + ObjTransOff);
            int ny = (int)m.ReadU32(crash + ObjTransOff + 4);
            int nz = (int)m.ReadU32(crash + ObjTransOff + 8);
            long dx = (long)nx - _rideSnapX;
            long dy = (long)ny - _rideSnapY;
            long dz = (long)nz - _rideSnapZ;
            if (_rideWasStanding && IsRiverRideSurface(m, obj)
                && _gateRot.TryGetValue(obj, out GatePose riverPose))
            {
                // Carry exactly the same translation as the moving support.
                // Reusing the guest's independently corrected player delta
                // lets the old direction survive a leaf's wobble reversal.
                dx = (long)riverPose.Qx - riverPose.Px;
                dy = (long)riverPose.Qy - riverPose.Py;
                dz = (long)riverPose.Qz - riverPose.Pz;
            }
            if (dx > VelTeleport || dx < -VelTeleport
                || dy > VelTeleport || dy < -VelTeleport
                || dz > VelTeleport || dz < -VelTeleport)
            {
                ClearGatedRide();
                return;
            }
            if (dx == 0 && dy == 0 && dz == 0
                && !TryGatedPlatDelta(m, obj, crash, out dx, out dy, out dz))
            {
                if (_rideObj == obj)
                    ClearGatedRide();
                return;
            }
            // Undo the 30 Hz GOOL write. Skip presents add rem×(acc/34)
            // so extra StopAtWalls cannot eat a frozen-AABB remainder.
            m.WriteU32(crash + ObjTransOff, (uint)_rideSnapX);
            m.WriteU32(crash + ObjTransOff + 4, (uint)_rideSnapY);
            m.WriteU32(crash + ObjTransOff + 8, (uint)_rideSnapZ);
            _rideObj = obj;
            _rideRemX = dx;
            _rideRemY = dy;
            _rideRemZ = dz;
            _rideAppX = 0;
            _rideAppY = 0;
            _rideAppZ = 0;
            _ridePhase = 0;
            FollowGatedRideAcc(m);
            if (_rideLog < 8)
            {
                _rideLog++;
                PaceLog($"ride 0x{obj:X8} d={dx},{dz} dt={_exactTicks:0.00}");
            }
        }
        catch
        {
            ClearGatedRide();
        }
    }

    /// <summary>
    /// Path delta when CarryCollider did not run (collider cleared). Crash is
    /// still at the snapshot; GatePose P→Q is the 30 Hz step just captured.
    /// </summary>
    static bool TryGatedPlatDelta(IMemory m, uint obj, uint crash,
        out long dx, out long dy, out long dz)
    {
        dx = 0;
        dy = 0;
        dz = 0;
        if (!_gateRot.TryGetValue(obj, out GatePose g)) return false;
        bool onPlat = CrashOnPlatPos(m, obj, crash, g.Px, g.Py, g.Pz);
        if (!onPlat)
        {
            try
            {
                onPlat = m.ReadU32(obj + ObjColliderOff) == crash;
            }
            catch
            {
                return false;
            }
        }
        if (!onPlat) return false;
        dx = (long)g.Qx - g.Px;
        dy = (long)g.Qy - g.Py;
        dz = (long)g.Qz - g.Pz;
        if (dx == 0 && dy == 0 && dz == 0) return false;
        if (dx > VelTeleport || dx < -VelTeleport
            || dy > VelTeleport || dy < -VelTeleport
            || dz > VelTeleport || dz < -VelTeleport)
            return false;
        return true;
    }

    /// <summary>
    /// Original CarryCollider is collider==crash (standing). Spreading that
    /// through jump states glued Crash to the disc in the air — the tight
    /// third ferry jump is then impossible at uncapped, and the mesh (acc
    /// already += dt) buzzes against Crash (still on last present's t).
    /// Apply after the whole object tree so skip acc matches the drawn lerp.
    /// </summary>
    static void RideAfterCrash(IMemory m)
    {
        if (!IsActive(m) || GamePaused(m)) return;
        if (!TryReadCrash(m, out uint crash)) return;
        if (_rideObj == 0)
            TryAttachRiverRide(m, crash);
        if (_rideObj == 0)
            TryAttachPathPlatRide(m, crash);
        if (_rideObj == 0) return;
        if (CrashLeftRide(m, _rideObj, crash))
        {
            ClearGatedRide();
            return;
        }
        FollowGatedRideAcc(m);
    }

    static void TryAttachRiverRide(IMemory m, uint crash)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        uint level = m.ReadU32(Catalog.LevelIdAddr);
        if (level != 15 && level != 24) return;
        if (!CrashCanRide(m, crash) || (int)m.ReadU32(crash + ObjVelYOff) > 0
            || (m.ReadU32(crash + ObjStatusAOff) & FlagGroundLand) == 0) return;
        uint chosen = 0;
        double phase = 0;
        long closest = long.MaxValue;
        GatePose support = default;
        foreach (var pair in _gateRot)
        {
            uint obj = pair.Key;
            if (!IsRiverRideSurface(m, obj) || !TryGetGatePhase(m, obj, out double ticks)) continue;
            double t = Math.Clamp(ticks / RefTicks, 0, 1);
            GatePose pose = pair.Value;
            int px = LerpPos(pose.Px, pose.Qx, t);
            int py = LerpPos(pose.Py, pose.Qy, t);
            int pz = LerpPos(pose.Pz, pose.Qz, t);
            if (!CrashOnPlatPos(m, obj, crash, px, py, pz)) continue;
            long top = py + Math.Max((int)m.ReadU32(obj + ObjBoundOff + 4),
                (int)m.ReadU32(obj + ObjBoundOff + 16));
            if (_lastBound.TryGetValue(obj, out BoundSnap bound))
                top = Math.Max(bound.Y1, bound.Y2) + (long)py - pose.Py;
            long bottom = (int)m.ReadU32(crash + ObjTransOff + 4)
                + (long)Math.Min((int)m.ReadU32(crash + ObjBoundOff + 4),
                    (int)m.ReadU32(crash + ObjBoundOff + 16));
            long gap = Math.Abs(bottom - top);
            if (gap > EmbedSlop || gap >= closest) continue;
            if (Math.Abs((long)pose.Qx - pose.Px) > VelTeleport
                || Math.Abs((long)pose.Qy - pose.Py) > VelTeleport
                || Math.Abs((long)pose.Qz - pose.Pz) > VelTeleport) continue;
            chosen = obj;
            phase = t;
            support = pose;
            closest = gap;
        }
        if (chosen == 0) return;
        _rideObj = chosen;
        _rideRemX = (long)support.Qx - support.Px;
        _rideRemY = (long)support.Qy - support.Py;
        _rideRemZ = (long)support.Qz - support.Pz;
        // Landing can happen on any present. Start at the contact's current
        // phase; do not replay the part of this platform step before landing.
        _rideAppX = (int)Math.Round(_rideRemX * phase);
        _rideAppY = (int)Math.Round(_rideRemY * phase);
        _rideAppZ = (int)Math.Round(_rideRemZ * phase);
        _ridePhase = phase;
    }

    /// <summary>
    /// #162: Crash lands on the drawn path plat partway through its 30 Hz
    /// step, but the carry started only at the plat's next Update. Until then
    /// the plat slid on under a standing Crash, up to one step (36 units on
    /// the Rolling Stones pillars); 30 FPS carries him from the next frame,
    /// with no slide. Join the ride at touchdown with the rest of the current
    /// step, like river leaves. PoPlC Active/Auto only; Temple Ruins / Jaws
    /// plats also turn Crash, which this translation alone would not do.
    /// </summary>
    static void TryAttachPathPlatRide(IMemory m, uint crash)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        try
        {
            uint level = m.ReadU32(Catalog.LevelIdAddr);
            if (level == LidTempleRuins || level == LidJawsOfDarkness) return;
            // GROUNDLAND, not CrashAirborne: the touchdown present is still in
            // the fall state, and waiting one present lost that much carry.
            if (!CrashCanRide(m, crash) || (int)m.ReadU32(crash + ObjVelYOff) > 0
                || (m.ReadU32(crash + ObjStatusAOff) & FlagGroundLand) == 0) return;
            // Landing squash: bound bottom above the feet (see CrashLandingOnPlat).
            int slop = LandingSlop(m, crash);
            foreach (var pair in _gateRot)
            {
                uint obj = pair.Key;
                if ((obj & 0xFF000000u) != 0x80000000u || m.ReadU32(obj) is 0 or 2) continue;
                if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypePoPl) continue;
                uint state = m.ReadU32(obj + ObjStateOff);
                if (state != StatePoPlActive && state != StatePoPlAuto) continue;
                if (!IsGatedRideSolid(m, obj)) continue;
                GatePose pose = pair.Value;
                long dx = (long)pose.Qx - pose.Px;
                long dy = (long)pose.Qy - pose.Py;
                long dz = (long)pose.Qz - pose.Pz;
                if (dx == 0 && dy == 0 && dz == 0) continue;
                if (Math.Abs(dx) > VelTeleport || Math.Abs(dy) > VelTeleport
                    || Math.Abs(dz) > VelTeleport) continue;
                if (!TryGetGatePhase(m, obj, out double ticks)) continue;
                double t = Math.Clamp(ticks / RefTicks, 0, 1);
                int py = LerpPos(pose.Py, pose.Qy, t);
                if (!CrashOnPlatPos(m, obj, crash, LerpPos(pose.Px, pose.Qx, t),
                        py, LerpPos(pose.Pz, pose.Qz, t), slop))
                    continue;
                // Feet (trans y: the floor test rests it on the top) on the
                // top, not beside or under the plat.
                if (!_lastBound.TryGetValue(obj, out BoundSnap bound)) continue;
                long top = Math.Max(bound.Y1, bound.Y2) + (long)py - pose.Py;
                long feet = (int)m.ReadU32(crash + ObjTransOff + 4);
                // The snap is at P or Q depending on the gate phase: one step.
                if (Math.Abs(feet - top) > EmbedSlop + Math.Abs(dy)) continue;
                _rideObj = obj;
                _rideRemX = dx;
                _rideRemY = dy;
                _rideRemZ = dz;
                // Crash already stands on this phase's pose: carry only the rest.
                _rideAppX = (int)Math.Round(dx * t);
                _rideAppY = (int)Math.Round(dy * t);
                _rideAppZ = (int)Math.Round(dz * t);
                _ridePhase = t;
                if (_rideLog < 8)
                {
                    _rideLog++;
                    PaceLog($"ride join 0x{obj:X8} t={t:0.00} d={dx},{dz}");
                }
                return;
            }
        }
        catch
        {
            // object freed / overlay swap
        }
    }

    /// <summary>
    /// Same t as <see cref="ApplyGatedDisplayPose"/> / skip AABB.
    /// </summary>
    static void FollowGatedRideAcc(IMemory m)
    {
        if (_rideObj == 0) return;
        if (!TryGetGatePhase(m, _rideObj, out double acc))
            return;
        double t = acc / RefTicks;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        ApplyRideTarget(m, _rideRemX * t, _rideRemY * t, _rideRemZ * t);
        _ridePhase = t;
    }

    static void ApplyRideTarget(IMemory m, double tx, double ty, double tz)
    {
        if (_rideObj == 0) return;
        if (!TryReadCrash(m, out uint crash) || !CrashCanRide(m, crash))
        {
            ClearGatedRide();
            return;
        }
        try
        {
            int ix = (int)Math.Round(tx) - _rideAppX;
            int iy = (int)Math.Round(ty) - _rideAppY;
            int iz = (int)Math.Round(tz) - _rideAppZ;
            _rideAppX += ix;
            _rideAppY += iy;
            _rideAppZ += iz;
            if (ix != 0)
                m.WriteU32(crash + ObjTransOff, (uint)((int)m.ReadU32(crash + ObjTransOff) + ix));
            if (iy != 0)
                m.WriteU32(crash + ObjTransOff + 4, (uint)((int)m.ReadU32(crash + ObjTransOff + 4) + iy));
            if (iz != 0)
                m.WriteU32(crash + ObjTransOff + 8, (uint)((int)m.ReadU32(crash + ObjTransOff + 8) + iz));
        }
        catch
        {
            ClearGatedRide();
        }
    }

    static bool TryGatedSolidVisual(IMemory m, uint obj, out int vx, out int vy, out int vz)
    {
        vx = 0;
        vy = 0;
        vz = 0;
        if (!_gateRot.TryGetValue(obj, out GatePose g))
            return false;
        if (!TryGetGatePhase(m, obj, out double acc))
            return false;
        double t = acc / RefTicks;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        vx = LerpPos(g.Px, g.Qx, t);
        vy = LerpPos(g.Py, g.Qy, t);
        vz = LerpPos(g.Pz, g.Qz, t);
        return true;
    }

    /// <summary>
    /// Bound ran before trans, so the snap is at GatePose P. Shift it to the
    /// display lerp so Crash's extra StopAtWalls sees the moving floor.
    /// </summary>
    static void TranslateGatedRideBound(IMemory m, uint obj, uint slot, bool fromSnap)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        if (!IsGatedRideSolid(m, obj)) return;
        if (!TryGatedSolidVisual(m, obj, out int vx, out int vy, out int vz))
            return;
        if (!_gateRot.TryGetValue(obj, out GatePose g)) return;
        int bx, by, bz;
        if (fromSnap && GemSnapAtQ(obj))
        {
            bx = g.Qx;
            by = g.Qy;
            bz = g.Qz;
        }
        else if (fromSnap)
        {
            bx = g.Px;
            by = g.Py;
            bz = g.Pz;
        }
        else
        {
            bx = (int)m.ReadU32(obj + ObjTransOff);
            by = (int)m.ReadU32(obj + ObjTransOff + 4);
            bz = (int)m.ReadU32(obj + ObjTransOff + 8);
        }
        int dx = vx - bx;
        int dy = vy - by;
        int dz = vz - bz;
        if (dx == 0 && dy == 0 && dz == 0) return;
        m.WriteU32(slot, (uint)((int)m.ReadU32(slot) + dx));
        m.WriteU32(slot + 4, (uint)((int)m.ReadU32(slot + 4) + dy));
        m.WriteU32(slot + 8, (uint)((int)m.ReadU32(slot + 8) + dz));
        m.WriteU32(slot + 12, (uint)((int)m.ReadU32(slot + 12) + dx));
        m.WriteU32(slot + 16, (uint)((int)m.ReadU32(slot + 16) + dy));
        m.WriteU32(slot + 20, (uint)((int)m.ReadU32(slot + 20) + dz));
    }

    /// <summary>
    /// GemsC (#146): GoolObjectUpdate runs Bound before trans only when the
    /// stamp it just wrote equals Crash's (Crash updates after entities, so
    /// his stamp is still the one it compared). Otherwise Bound ran in
    /// physics, after trans, and the box is at Q, not P.
    /// </summary>
    static bool GemBoundAfterTrans(IMemory m, uint obj)
    {
        if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeGems)
            return false;
        if (!TryReadCrash(m, out uint crash)) return false;
        return m.ReadU32(obj + ObjAnimStampOff) != m.ReadU32(crash + ObjAnimStampOff);
    }

    /// <summary>
    /// Shifting a Q box by visual − P put the gem's ride solid one 30 Hz step
    /// ahead: Crash climbed above the lift, lost the floor when its step
    /// shrank, and the airborne interpret sent <c>pathprog</c> back (#146).
    /// </summary>
    static bool GemSnapAtQ(uint obj)
        => _lastBound.TryGetValue(obj, out BoundSnap snap) && snap.AtQ;

    static void LogObjectClass(IMemory m, uint obj)
    {
        if (!_haveObj || _crashObj) return;
        try
        {
            uint b = m.ReadU32(obj + ObjStatusBOff);
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            bool tnt = type == GoolTypeBox || type == GoolTypeRooO;
            bool plat = cat == GoolCategoryPlatform;
            if (!tnt && !plat && _objClassLog >= 8) return;
            if ((tnt || plat) && _objClassLog >= 24) return;
            _objClassLog++;
            PaceLog($"obj 0x{obj:X8} b=0x{b:X} type={type} cat=0x{cat:X} solid={_solidObj} plat={_platObj} hop={_pathHoppers.Contains(obj)}");
        }
        catch
        {
            // object freed
        }
    }

    static bool IsFirstFrame(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return (m.ReadU32(obj + ObjStatusAOff) & FlagFirstFrame) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Until its first Update, a new object holds its parent's trans/rot
    /// (spawn()) or a stale pool slot's; it was never drawn there. Original
    /// shows it first at the post-CODE pose. FatsC barrel (#94): lerping from
    /// Fat's feet to the throw point drew it at his chest for one present.
    /// </summary>
    static void NoteNewObject(CpuContext c)
    {
        if (c.RA != InitChangeStateRa) return;
        if (_newObjs.Count > ObjectPoolMax) _newObjs.Clear();
        _newObjs.Add(c.A0);
    }

    static void ArmSpawnBurst(IMemory m, uint obj)
    {
        _spawnBurst = IsFirstFrame(m, obj);
        _spawnFirstFrame = _spawnBurst;
        if (_spawnBurst || (obj & 0xFF000000u) != 0x80000000u) return;
        _spawnCredit.TryGetValue(obj, out double credit);
        // A gated run is one whole original step (ArmSpawnBurst only sees the
        // runs). Present dt left WarpC Warp_Out_Used, which spawns a sparkle
        // every trans, at 1 per 40 steps at 1200 FPS.
        credit += _solidObj ? RefTicks : _exactTicks;
        if (credit >= RefTicks)
            _spawnBurst = true;
        EvictDictDown(_spawnCredit, obj, 96);
        _spawnCredit[obj] = credit;
    }

    static void NoteSpawnUsed()
    {
        if (!_didSpawn || !_spawnBurst || _crashObj || !_haveObj || _spawnFirstFrame) return;
        if (_spawnCredit.TryGetValue(_obj, out double credit))
        {
            credit -= RefTicks;
            if (credit < 0) credit = 0;
            _spawnCredit[_obj] = credit;
        }
    }

    /// <summary>
    /// Trans runs every display frame. spawn() in trans at 270 Hz fills the
    /// 96-slot pool (GoolObjectAlloc then steals expendables) and overflows
    /// world-clip scratchpad. Keep GOOL creates at one 30 Hz burst.
    /// </summary>
    static bool PreObjectCreate(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || !_haveObj || _crashObj) return true;
        if (c.A1 == 0 && c.A2 == 0) return true;
        if (IsFirstFrame(m, _obj))
        {
            _didSpawn = true;
            return true;
        }
        if (TryReadGoolClass(m, _obj, out uint ptype, out _)
            && ptype is GoolTypeBox or GoolTypeBono or GoolTypeCard)
        {
            _didSpawn = true;
            return true;
        }
        if (!_spawnBurst || _spawnBudget <= 0)
        {
            c.V0 = ErrorObjectPoolFull;
            return false;
        }
        _spawnBudget--;
        _didSpawn = true;
        return true;
    }

    /// <summary>
    /// DispC (lives, wumpa, Tawna tokens, pause). Not every FLAG_2D sprite —
    /// PoRoC mist and RuiOC flames use that bit in the world.
    /// </summary>
    static bool IsHud(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat))
                return false;
            return type == GoolTypeDisp || cat == GoolCategoryHud;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// GoolObjectUpdate decrements anim_counter once per display frame.
    /// Add one back until 34 wall ticks pass so stall lasts 30 Hz, not 300 Hz.
    /// Death / Warp_In skip extra Updates — do not call this there.
    /// </summary>
    static void HoldCrashStall(IMemory m, uint obj)
    {
        try
        {
            if ((m.ReadU32(obj + ObjStatusBOff) & FlagStall) == 0) return;
            uint n = m.ReadU32(obj + ObjAnimCounterOff);
            if (n == 0) return;
            _stallFrac += _exactTicks;
            if (_stallFrac < RefTicks)
                m.WriteU32(obj + ObjAnimCounterOff, n + 1);
            else
                _stallFrac -= RefTicks;
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>
    /// Trans ChangeAnim does not suspend (no SUSPEND_ON_ANIM), so anim_frame
    /// still advances every present even when the code wait tag is held.
    /// Keep seq/frame at the last committed pose until 33 ms of wall time
    /// pass. Reverse is the look ping-pong, not a cut — hold it too.
    /// Gated solids already step at 30 Hz; holding them again made
    /// CaptureGateRot see from==to so the mesh never lerped.
    /// </summary>
    static void HoldAnimPose(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        if (IsHud(m, obj)) return;
        if (GamePaused(m)) return;
        if (IsRigidWorldPlat(m, obj)) return;
        if (obj == _obj && _solidObj) return;
        // Crash look is CODE playanim wait=0 (stance 14↔19). HoldAnimWait
        // already steps that like gated TNT: one ChangeAnim per 33 ms wall.
        // Holding anim_frame again freezes idx so WallAnimFrac sees from==to
        // for the whole wait — 30 Hz snaps, no morph. Leave the new frame
        // and lerp previous→current on the Gfx pointer.
        if (obj == _obj && _crashObj)
        {
            if (_animPose.TryGetValue(obj, out AnimPose freeze) && freeze.Holding)
            {
                freeze.Holding = false;
                _animPose[obj] = freeze;
            }
            return;
        }
        try
        {
            uint type = m.ReadU32(obj);
            if (type is 0 or 2) return;
            uint seq = m.ReadU32(obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return;
            if (m.ReadU8(seq) != AnimTypeVtx) return;
            int frame = (int)m.ReadU32(obj + ObjAnimFrameOff);
            long now = Stopwatch.GetTimestamp();
            if (_exactTicks >= RefTicks - 0.01)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            if (!_animPose.TryGetValue(obj, out AnimPose p) || !p.Have)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            if (seq != p.Seq)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            int a = frame >> 8;
            int b = p.Frame >> 8;
            if (a == b)
            {
                if (!p.Holding) return;
                double holdSec = (now - p.Ts) / (double)Stopwatch.Frequency;
                if (holdSec < HitchSeconds) return;
                long holdTick = (long)(HitchSeconds * Stopwatch.Frequency);
                long holdTs = p.Ts + holdTick;
                if (now - holdTs > holdTick)
                    holdTs = now;
                m.WriteU32(obj + ObjAnimFrameOff, (uint)p.Target);
                CommitAnimPose(obj, seq, p.Target, p.LastStep, holdTs);
                return;
            }
            int step = a - b;
            int d = step < 0 ? -step : step;
            if (d > 2)
            {
                CommitAnimPose(obj, seq, frame, step, now);
                return;
            }
            double sec = (now - p.Ts) / (double)Stopwatch.Frequency;
            if (sec < 0) sec = 0;
            if (sec < HitchSeconds)
            {
                m.WriteU32(obj + ObjAnimSeqOff, p.Seq);
                m.WriteU32(obj + ObjAnimFrameOff, (uint)p.Frame);
                p.Target = frame;
                p.LastStep = step;
                p.Holding = true;
                _animPose[obj] = p;
                if (_poseHoldLog < 8)
                {
                    _poseHoldLog++;
                    bool crash = false;
                    try { crash = obj == m.ReadU32(CrashPtrAddr); } catch { /* */ }
                    PaceLog($"pose hold obj=0x{obj:X8} crash={crash} {b}->{a} sec={sec:0.000}");
                }
                return;
            }
            long tick = (long)(HitchSeconds * Stopwatch.Frequency);
            long ts = p.Ts + tick;
            if (now - ts > tick)
                ts = now;
            CommitAnimPose(obj, seq, frame, step, ts);
        }
        catch
        {
            // object freed
        }
    }

    static void CommitAnimPose(uint obj, uint seq, int frame, int step, long ts)
    {
        _animPose[obj] = new AnimPose
        {
            Seq = seq,
            Frame = frame,
            Target = frame,
            LastStep = step,
            Ts = ts,
            Have = true,
            Holding = false
        };
        int idx = frame >> 8;
        int from = idx;
        if (_poseClock.TryGetValue(obj, out PoseClock p) && p.Sequence == seq)
            from = p.Idx;
        long behind = (long)(HitchSeconds * Stopwatch.Frequency);
        _poseClock[obj] = new PoseClock { Sequence = seq, Idx = idx, From = from, Ts = ts - behind };
    }

    /// <summary>
    /// wait=0 is next Update. Hold it for one original 33 ms of wall time
    /// so 300 FPS and 500 FPS take the same pose step. A present that is
    /// already a full 34-tick step does not hold (that is the original
    /// 30 Hz loop, not a 60/120 table).
    /// </summary>
    static void HoldAnimWait(IMemory m, uint obj)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        if (IsFirstFrame(m, obj))
        {
            _animAcc[obj] = 0;
            _animHold.Remove(obj);
            _waitHoldTs.Remove(obj);
            return;
        }
        try
        {
            uint sp = m.ReadU32(obj + ObjSpOff);
            if ((sp & 0xFF000000u) != 0x80000000u || sp < 4) return;
            uint tagAddr = sp - 4;
            uint top = m.ReadU32(tagAddr);
            uint wait = top >> 24;
            if (wait > 1)
            {
                _animHold.Remove(obj);
                _waitHoldTs.Remove(obj);
                return;
            }
            uint fe = m.ReadU32(FramesElapsedAddr) & 0x00FFFFFFu;
            bool holding = _waitHoldTs.ContainsKey(obj) || _animHold.Contains(obj);
            if (wait == 1 && !holding) return;
            if (_waitHoldTs.Count > 128)
            {
                _waitHoldTs.Clear();
                _animHold.Clear();
                _animAcc.Clear();
            }
            long now = Stopwatch.GetTimestamp();
            if (!_waitHoldTs.TryGetValue(obj, out long start))
            {
                _waitHoldTs[obj] = now;
                start = now;
            }
            double sec = (now - start) / (double)Stopwatch.Frequency;
            if (sec < HitchSeconds)
            {
                _animAcc[obj] = sec * TicksPerSecond;
                _animHold.Add(obj);
                m.WriteU32(tagAddr, 0x01000000u | fe);
                return;
            }
            _waitHoldTs.Remove(obj);
            _animHold.Remove(obj);
            _animAcc[obj] = 0;
            m.WriteU32(tagAddr, fe);
        }
        catch
        {
            // stack not mapped
        }
    }

}
