namespace RecompOne.Runtime.Hle;

/// <summary>
/// GPU BACKEND
/// </summary>
public interface IGpuBackend
{
    // false in headless or if gl init failed
    bool Ready { get; }

    // stats of the last present that carried new geometry
    long DrawnFrames { get; }
    int LastFrameFlushes { get; }
    int LastFrameWritebacks { get; }
    int LastFrameVertices { get; }
    int LastFrameCpuReads { get; }
    string LastDiagnostic { get; }

    // submit
    void SetDrawEnv(in HleDrawEnv env);
    void BeginWideDepth(uint? clearColor);
    // fill rows y..y+h-1 of both 16:9 side bands (0xBBGGRR), under everything drawn there
    void FillWideBands(int y, int h, uint color);
    void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f);
    void DrawRect(in HleRect r, in PrimFlags f);
    void DrawLine(in HleVertex a, in HleVertex b, in PrimFlags f);
    void FillRect(int x, int y, int w, int h, ushort color15);
    void CopyVram(int sx, int sy, int dx, int dy, int w, int h);
    void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px);
    void ReadVram(int x, int y, int w, int h, Span<ushort> px);

    // these touch gl
    void Flush();
    void Present(in HleDispEnv disp);
}
