using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Core;

public sealed class EmulationMachine
{
    private readonly ICpuCore _cpu;
    private readonly IMemoryBus _memoryBus;

    public EmulationMachine(
        ICpuCore cpu,
        IMemoryBus memoryBus,
        uint entryPoint)
    {
        _cpu = cpu;
        _memoryBus = memoryBus;
        _cpu.Reset(entryPoint);
    }

    public int ExecutedInstructions { get; private set; }

    public bool Halted => _cpu.Halted;

    public uint ProgramCounter => _cpu.ProgramCounter;

    public void LoadImage(uint baseAddress, byte[] imageBytes)
    {
        _memoryBus.LoadBytes(baseAddress, imageBytes);
    }

    public ExecutionSummary Run(int instructionBudget)
    {
        if (instructionBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionBudget), "Instruction budget must be greater than zero.");
        }

        var executedThisRun = 0;

        while (!_cpu.Halted && executedThisRun < instructionBudget)
        {
            _cpu.ExecuteCycle(_memoryBus);
            executedThisRun++;
            ExecutedInstructions++;
        }

        return new ExecutionSummary(
            executedThisRun,
            _cpu.Halted,
            _cpu.ProgramCounter);
    }
}
