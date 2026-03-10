namespace FrameStack.Emulation.PowerPc32;

internal readonly struct PowerPcInstruction(uint word)
{
    public uint Word { get; } = word;

    public int Opcode => (int)(Word >> 26);

    public int Rt => (int)((Word >> 21) & 0x1F);

    public int Rs => Rt;

    public int Ra => (int)((Word >> 16) & 0x1F);

    public int Rb => (int)((Word >> 11) & 0x1F);

    public short Simm => unchecked((short)(Word & 0xFFFF));

    public ushort Uimm => (ushort)(Word & 0xFFFF);

    public int XO => (int)((Word >> 1) & 0x3FF);

    public bool RecordCondition => (Word & 1) != 0;

    public bool Link => (Word & 1) != 0;

    public bool AbsoluteAddress => ((Word >> 1) & 1) != 0;

    public int BranchOptions => (int)((Word >> 21) & 0x1F);

    public int BranchConditionBit => (int)((Word >> 16) & 0x1F);

    public int Shift => (int)((Word >> 11) & 0x1F);

    public int MaskBegin => (int)((Word >> 6) & 0x1F);

    public int MaskEnd => (int)((Word >> 1) & 0x1F);

    public int ConditionRegisterField => (int)((Word >> 23) & 0x7);

    public int ConditionRegisterMask => (int)((Word >> 12) & 0xFF);

    public int BranchDisplacementImmediate
    {
        get
        {
            var value = (int)(Word & 0x03FF_FFFC);

            if ((value & 0x0200_0000) != 0)
            {
                value |= unchecked((int)0xFC00_0000);
            }

            return value;
        }
    }

    public int BranchDisplacementConditional
    {
        get
        {
            var value = (int)((Word >> 2) & 0x3FFF);

            if ((value & 0x2000) != 0)
            {
                value |= unchecked((int)0xFFFF_C000);
            }

            return value << 2;
        }
    }

    public int Spr
    {
        get
        {
            var upper = (int)((Word >> 16) & 0x1F);
            var lower = (int)((Word >> 11) & 0x1F);
            return (lower << 5) | upper;
        }
    }
}
