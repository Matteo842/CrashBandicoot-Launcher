namespace RecompOne.Runtime;

/// <summary>
/// Maps truncated GTE screen XY to the pre-truncate subpixel values for dejitter.
/// Game still reads integer SXY; only the HLE raster uses the floats.
/// </summary>
static class GteScreenCache
{
    const int Size = 2048;
    const int Mask = Size - 1;

    static readonly short[] _ix = new short[Size];
    static readonly short[] _iy = new short[Size];
    static readonly float[] _fx = new float[Size];
    static readonly float[] _fy = new float[Size];
    static readonly byte[] _live = new byte[Size];

    static int Hash(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & Mask;

    public static void Store(int ix, int iy, float fx, float fy)
    {
        int i = Hash(ix, iy);
        for (int p = 0; p < 4; p++)
        {
            int s = (i + p) & Mask;
            if (_live[s] == 0 || (_ix[s] == ix && _iy[s] == iy))
            {
                _ix[s] = (short)ix;
                _iy[s] = (short)iy;
                _fx[s] = fx;
                _fy[s] = fy;
                _live[s] = 1;
                return;
            }
        }
        // Collision: overwrite primary slot.
        _ix[i] = (short)ix;
        _iy[i] = (short)iy;
        _fx[i] = fx;
        _fy[i] = fy;
        _live[i] = 1;
    }

    public static bool TryFind(int ix, int iy, out float fx, out float fy)
    {
        int i = Hash(ix, iy);
        for (int p = 0; p < 4; p++)
        {
            int s = (i + p) & Mask;
            if (_live[s] != 0 && _ix[s] == ix && _iy[s] == iy)
            {
                fx = _fx[s];
                fy = _fy[s];
                return true;
            }
        }
        fx = fy = 0f;
        return false;
    }
}
