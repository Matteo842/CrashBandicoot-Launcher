namespace RecompOne.Runtime.Context;

public sealed class CpuContext
{
    private uint[] _gpr = new uint[32];

    public uint At { get => _gpr[1];  set => _gpr[1]=  value; }
    public uint V0 { get => _gpr[2];  set => _gpr[2]=  value; }
    public uint V1 { get => _gpr[3];  set => _gpr[3]=  value; }
    public uint A0 { get => _gpr[4];  set => _gpr[4]=  value; }
    public uint A1 { get => _gpr[5];  set => _gpr[5]=  value; }
    public uint A2 { get => _gpr[6];  set => _gpr[6]=  value; }
    public uint A3 { get => _gpr[7];  set => _gpr[7]=  value; }
    public uint T0 { get => _gpr[8];  set => _gpr[8]=  value; }
    public uint T1 { get => _gpr[9];  set => _gpr[9]=  value; }
    public uint T2 { get => _gpr[10]; set => _gpr[10] = value; }
    public uint T3 { get => _gpr[11]; set => _gpr[11] = value; }
    public uint T4 { get => _gpr[12]; set => _gpr[12] = value; }
    public uint T5 { get => _gpr[13]; set => _gpr[13] = value; }
    public uint T6 { get => _gpr[14]; set => _gpr[14] = value; }
    public uint T7 { get => _gpr[15]; set => _gpr[15] = value; }
    public uint S0 { get => _gpr[16]; set => _gpr[16] = value; }
    public uint S1 { get => _gpr[17]; set => _gpr[17] = value; }
    public uint S2 { get => _gpr[18]; set => _gpr[18] = value; }
    public uint S3 { get => _gpr[19]; set => _gpr[19] = value; }
    public uint S4 { get => _gpr[20]; set => _gpr[20] = value; }
    public uint S5 { get => _gpr[21]; set => _gpr[21] = value; }
    public uint S6 { get => _gpr[22]; set => _gpr[22] = value; }
    public uint S7 { get => _gpr[23]; set => _gpr[23] = value; }
    public uint T8 { get => _gpr[24]; set => _gpr[24] = value; }
    public uint T9 { get => _gpr[25]; set => _gpr[25] = value; }
    public uint K0 { get => _gpr[26]; set => _gpr[26] = value; }
    public uint K1 { get => _gpr[27]; set => _gpr[27] = value; }
    public uint GP { get => _gpr[28]; set => _gpr[28] = value; }
    public uint SP { get => _gpr[29]; set => _gpr[29] = value; }
    public uint FP { get => _gpr[30]; set => _gpr[30] = value; }
    public uint RA { get => _gpr[31]; set => _gpr[31] = value; }

    public uint HI;
    public uint LO;
    
    public uint SR; 
    public uint Cause; 
    public uint EPC;
    public uint BadVAddr; 
    public uint PRId; 
    
    public uint this[int index]
    {
        get => index == 0 ? 0u : _gpr[index];
        set { if (index != 0) _gpr[index] = value; }
    }

    public (uint[] gpr, uint hi, uint lo) Snapshot() => ((uint[])_gpr.Clone(), HI, LO);

    public void Restore((uint[] gpr, uint hi, uint lo) s)
    {
        Array.Copy(s.gpr, _gpr, 32);
        HI = s.hi;
        LO = s.lo;
    }
}
