namespace RecompOne.Runtime;

/// <summary>
/// GTE RTP outputs: subpixel XY + depth for dejitter / perspective-correct UVs.
/// Open-addressing hash keyed by integer screen coords (most-recent wins).
/// Generation invalidates the table each frame so far verts are not evicted
/// by later near-object RTPs within the same frame.
/// </summary>
static class GteScreenCache
{
    // Crash transforms well over 512 verts per frame; 32k keeps distant RTP hits alive until DrawOTag.
    const int Capacity = 32768;
    const int Mask = Capacity - 1;

    static readonly uint[] _key = new uint[Capacity];
    static readonly uint[] _genAt = new uint[Capacity];
    static readonly float[] _fx = new float[Capacity];
    static readonly float[] _fy = new float[Capacity];
    static readonly float[] _z = new float[Capacity];
    static uint _gen = 1;
    static bool _frameOpen;

    /// <summary>Call when a new OT is built (DMA6) or lazily on the first RTP of a frame.</summary>
    public static void BeginFrame()
    {
        _gen++;
        if (_gen == 0)
        {
            Array.Clear(_genAt);
            _gen = 1;
        }
        _frameOpen = true;
    }

    /// <summary>Call after DrawOTag so the next RTP starts a fresh generation.</summary>
    public static void EndFrame() => _frameOpen = false;

    public static void Store(int ix, int iy, float fx, float fy, float z)
    {
        if (!_frameOpen)
            BeginFrame();

        if (MathF.Abs(fx - ix) >= 1.01f || MathF.Abs(fy - iy) >= 1.01f)
            return;

        uint key = Pack(ix, iy);
        int i = (int)(Hash(key) & Mask);
        uint gen = _gen;

        for (int n = 0; n < Capacity; n++)
        {
            if (_genAt[i] != gen)
            {
                _key[i] = key;
                _genAt[i] = gen;
                _fx[i] = fx;
                _fy[i] = fy;
                _z[i] = z;
                return;
            }

            if (_key[i] == key)
            {
                _fx[i] = fx;
                _fy[i] = fy;
                _z[i] = z;
                return;
            }

            i = (i + 1) & Mask;
        }
    }

    public static bool TryFind(int ix, int iy, out float fx, out float fy, out float z)
    {
        uint key = Pack(ix, iy);
        int i = (int)(Hash(key) & Mask);
        uint gen = _gen;

        for (int n = 0; n < Capacity; n++)
        {
            if (_genAt[i] != gen)
                break;

            if (_key[i] == key)
            {
                fx = _fx[i];
                fy = _fy[i];
                z = _z[i];
                return true;
            }

            i = (i + 1) & Mask;
        }

        fx = fy = z = 0f;
        return false;
    }

    static uint Pack(int ix, int iy) => ((uint)(ix & 0xFFFF) << 16) | (uint)(iy & 0xFFFF);

    static uint Hash(uint key)
    {
        key ^= key >> 16;
        key *= 0x7FEB352Du;
        key ^= key >> 15;
        key *= 0x846CA68Bu;
        key ^= key >> 16;
        return key;
    }
}
