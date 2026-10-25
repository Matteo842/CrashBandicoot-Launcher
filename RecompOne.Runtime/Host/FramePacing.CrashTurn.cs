using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>gool_process ang_velocity_x (GOOL angvel); rotate speed per 1024 ticks.</summary>
    const uint ObjAngVelXOff = 0xB0u;
    /// <summary>gool_process target_rot.x (GOOL troty): heading the physics turns to.</summary>
    const uint ObjTargetRotXOff = 0xB4u;
    /// <summary>gool_process rot.x (GOOL roty): heading.</summary>
    const uint ObjHeadingOff = ObjRotOff + 4;
    /// <summary>status_b: GoolObjectPhysics turns rot.x toward target_rot.x.</summary>
    const uint FlagRotY = 0x1u;
    /// <summary>status_b: GoolObjectRotate2 (other rules) instead.</summary>
    const uint FlagRotY2 = 0x80000u;
    /// <summary>status_b: GoolObjectRotate slows down in steps near the target.</summary>
    const uint FlagRotRamp = 0x20000000u;
    /// <summary>
    /// WillC SpinRotSpeed (mem 12): Willy_Spin / Willy_Spin_Air trans and the
    /// stance spin add it to roty every frame, ±40° on the ground, ±60° in air.
    /// </summary>
    const uint ObjSpinRotSpeedOff = ObjMemOff + 12 * 4;
    const int SpinTurnMax = 0x400;
    const uint StateWillySpin = 17;
    const uint StateWillySpinAir = 18;
    /// <summary>Air ControlDir: GoolObjectRotate(target, pad, 0xF00) per 34-tick frame.</summary>
    const int AirTargetStep = 0xF00 * (int)RefTicks / 1024;
    /// <summary>Air ControlDir sets target_rot directly at speed &lt;= this.</summary>
    const int AirTargetSetSpeed = 0x100;
    /// <summary>Ground ControlDir: above this speed a turn past 90° stops Crash instead.</summary>
    const int GroundStopSpeed = 0x100;
    /// <summary>status_b: ControlDir steers from the d-pad.</summary>
    const uint FlagDpadControl = 0x80u;
    /// <summary><c>pads[0].held</c> d-pad bits (move_states index).</summary>
    const uint PadDirMask = 0xF000u;

    static bool _turnPaced;
    static int _turnAngVel;
    static int _turnTargetPre;
    static int _turnHeadingPre;
    static int _turnRotStep;
    static double _turnCodeFrac, _turnRotFrac, _turnAirFrac;
    static int _turnSpeedPre;
    static bool _turnStopArmed, _turnPadMasked;
    static uint _turnStopStart, _turnPadHeld;

    static int WrapAng(int d)
    {
        d &= 0xFFF;
        return d > 0x800 ? d - 0x1000 : d;
    }

    /// <summary>GoolObjectRotate without the ramp, turning at most <paramref name="step"/>.</summary>
    static int RotateToward(int from, int to, int step)
    {
        int d = WrapAng(to - from);
        int abs = Math.Abs(d);
        if (abs < step) return to & 0xFFF;
        if (abs == 0x800) d = -d;
        return (from + (d > 0 ? step : -step)) & 0xFFF;
    }

    static void ClearTurnFractions()
    {
        _turnCodeFrac = 0;
        _turnRotFrac = 0;
        _turnAirFrac = 0;
        _turnStopArmed = false;
    }

    /// <summary>
    /// Crash's physics runs a full 34-tick step every present and the result
    /// used to keep dt/34 of each heading delta. GoolObjectRotate is not a
    /// rate near the end: |delta| below one frame's turn snaps to the target.
    /// Kept per present, that snap became an exponential approach, and code
    /// SETs (Spin_End KeepRot roty = troty, the Intro exit) only moved part
    /// way and then turned at the normal rate: up to 90° behind the PS1 by
    /// game time (logic-parity Track B). Instead the guest turns at dt:
    /// angvel is scaled for this physics call, so the rotate snaps or steps
    /// with dt/34 of a frame's turn, like the PS1 at its 30 Hz frame ends.
    /// Code before physics: the spin add (SpinRotSpeed per frame) keeps
    /// dt/34, any other change (state SETs) is kept whole. Unlocked only,
    /// not on the hog (its own carry) or land-locked states (34-tick).
    /// </summary>
    static void PrepareCrashTurn(IMemory m)
    {
        _turnPaced = false;
        if (!_crashObj || _crashHog || _objScaled) return;
        if (_exactTicks <= 0 || _exactTicks >= RefTicks - 0.01) return;
        try
        {
            uint o = _obj;
            uint statusB = m.ReadU32(o + ObjStatusBOff);
            if ((statusB & (FlagRotY2 | FlagRotRamp)) != 0) return;
            int angVel = (int)m.ReadU32(o + ObjAngVelXOff);
            if (angVel < 0) return;

            int heading = (int)m.ReadU32(o + ObjHeadingOff);
            int code = WrapAng(heading - _ory);
            int spin = WrapAng((int)m.ReadU32(o + ObjSpinRotSpeedOff));
            uint state = m.ReadU32(o + ObjStateOff);
            bool spinAdd = code != 0
                && spin != 0 && Math.Abs(spin) < SpinTurnMax
                && (code == spin || state == StateWillySpin || state == StateWillySpinAir);
            if (spinAdd)
            {
                // A first spin frame can also carry a SET (Intro exit 180°,
                // KeepRot): that part whole, the frame's spin add dt/34.
                double step = spin * _exactTicks / RefTicks + _turnCodeFrac;
                int kept = (int)Math.Truncate(step);
                _turnCodeFrac = step - kept;
                heading = (_ory + WrapAng(code - spin) + kept) & 0xFFF;
                m.WriteU32(o + ObjHeadingOff, (uint)heading);
            }
            else
                _turnCodeFrac = 0;

            _turnRotStep = -1;
            if ((statusB & FlagRotY) != 0)
            {
                // GoolObjectRotate: velocity = angvel × 34 / 1024. Give it an
                // angvel whose 34-tick turn is this present's dt share.
                double turn = (angVel * (int)RefTicks / 1024) * _exactTicks / RefTicks + _turnRotFrac;
                int whole = (int)Math.Truncate(turn);
                _turnRotFrac = turn - whole;
                int scaled = (whole * 1024 + (int)RefTicks - 1) / (int)RefTicks;
                m.WriteU32(o + ObjAngVelXOff, (uint)scaled);
                _turnRotStep = whole;
                int target = (int)m.ReadU32(o + ObjTargetRotXOff);
                if (Math.Abs(WrapAng(target - heading)) < whole)
                    _turnRotFrac = 0;
            }
            else
                _turnRotFrac = 0;

            _turnAngVel = angVel;
            _turnHeadingPre = heading;
            _turnTargetPre = (int)m.ReadU32(o + ObjTargetRotXOff);
            _turnSpeedPre = (int)m.ReadU32(o + ObjSpeedOff);
            _turnPaced = true;
            HoldGroundStop(m);
        }
        catch
        {
            _turnPaced = false;
        }
    }

    static void RestoreCrashTurnSpeed(IMemory m)
    {
        if (!_turnPaced) return;
        try
        {
            m.WriteU32(_obj + ObjAngVelXOff, (uint)_turnAngVel);
            if (_turnPadMasked)
            {
                _turnPadMasked = false;
                m.WriteU32(PadsAddr + 4, _turnPadHeld);
            }
            else
                NoteGroundStop(m);
        }
        catch { /* object freed */ }
    }

    /// <summary>
    /// Ground ControlDir with speed &gt; 0x100 and the d-pad more than 90°
    /// off the target sets speed 0 and leaves the target: Crash stands for
    /// one 30 Hz frame and turns on the next. Here the next present already
    /// turned (taps and reversals 12° ahead at 60 FPS, Walk/Stance early).
    /// Note the stop; <see cref="HoldGroundStop"/> keeps it for the frame.
    /// </summary>
    static void NoteGroundStop(IMemory m)
    {
        uint o = _obj;
        if (_turnSpeedPre <= GroundStopSpeed || (int)m.ReadU32(o + ObjSpeedOff) != 0) return;
        if ((m.ReadU32(o + ObjStatusBOff) & FlagDpadControl) == 0) return;
        if ((m.ReadU32(o + ObjStateFlagsOff) & FlagStateAir) != 0) return;
        if ((m.ReadU32(PadsAddr + 4) & PadDirMask) == 0) return;
        if ((((int)m.ReadU32(o + ObjTargetRotXOff) - _turnTargetPre) & 0xFFF) != 0) return;
        _turnStopArmed = true;
        _turnStopStart = _guestTicks - _frameTicks;
    }

    /// <summary>
    /// Presents whose middle is still inside the PS1 stop frame run physics
    /// without the d-pad (ControlDir decelerates 0 → 0, target and heading
    /// stay). GOOL code before physics still saw the real pad, as the PS1.
    /// </summary>
    static void HoldGroundStop(IMemory m)
    {
        if (!_turnStopArmed) return;
        uint start = _guestTicks - _frameTicks;
        if (start == _turnStopStart) return;
        // Still standing on the ground from the stop (a jump or a code SET
        // of speed in the meantime is its own frame: let it steer).
        if (unchecked(start - _turnStopStart) + _frameTicks / 2.0 >= RefTicks
            || _turnSpeedPre != 0
            || (m.ReadU32(_obj + ObjStateFlagsOff) & FlagStateAir) != 0)
        {
            _turnStopArmed = false;
            return;
        }
        _turnPadHeld = m.ReadU32(PadsAddr + 4);
        m.WriteU32(PadsAddr + 4, _turnPadHeld & ~PadDirMask);
        _turnPadMasked = true;
    }

    /// <summary>
    /// Air ControlDir turns target_rot by GoolObjectRotate(…, 0xF00) at the
    /// 34-tick step every present: 2× the PS1 air steering at 60 FPS, 4× at
    /// 120. Keep dt/34 of that step (snap when the rest is shorter). Direct
    /// sets (slow, or code) stay whole. Next present's velocity uses it.
    /// The heading turned toward the guest's target in the same physics
    /// call; turn it again toward the kept one.
    /// </summary>
    static void FinishCrashAirTarget(IMemory m, int speedTo)
    {
        if (!_turnPaced) return;
        try
        {
            uint o = _obj;
            if ((m.ReadU32(o + ObjStateFlagsOff) & FlagStateAir) == 0 || speedTo <= AirTargetSetSpeed)
            {
                _turnAirFrac = 0;
                return;
            }
            int to = (int)m.ReadU32(o + ObjTargetRotXOff);
            int d = WrapAng(to - _turnTargetPre);
            int abs = Math.Abs(d);
            if (d == 0 || abs > AirTargetStep)
            {
                _turnAirFrac = 0;
                return;
            }
            double step = AirTargetStep * _exactTicks / RefTicks + _turnAirFrac;
            int whole = (int)Math.Truncate(step);
            if (abs < AirTargetStep && abs <= whole)
            {
                _turnAirFrac = 0;
                return;
            }
            _turnAirFrac = step - whole;
            int kept = (_turnTargetPre + (d > 0 ? whole : -whole)) & 0xFFF;
            m.WriteU32(o + ObjTargetRotXOff, (uint)kept);
            if (_turnRotStep >= 0)
                m.WriteU32(o + ObjHeadingOff, (uint)RotateToward(_turnHeadingPre, kept, _turnRotStep));
        }
        catch
        {
            // object freed
        }
    }
}
