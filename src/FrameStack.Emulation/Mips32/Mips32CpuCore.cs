using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Mips32;

public sealed class Mips32CpuCore : ICpuCore
{
    private readonly Mips32RegisterFile _registers = new();

    public uint ProgramCounter => _registers.Pc;

    public bool Halted { get; private set; }

    public Mips32RegisterFile Registers => _registers;

    public void Reset(uint entryPoint)
    {
        _registers.Pc = entryPoint;
        Halted = false;
    }

    public void ExecuteCycle(IMemoryBus memoryBus)
    {
        if (Halted)
        {
            return;
        }

        var instructionWord = memoryBus.ReadUInt32(_registers.Pc);
        var instruction = new Mips32Instruction(instructionWord);

        switch (instruction.Opcode)
        {
            case 0x00:
                ExecuteSpecial(instruction);
                break;
            case 0x02:
                ExecuteJump(instruction);
                break;
            case 0x08:
                ExecuteAddImmediate(instruction);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported MIPS32 opcode 0x{instruction.Opcode:X2} at PC=0x{_registers.Pc:X8}.");
        }
    }

    private void ExecuteSpecial(Mips32Instruction instruction)
    {
        if (instruction.Word == 0)
        {
            _registers.Pc += 4;
            return;
        }

        if (instruction.Function == 0x0D)
        {
            Halted = true;
            _registers.Pc += 4;
            return;
        }

        throw new NotSupportedException(
            $"Unsupported SPECIAL function 0x{instruction.Function:X2} at PC=0x{_registers.Pc:X8}.");
    }

    private void ExecuteJump(Mips32Instruction instruction)
    {
        var nextPc = _registers.Pc + 4;
        var upper = nextPc & 0xF000_0000;
        var target = instruction.JumpTarget << 2;

        _registers.Pc = upper | target;
    }

    private void ExecuteAddImmediate(Mips32Instruction instruction)
    {
        var rsValue = _registers[instruction.Rs];
        var immediate = instruction.Immediate;
        var result = unchecked(rsValue + (uint)immediate);

        _registers[instruction.Rt] = result;
        _registers.Pc += 4;
    }
}
