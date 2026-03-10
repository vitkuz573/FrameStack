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

    public ExecutionTraceSummary RunWithTrace(int instructionBudget, int maxHotSpots = 10)
    {
        if (instructionBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionBudget), "Instruction budget must be greater than zero.");
        }

        if (maxHotSpots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHotSpots), "Hot spot count must be greater than zero.");
        }

        var hitCounter = new Dictionary<uint, int>();
        var executedThisRun = 0;

        while (!_cpu.Halted && executedThisRun < instructionBudget)
        {
            var pc = _cpu.ProgramCounter;
            hitCounter.TryGetValue(pc, out var hits);
            hitCounter[pc] = hits + 1;

            _cpu.ExecuteCycle(_memoryBus);
            executedThisRun++;
            ExecutedInstructions++;
        }

        var summary = new ExecutionSummary(
            executedThisRun,
            _cpu.Halted,
            _cpu.ProgramCounter);

        var hotSpots = hitCounter
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(maxHotSpots)
            .Select(pair => new ExecutionTraceEntry(pair.Key, pair.Value))
            .ToArray();

        return new ExecutionTraceSummary(summary, hotSpots);
    }
}
