namespace FrameStack.Emulation.Mips32;

public sealed class Mips32RegisterFile
{
    private readonly uint[] _gpr = new uint[32];

    public uint this[int index]
    {
        get => _gpr[index];
        set
        {
            if (index == 0)
            {
                return;
            }

            _gpr[index] = value;
        }
    }

    public uint Pc { get; set; }
}
