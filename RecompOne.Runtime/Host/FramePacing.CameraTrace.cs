using System.Diagnostics;
using System.Globalization;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Dev-menu diagnostic: one line per CamFollow to logs/camtrace.txt, at any
/// frame rate (also Original 30 FPS) so the two runs can be diffed. Game thread only.
/// </summary>
public static partial class FramePacing
{
    public static volatile bool CameraTrace;

    /// <summary>c1 globals.h <c>gem_stamp</c>. CamFollow skips goal&amp;4 neighbor cams when stale.</summary>
    const uint GemStampAddr = 0x80061990u;
    /// <summary>CamFollow catch-up accumulator (gp+0x1C4).</summary>
    const uint CamSpeedAddr = 0x800565C0u;
    const int CamTraceMaxLines = 30000;

    static StreamWriter? _camTrace;
    static long _camTraceT0;
    static int _camTraceLines;
    static bool _ctActive;
    static int _ctProg0;
    static uint _ctPath0;
    static int _ctCalls;
    static uint _ctRa;
    static int _ctReq;

    static void CamTraceBegin(IMemory m)
    {
        _ctActive = false;
        if (!CameraTrace)
        {
            CloseCamTrace();
            return;
        }
        try
        {
            if (_camTrace == null)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                _camTrace = new StreamWriter(Path.Combine(AppPaths.LogsDir, "camtrace.txt"), false);
                _camTraceT0 = Stopwatch.GetTimestamp();
                _camTraceLines = 0;
                _camTrace.WriteLine($"# camtrace fps={(WantsUnlock ? "unlocked" : "original")} lid={m.ReadU32(Catalog.LevelIdAddr)}");
                _camTrace.WriteLine("# ms dt prog0->prog1 calls lu(ra:req) crash(x,y,z>>8) st sa ride offx offz zoom spd fe gs");
            }
            if (_camTraceLines >= CamTraceMaxLines) return;
            _ctProg0 = (int)m.ReadU32(CamProgressAddr);
            _ctPath0 = m.ReadU32(CamPathAddr);
            _ctCalls = 0;
            _ctRa = 0;
            _ctReq = 0;
            _ctActive = true;
        }
        catch
        {
            CloseCamTrace();
        }
    }

    static void CamTraceLevelUpdate(CpuContext c)
    {
        if (!_ctActive) return;
        _ctCalls++;
        _ctRa = c.RA;
        _ctReq = (int)c.A2;
    }

    static void CamTraceEnd(IMemory m)
    {
        if (!_ctActive || _camTrace == null) return;
        _ctActive = false;
        try
        {
            int prog1 = (int)m.ReadU32(CamProgressAddr);
            bool pathChange = m.ReadU32(CamPathAddr) != _ctPath0;
            string lu = _ctCalls == 0 ? "-"
                : (_ctRa switch
                {
                    CamFollowSnapRa => "snap",
                    0x8002A0B4u => "adj",
                    _ => $"{_ctRa:X8}"
                }) + ":" + _ctReq.ToString(CultureInfo.InvariantCulture);
            string crashTxt = "-";
            if (TryReadCrash(m, out uint crash))
            {
                int x = (int)m.ReadU32(crash + ObjTransOff) >> 8;
                int y = (int)m.ReadU32(crash + ObjTransOff + 4) >> 8;
                int z = (int)m.ReadU32(crash + ObjTransOff + 8) >> 8;
                crashTxt = $"{x},{y},{z} st={m.ReadU32(crash + ObjStateOff)} sa={m.ReadU32(crash + ObjStatusAOff) & 0xFF:X}";
            }
            double ms = (Stopwatch.GetTimestamp() - _camTraceT0) * 1000.0 / Stopwatch.Frequency;
            _camTrace.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{ms:0.0} dt={_exactTicks:0.00} p={_ctProg0}->{prog1}{(pathChange ? "*" : "")} n={_ctCalls} lu={lu} c={crashTxt} ride={_rideObj:X} " +
                $"offx={(int)m.ReadU32(CamOffsetXAddr)} offz={(int)m.ReadU32(CamOffsetZAddr)} zoom={(int)m.ReadU32(CamZoomAddr)} " +
                $"spd={(int)m.ReadU32(CamSpeedAddr)} fe={m.ReadU32(FramesElapsedAddr)} gs={m.ReadU32(GemStampAddr)}"));
            if (++_camTraceLines % 120 == 0)
                _camTrace.Flush();
            if (_camTraceLines >= CamTraceMaxLines)
            {
                _camTrace.WriteLine("# line cap reached");
                _camTrace.Flush();
            }
        }
        catch
        {
            CloseCamTrace();
        }
    }

    static void CloseCamTrace()
    {
        if (_camTrace == null) return;
        try
        {
            _camTrace.Flush();
            _camTrace.Dispose();
        }
        catch
        {
            // ignore
        }
        _camTrace = null;
    }
}
