namespace RecompOne.Runtime;

/// <summary>
/// Maps truncated GTE screen XY to pre-truncate subpixel values for dejitter.
/// Ring of recent RTP results (exact int match, most-recent wins) — avoids hash
/// collisions that swapped wrong floats onto vertices and made textures swim.
/// </summary>
static class GteScreenCache
{
    const int Size = 256;
    const int Mask = Size - 1;

    static readonly short[] _ix = new short[Size];
    static readonly short[] _iy = new short[Size];
    static readonly float[] _fx = new float[Size];
    static readonly float[] _fy = new float[Size];
    static int _head;
    static int _count;

    public static void Store(int ix, int iy, float fx, float fy)
    {
        // Only keep values that are the same pixel as the truncated screen XY.
        if (MathF.Abs(fx - ix) >= 1.01f || MathF.Abs(fy - iy) >= 1.01f)
            return;

        int i = _head;
        _ix[i] = (short)ix;
        _iy[i] = (short)iy;
        _fx[i] = fx;
        _fy[i] = fy;
        _head = (i + 1) & Mask;
        if (_count < Size) _count++;
    }

    public static bool TryFind(int ix, int iy, out float fx, out float fy)
    {
        // Scan newest → oldest so a reused screen pixel gets the latest projection.
        int n = _count;
        int i = (_head - 1) & Mask;
        for (int k = 0; k < n; k++)
        {
            if (_ix[i] == ix && _iy[i] == iy)
            {
                fx = _fx[i];
                fy = _fy[i];
                return true;
            }
            i = (i - 1) & Mask;
        }
        fx = fy = 0f;
        return false;
    }
}
