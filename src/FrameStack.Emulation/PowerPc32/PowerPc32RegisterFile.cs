namespace FrameStack.Emulation.PowerPc32;

public sealed class PowerPc32RegisterFile
{
    private readonly uint[] _gpr = new uint[32];

    public uint this[int index]
    {
        get => _gpr[index];
        set => _gpr[index] = value;
    }

    public uint Pc { get; set; }

    public uint Lr { get; set; }

    public uint Ctr { get; set; }

    public uint Cr { get; set; }

    public uint Xer { get; set; }
}
