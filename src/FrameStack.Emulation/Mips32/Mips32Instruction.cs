namespace FrameStack.Emulation.Mips32;

internal readonly struct Mips32Instruction(uint word)
{
    public uint Word { get; } = word;

    public int Opcode => (int)(Word >> 26);

    public int Rs => (int)((Word >> 21) & 0x1F);

    public int Rt => (int)((Word >> 16) & 0x1F);

    public short Immediate => unchecked((short)(Word & 0xFFFF));

    public int Function => (int)(Word & 0x3F);

    public uint JumpTarget => Word & 0x03FF_FFFF;
}
