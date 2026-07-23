using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Context;

//some shenineguns to properly resolve the prehook without forcing it to be always bool
public static class PreHook
{
    public static bool Run(System.Func<CpuContext, IMemory, bool> hook, CpuContext c, IMemory m) => hook(c, m);

    public static bool Run(System.Action<CpuContext, IMemory> hook, CpuContext c, IMemory m)
    {
        hook(c, m);
        return true;
    }
}
