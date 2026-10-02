using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

/// <summary>
/// GTE RTP outputs: subpixel XY + depth for dejitter / perspective-correct UVs.
/// Vertices are matched by the RAM word swc2 wrote them to (the primitive field
/// the GPU later reads), validated against the word itself. Matching by integer
/// screen coords let overlapping geometry, Crash and the HUD borrow each other's
/// subpixel position whenever they landed on the same pixel (#66); that hash
/// (most-recent wins) only remains for code recompiled without swc2 addresses.
/// Generation invalidates the tables each frame so far verts are not evicted
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

    // One entry per vertex word swc2 writes into primitive memory this frame.
    const int AddrCapacity = 65536;
    const int AddrMask = AddrCapacity - 1;
    // Same RAM window DrawOTag and GPU DMA walk primitives in.
    const uint RamWordMask = 0x1FFFFCu;

    static readonly uint[] _addrKey = new uint[AddrCapacity];
    static readonly uint[] _addrGenAt = new uint[AddrCapacity];
    static readonly uint[] _addrWord = new uint[AddrCapacity];
    static readonly float[] _addrFx = new float[AddrCapacity];
    static readonly float[] _addrFy = new float[AddrCapacity];
    static readonly float[] _addrZ = new float[AddrCapacity];

    static uint _gen = 1;
    static bool _frameOpen;

    /// <summary>True once the recompiled code reports where swc2 stores projected vertices.</summary>
    public static bool AddressTagged { get; private set; }

    /// <summary>Call when a new OT is built (DMA6) or lazily on the first RTP of a frame.</summary>
    public static void BeginFrame()
    {
        _gen++;
        if (_gen == 0)
        {
            Array.Clear(_genAt);
            Array.Clear(_addrGenAt);
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

    /// <summary>Records a projected vertex (SXY word) written to <paramref name="address"/> by swc2.</summary>
    public static void StoreAt(uint address, uint word, float fx, float fy, float z)
    {
        uint phys = MemoryMap.ToPhysical(address);
        if (phys >= MemoryMap.RamWindow)
            return; // scratchpad staging; the GPU only reads primitives from RAM

        AddressTagged = true;
        if (!_frameOpen)
            BeginFrame();

        uint key = phys & RamWordMask;
        int i = (int)(Hash(key) & AddrMask);
        uint gen = _gen;

        for (int n = 0; n < AddrCapacity; n++)
        {
            if (_addrGenAt[i] != gen || _addrKey[i] == key)
            {
                _addrKey[i] = key;
                _addrGenAt[i] = gen;
                _addrWord[i] = word;
                _addrFx[i] = fx;
                _addrFy[i] = fy;
                _addrZ[i] = z;
                return;
            }

            i = (i + 1) & AddrMask;
        }
    }

    /// <summary>
    /// Subpixel data for a GP0 vertex word read from <paramref name="source"/>
    /// (<see cref="Gpu.NoSource"/> when it did not come from RAM).
    /// </summary>
    public static bool TryFindVertex(uint source, uint word, int ix, int iy, out float fx, out float fy, out float z)
    {
        if (!AddressTagged)
            return TryFind(ix, iy, out fx, out fy, out z);

        if (source != Gpu.NoSource)
        {
            uint key = source & RamWordMask;
            int i = (int)(Hash(key) & AddrMask);
            uint gen = _gen;

            for (int n = 0; n < AddrCapacity; n++)
            {
                if (_addrGenAt[i] != gen)
                    break;

                if (_addrKey[i] == key)
                {
                    // The CPU may have rewritten the primitive since swc2 stored it.
                    if (_addrWord[i] != word)
                        break;
                    fx = _addrFx[i];
                    fy = _addrFy[i];
                    z = _addrZ[i];
                    return true;
                }

                i = (i + 1) & AddrMask;
            }
        }

        fx = fy = z = 0f;
        return false;
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
