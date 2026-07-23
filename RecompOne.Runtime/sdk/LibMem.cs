using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

/// <summary>
/// Crash's halfword memcpy at 0x80033FBC (A0=src, A1=dst, A2=halfword count).
/// The retail code uses Duff's-device computed jumps into the copy body; the recompiler
/// turns those into Dispatcher.Call and blows up with "unmapped call".
/// </summary>
public static class LibMem
{
    public static void RMemcpy(CpuContext c, IMemory m)
    {
        uint src = c.A0;
        uint dst = c.A1;
        uint n = c.A2; // halfwords
        if (n == 0) return;

        // memmove-style for overlapping regions
        if (dst > src && dst < src + n * 2u)
        {
            for (uint i = n; i > 0; i--)
                m.WriteU16(dst + (i - 1) * 2u, m.ReadU16(src + (i - 1) * 2u));
            return;
        }

        for (uint i = 0; i < n; i++)
            m.WriteU16(dst + i * 2u, m.ReadU16(src + i * 2u));
    }
}
