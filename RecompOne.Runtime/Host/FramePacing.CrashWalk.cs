using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    const uint GameStatePlaying = 0x100u;

    /// <summary>Last whole-frame speed change of the walk ramp (signed), see <see cref="PaceCrashWalkAccel"/>.</summary>
    static int _walkAccel;

    /// <summary>
    /// #127 walk start. Ground ControlDir does <c>speed += accel</c> and the
    /// same frame moves with the new speed (semi-implicit, 30 Hz). Unlocked,
    /// the guest runs that 34-tick step every present and dt/34 of the move
    /// is kept, so every present moved with s + one whole accel: from rest
    /// Crash was ahead by accel·(34−dt)/68 per tick (+4/+6 cm at 60/120 FPS,
    /// first ticks +33–50 %), and stopped as much short when braking.
    /// Speed: the ramp keeps dt/34 of a whole accel per present up to the
    /// guest's clamp (max speed / 0). dt/34 of the clamped remainder made
    /// an exponential tail at max speed (long at 144 FPS).
    /// Move: matching the PS1 frame ends needs speed s + accel·(34−dt)/68
    /// over the present (s = kept speed), at most the clamp: the difference
    /// to the guest speed is taken back or added along the guest velocity.
    /// Never more than the kept move on an axis, nothing when the axis did
    /// not move with the velocity (wall, carry). Ground d-pad walk only, not
    /// the hog, not the spin states: the intro spin run was already behind
    /// the PS1 (13 cm at 144 FPS) and this made it worse.
    /// </summary>
    static void PaceCrashWalkAccel(IMemory m, uint o, int vx, int vz, int speedTo)
    {
        int ds = speedTo - _ospeed;
        if (_crashHog || _exactTicks <= 0 || _exactTicks >= RefTicks - 0.01 || ds == 0 || speedTo <= 0
            || CrashSpinning(m, o)
            || (m.ReadU32(o + ObjStatusBOff) & FlagDpadControl) == 0
            || m.ReadU32(GameStateAddr) != GameStatePlaying)
        {
            _walkAccel = 0;
            return;
        }
        // A smaller step the same way as the last one = the guest clamped it.
        int a = ds;
        if (_walkAccel != 0 && (ds > 0) == (_walkAccel > 0) && Math.Abs(ds) < Math.Abs(_walkAccel))
            a = _walkAccel;
        else
            _walkAccel = ds;
        double step = a * _exactTicks / RefTicks;
        double kept = a > 0 ? Math.Min(_ospeed + step, speedTo) : Math.Max(_ospeed + step, speedTo);
        double move = kept + a * (RefTicks - _exactTicks) / (2 * RefTicks);
        move = a > 0 ? Math.Min(move, speedTo) : Math.Max(move, speedTo);
        _crashFracSp = 0;
        m.WriteU32(o + ObjSpeedOff, (uint)(int)Math.Round(kept));
        // Velocity per speed unit (speed_scale, heading) times the speed missing.
        double k = (move - speedTo) / speedTo * _exactTicks / 1024.0;
        TrimWalkAxis(m, o + ObjTransOff, _ox, vx, vx * k);
        TrimWalkAxis(m, o + ObjTransOff + 8, _oz, vz, vz * k);
    }

    static void TrimWalkAxis(IMemory m, uint addr, int from, int vel, double corr)
    {
        int kept = (int)m.ReadU32(addr) - from;
        if (kept == 0 || vel == 0 || (kept > 0) != (vel > 0)) return;
        double cap = Math.Abs((double)kept);
        int c = (int)Math.Round(Math.Clamp(corr, -cap, cap));
        if (c != 0) m.WriteU32(addr, (uint)(from + kept + c));
    }
}
