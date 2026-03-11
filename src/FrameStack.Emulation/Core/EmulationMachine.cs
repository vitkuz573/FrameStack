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

    public IMemoryBus MemoryBus => _memoryBus;

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

    public ExecutionTraceSummary RunWithTrace(
        int instructionBudget,
        int maxHotSpots = 10,
        int tailLength = 0,
        uint? stopAtProgramCounter = null,
        IReadOnlyList<uint>? watchWordAddresses = null,
        int maxMemoryWatchEvents = 512)
    {
        if (instructionBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionBudget), "Instruction budget must be greater than zero.");
        }

        if (maxHotSpots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHotSpots), "Hot spot count must be greater than zero.");
        }

        if (tailLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tailLength), "Tail length cannot be negative.");
        }

        if (maxMemoryWatchEvents < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMemoryWatchEvents),
                "Max memory watch events cannot be negative.");
        }

        var hitCounter = new Dictionary<uint, int>();
        var executedThisRun = 0;
        var tailBuffer = tailLength > 0 ? new uint[tailLength] : null;
        var tailIndex = 0;
        var tailCount = 0;
        var watchEvents = new List<MemoryWatchTraceEntry>();
        List<MemoryWatchState>? watchStates = null;

        if (watchWordAddresses is { Count: > 0 })
        {
            watchStates = new List<MemoryWatchState>(watchWordAddresses.Count);

            foreach (var address in watchWordAddresses)
            {
                watchStates.Add(new MemoryWatchState(address, _memoryBus.ReadUInt32(address)));
            }
        }

        while (!_cpu.Halted && executedThisRun < instructionBudget)
        {
            var pc = _cpu.ProgramCounter;

            if (stopAtProgramCounter.HasValue &&
                pc == stopAtProgramCounter.Value)
            {
                break;
            }

            hitCounter.TryGetValue(pc, out var hits);
            hitCounter[pc] = hits + 1;

            if (tailBuffer is not null)
            {
                tailBuffer[tailIndex] = pc;
                tailIndex = (tailIndex + 1) % tailBuffer.Length;

                if (tailCount < tailBuffer.Length)
                {
                    tailCount++;
                }
            }

            _cpu.ExecuteCycle(_memoryBus);
            executedThisRun++;
            ExecutedInstructions++;

            if (watchStates is not null && watchEvents.Count < maxMemoryWatchEvents)
            {
                for (var index = 0; index < watchStates.Count; index++)
                {
                    var state = watchStates[index];
                    var currentValue = _memoryBus.ReadUInt32(state.Address);

                    if (currentValue != state.LastValue)
                    {
                        watchEvents.Add(new MemoryWatchTraceEntry(
                            pc,
                            state.Address,
                            state.LastValue,
                            currentValue));

                        watchStates[index] = state with { LastValue = currentValue };

                        if (watchEvents.Count >= maxMemoryWatchEvents)
                        {
                            break;
                        }
                    }
                }
            }
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

        IReadOnlyList<uint> programCounterTail = Array.Empty<uint>();

        if (tailBuffer is not null && tailCount > 0)
        {
            var orderedTail = new uint[tailCount];
            var start = tailCount == tailBuffer.Length
                ? tailIndex
                : 0;

            for (var index = 0; index < tailCount; index++)
            {
                orderedTail[index] = tailBuffer[(start + index) % tailBuffer.Length];
            }

            programCounterTail = orderedTail;
        }

        return new ExecutionTraceSummary(summary, hotSpots, programCounterTail, watchEvents);
    }

    private readonly record struct MemoryWatchState(
        uint Address,
        uint LastValue);

    public byte ReadByte(uint address)
    {
        return _memoryBus.ReadByte(address);
    }

    public uint ReadUInt32(uint address)
    {
        return _memoryBus.ReadUInt32(address);
    }
}
