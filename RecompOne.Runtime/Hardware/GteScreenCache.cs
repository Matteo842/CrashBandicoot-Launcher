namespace RecompOne.Runtime;

/// <summary>
/// Recent GTE RTP outputs: subpixel XY + depth for dejitter / perspective-correct UVs.
/// Ring with exact int match (most-recent wins).
/// </summary>
static class GteScreenCache
{
    const int Size = 512;
    const int Mask = Size - 1;

    static readonly short[] _ix = new short[Size];
    static readonly short[] _iy = new short[Size];
    static readonly float[] _fx = new float[Size];
    static readonly float[] _fy = new float[Size];
    static readonly float[] _z = new float[Size];
    static int _head;
    static int _count;

    public static void Store(int ix, int iy, float fx, float fy, float z)
    {
        if (MathF.Abs(fx - ix) >= 1.01f || MathF.Abs(fy - iy) >= 1.01f)
            return;

        int i = _head;
        _ix[i] = (short)ix;
        _iy[i] = (short)iy;
        _fx[i] = fx;
        _fy[i] = fy;
        _z[i] = z;
        _head = (i + 1) & Mask;
        if (_count < Size) _count++;
    }

    public static bool TryFind(int ix, int iy, out float fx, out float fy, out float z)
    {
        int n = _count;
        int i = (_head - 1) & Mask;
        for (int k = 0; k < n; k++)
        {
            if (_ix[i] == ix && _iy[i] == iy)
            {
                fx = _fx[i];
                fy = _fy[i];
                z = _z[i];
                return true;
            }
            i = (i - 1) & Mask;
        }
        fx = fy = z = 0f;
        return false;
    }
}
