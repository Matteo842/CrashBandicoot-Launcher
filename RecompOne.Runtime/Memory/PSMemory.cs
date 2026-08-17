using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Memory;

public sealed class PSMemory : IMemory
{
    private readonly byte[] _ram = new byte[Runtime.Mode == RunMode.Devkit ? MemoryMap.DevkitRamSize : MemoryMap.RetailRamSize];
    private readonly byte[] _scratchpad = new byte[MemoryMap.ScratchpadSize];
    private readonly byte[] _hwregs = new byte[MemoryMap.HwRegsSize];
    private readonly byte[] _bios = new byte[MemoryMap.BiosSize];

    private readonly Gpu _gpu = new();
    private readonly Spu _spu = new();
    private readonly Mdec _mdec = new();
    private readonly Timers _timers = new();
    private readonly Dma _dma;
    private CdController? _cd;

    public ReadOnlySpan<byte> Ram => _ram;
    internal byte[] RamBuffer => _ram;

    public PSMemory()
    {
        _dma = new Dma(this, _gpu, _spu, _mdec, () => Runtime.DispatchIrq(3));
        Runtime.Gpu = _gpu;
        Runtime.Spu = _spu;
        Bios.KromFont.InstallInto(_bios);
    }

    public void SetCd(CdController cd) { _cd = cd; _dma.SetCd(cd); }

    private static bool IsDmaChcr(uint phys) => phys >= 0x1F801080u && phys < 0x1F8010F0u && (phys & 0xFu) == 8u;

    private uint Hw32(uint phys)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        return (uint)(_hwregs[o] | (_hwregs[o + 1] << 8) | (_hwregs[o + 2] << 16) | (_hwregs[o + 3] << 24));
    }

    private void Hw32(uint phys, uint v)
    {
        int o = (int)(phys - MemoryMap.HwRegsBase);
        _hwregs[o] = (byte)v;
        _hwregs[o + 1] = (byte)(v >> 8);
        _hwregs[o + 2] = (byte)(v >> 16);
        _hwregs[o + 3] = (byte)(v >> 24);
    }

    private void TrackWrite(uint phys, int size)
    {
        if (phys < MemoryMap.RamWindow)
        {
            uint off = phys % (uint)_ram.Length;
            Runtime.RamLog.RecordWrite(phys % (uint)_ram.Length, size);
            Dispatcher.NotifyWrite(off);
        }

    }

    private void TrackRead(uint phys, int size)
    {
        if (RamLogger.TrackReads && phys < MemoryMap.RamWindow)
            Runtime.RamLog.RecordRead(phys % (uint)_ram.Length, size);
    }

    // Crash SCUS-94900: .sbss global pointer. Geom jump-table stubs temporarily load
    // $gp with AND masks (0x00FFFFFF / 0x0000FFFF); a recompiler `return` instead of
    // `jr` can leave that mask in place, so later `lw ..., 4($gp)` hits 0x01000003.
    const uint CrashBandicootGp = 0x800563FCu;

    int _memYield = 65536;

    private Span<byte> Resolve(uint address, int size)
    {
        if (--_memYield <= 0)
        {
            _memYield = 65536;
            Sdk.LibEtc.MaybeCatchUpVBlank();
        }
        if (TryMap(address, size, out var span))
            return span;

        // Only heal when the access is already unmapped — do not touch $gp while geom
        // code still legitimately uses it as an AND mask for register ops.
        if (TryHealClobberedGpAddress(ref address) && TryMap(address, size, out span))
            return span;

        uint phys = MemoryMap.ToPhysical(address);
        throw new InvalidOperationException(FormatUnmapped(address, phys, size));
    }

    private bool TryMap(uint address, int size, out Span<byte> span)
    {
        uint phys = MemoryMap.ToPhysical(address);

        if (phys < MemoryMap.RamWindow)
        {
            span = _ram.AsSpan((int)(phys % (uint)_ram.Length), size);
            return true;
        }

        if (phys >= MemoryMap.ScratchpadBase && phys < MemoryMap.ScratchpadBase + MemoryMap.ScratchpadSize)
        {
            span = _scratchpad.AsSpan((int)(phys - MemoryMap.ScratchpadBase), size);
            return true;
        }

        if (phys >= MemoryMap.HwRegsBase && phys < MemoryMap.HwRegsBase + MemoryMap.HwRegsSize)
        {
            span = _hwregs.AsSpan((int)(phys - MemoryMap.HwRegsBase), size);
            return true;
        }

        if (phys >= MemoryMap.BiosBase && phys < MemoryMap.BiosBase + MemoryMap.BiosSize)
        {
            span = _bios.AsSpan((int)(phys - MemoryMap.BiosBase), size);
            return true;
        }

        span = default;
        return false;
    }

    /// <summary>
    /// If $gp is stuck as a geom AND mask and <paramref name="address"/> looks like a
    /// GP-relative access from that mask, restore the real GP and retarget the address.
    /// </summary>
    private static bool TryHealClobberedGpAddress(ref uint address)
    {
        var c = Runtime.Cpu;
        if (c is null) return false;
        if (c.GP is not (0x00FFFFFFu or 0x0000FFFFu)) return false;

        uint badGp = c.GP;
        int delta = unchecked((int)(address - badGp));
        if (delta is < -0x8000 or >= 0x8000) return false;

        c.GP = CrashBandicootGp;
        address = unchecked(CrashBandicootGp + (uint)delta);
        return true;
    }

    static string FormatUnmapped(uint address, uint phys, int size)
    {
        var c = Runtime.Cpu;
        var sb = new System.Text.StringBuilder();
        sb.Append($"unmapped address: 0x{address:X8} (phys=0x{phys:X8}, size={size})");
        if (c != null)
        {
            sb.Append($" RA=0x{c.RA:X8} SP=0x{c.SP:X8} GP=0x{c.GP:X8}");
            sb.Append($" A0=0x{c.A0:X8} A1=0x{c.A1:X8} A2=0x{c.A2:X8} A3=0x{c.A3:X8}");
            sb.Append($" S0=0x{c.S0:X8} S1=0x{c.S1:X8}");
        }
        if ((address & 1u) != 0)
            sb.Append(" [odd — possible unresolved CID/EID]");
        try
        {
            // Direct RAM peek — do not re-enter Resolve.
            if (Runtime.Mem is PSMemory psm && psm.Ram.Length > 0x56714)
            {
                var ram = psm.Ram;
                uint level = (uint)(ram[0x56710] | (ram[0x56711] << 8) | (ram[0x56712] << 16) | (ram[0x56713] << 24));
                sb.Append($" level=0x{level:X}");
            }
        }
        catch { /* ignore nested faults */ }

        // Keep the MessageBox readable: only recompiled / runtime frames.
        var frames = Environment.StackTrace.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("Recompiled.", StringComparison.Ordinal)
                        || l.Contains("RecompOne.", StringComparison.Ordinal)
                        || l.Contains("func_", StringComparison.Ordinal))
            .Take(12);
        foreach (var f in frames)
            sb.Append("\n  ").Append(f);

        var msg = sb.ToString();
        try { Diagnostics.SessionLog.Error(msg); } catch { /* ignore */ }
        return msg;
    }

    private static bool IsCd(uint phys) => phys >= 0x1F801800u && phys <= 0x1F801803u;
    private static bool IsSpu(uint phys) => phys >= 0x1F801C00u && phys < 0x1F801E80u;

    public byte ReadU8(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 1);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        return Resolve(address, 1)[0];
    }

    public ushort ReadU16(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 2);
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (IsSpu(phys)) return _spu.ReadReg16(phys);
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return (ushort)tv;
        var s = Resolve(address, 2);
        return (ushort)(s[0] | (s[1] << 8));
    }

    public uint ReadU32(uint address)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackRead(phys, 4);
        if (phys == 0x1F801810u) return _gpu.ReadData();
        if (phys == 0x1F801814u) return _gpu.ReadStat();
        if (phys == 0x1F801820u) return _mdec.ReadData();
        if (phys == 0x1F801824u) return _mdec.ReadStatus();
        if (phys == 0x1F8010F4u) return _dma.ReadDicr();
        if (_cd != null && IsCd(phys)) return _cd.Read(phys);
        if (IsSpu(phys)) return (uint)(_spu.ReadReg16(phys) | (_spu.ReadReg16(phys + 2) << 16));
        if (Timers.InRange(phys) && _timers.TryRead(phys, out uint tv)) return tv;
        var s = Resolve(address, 4);
        return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
    }

    public void WriteU8(uint address, byte value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 1);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, value); return; }
        Resolve(address, 1)[0] = value;
    }

    public void WriteU16(uint address, ushort value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 2);
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, value); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 2);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
    }

    public void WriteU32(uint address, uint value)
    {
        uint phys = MemoryMap.ToPhysical(address);
        TrackWrite(phys, 4);
        if (phys == 0x1F801810u) { _gpu.WriteGp0(value); return; }
        if (phys == 0x1F801814u) { _gpu.WriteGp1(value); return; }
        if (phys == 0x1F801820u) { _mdec.Write0(value); return; }
        if (phys == 0x1F801824u) { _mdec.WriteControl(value); return; }
        if (phys == 0x1F8010F4u) { _dma.WriteDicr(value); return; }
        if (IsDmaChcr(phys) && (value & 0x01000000u) != 0)
        {
            Hw32(phys, value & ~0x01000000u);
            _dma.Run((int)((phys - 0x1F801080u) / 0x10u), Hw32(phys - 8u), Hw32(phys - 4u), value);
            return;
        }
        if (_cd != null && IsCd(phys)) { _cd.Write(phys, (byte)value); return; }
        if (IsSpu(phys)) { _spu.WriteReg16(phys, (ushort)value); _spu.WriteReg16(phys + 2, (ushort)(value >> 16)); return; }
        if (_timers.TryWrite(phys, value)) return;
        var s = Resolve(address, 4);
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
        s[2] = (byte)(value >> 16);
        s[3] = (byte)(value >> 24);
    }

    public uint ReadWordLeft(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0x00FFFFFFu >> shift)) | (word << (24 - shift));
    }

    public uint ReadWordRight(uint current, uint address)
    {
        int shift = (int)((address & 3) * 8);
        uint word = ReadU32(address & ~3u);
        return (current & (0xFFFFFF00u << (24 - shift))) | (word >> shift);
    }

    public void WriteWordLeft(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0xFFFFFF00u << shift)) | (value >> (24 - shift)));
    }

    public void WriteWordRight(uint address, uint value)
    {
        uint aligned = address & ~3u;
        int shift = (int)((address & 3) * 8);
        uint mem = ReadU32(aligned);
        WriteU32(aligned, (mem & (0x00FFFFFFu >> (24 - shift))) | (value << shift));
    }

    public void LoadBytes(uint address, byte[] data)
    {
        if (data.Length == 0) return;
        uint phys = MemoryMap.ToPhysical(address);
        if (phys < (uint)_ram.Length && (long)phys + data.Length <= _ram.Length)
        {
            Buffer.BlockCopy(data, 0, _ram, (int)phys, data.Length);
            TrackWrite(phys, data.Length);
            return;
        }
        for (int i = 0; i < data.Length; i++)
            WriteU8(address + (uint)i, data[i]);
    }

    public void ZeroRange(uint address, uint length)
    {
        for (uint i = 0; i < length; i++)
            WriteU8(address + i, 0);
    }
}
