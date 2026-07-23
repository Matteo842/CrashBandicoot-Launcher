using RecompOne.Runtime.Bios;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public static class Interrupts
{
    public static void Deliver(int irq, CpuContext cpu, IMemory mem)
    {
        uint intrEnv = BiosB.IntrEnvInInterruptAddr;
        if (intrEnv == 0) return;

        uint handler = mem.ReadU32(intrEnv + 2u + (uint)irq * 4u);
        if (handler == 0) return;

        //takes a snap, apparently interrupt callbacks dont operate at the same context? could be wrong in mips3000, need to check furter TODO, seens to be accurate
        var snap = cpu.Snapshot();
        mem.WriteU16(intrEnv, 1);
        Dispatcher.Call(cpu, mem, handler);
        mem.WriteU16(intrEnv, 0);
        cpu.Restore(snap);
    }
}
