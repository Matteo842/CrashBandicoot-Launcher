namespace RecompOne.Recompiler.Disasm;

public static class MipsDisasm
{
    public static MipsInstruction[] Disassemble(byte[] code, uint baseAddr)
    {
        int count = code.Length / 4;
        var instrs = new MipsInstruction[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * 4;
            uint word = (uint)(code[off] | code[off + 1] << 8 | code[off + 2] << 16 | code[off + 3] << 24);
            instrs[i] = new MipsInstruction(word, baseAddr + (uint)off);
        }
        return instrs;
    }
}
