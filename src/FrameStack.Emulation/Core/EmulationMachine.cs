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
        IReadOnlyDictionary<uint, long>? stopAtProgramCounterHits = null,
        IReadOnlySet<uint>? trackedProgramCounters = null,
        IReadOnlyList<uint>? watchWordAddresses = null,
        IReadOnlySet<uint>? stopOnWatchWordChangeAddresses = null,
        int maxMemoryWatchEvents = 512)
    {
        if (instructionBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionBudget), "Instruction budget must be greater than zero.");
        }

        if (maxHotSpots < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHotSpots), "Hot spot count cannot be negative.");
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

        Dictionary<uint, int>? hotSpotCounter = null;
        Dictionary<uint, int>? stopAtProgramCounterHitCounter = null;
        Dictionary<uint, long>? trackedProgramCounterHitCounter = null;
        var trackHotSpots = maxHotSpots > 0;
        var executedThisRun = 0;
        var tailBuffer = tailLength > 0 ? new uint[tailLength] : null;
        var tailIndex = 0;
        var tailCount = 0;
        var watchEvents = new List<MemoryWatchTraceEntry>();
        var stopReason = ExecutionStopReason.None;
        List<MemoryWatchState>? watchStates = null;

        if (trackHotSpots)
        {
            hotSpotCounter = new Dictionary<uint, int>();
        }

        if (stopAtProgramCounterHits is { Count: > 0 })
        {
            stopAtProgramCounterHitCounter = new Dictionary<uint, int>(stopAtProgramCounterHits.Count);
        }

        if (trackedProgramCounters is { Count: > 0 })
        {
            trackedProgramCounterHitCounter = new Dictionary<uint, long>(trackedProgramCounters.Count);

            foreach (var trackedProgramCounter in trackedProgramCounters)
            {
                trackedProgramCounterHitCounter[trackedProgramCounter] = 0;
            }
        }

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
                stopReason = ExecutionStopReason.StopAtProgramCounter;
                break;
            }

            if (hotSpotCounter is not null)
            {
                hotSpotCounter.TryGetValue(pc, out var hits);
                hotSpotCounter[pc] = hits + 1;
            }

            if (trackedProgramCounterHitCounter is not null &&
                trackedProgramCounterHitCounter.TryGetValue(pc, out var trackedHits))
            {
                trackedProgramCounterHitCounter[pc] = trackedHits + 1;
            }

            if (stopAtProgramCounterHits is not null &&
                stopAtProgramCounterHits.TryGetValue(pc, out var requiredHits) &&
                requiredHits > 0)
            {
                var pcHits = 0;

                if (hotSpotCounter is not null &&
                    hotSpotCounter.TryGetValue(pc, out var hotSpotHits))
                {
                    pcHits = hotSpotHits;
                }
                else if (stopAtProgramCounterHitCounter is not null)
                {
                    stopAtProgramCounterHitCounter.TryGetValue(pc, out var existingHits);
                    pcHits = existingHits + 1;
                    stopAtProgramCounterHitCounter[pc] = pcHits;
                }

                if (pcHits >= requiredHits)
                {
                    stopReason = ExecutionStopReason.StopAtProgramCounterHit;
                    break;
                }
            }

            if (tailBuffer is not null)
            {
                tailBuffer[tailIndex] = pc;
                tailIndex = (tailIndex + 1) % tailBuffer.Length;

                if (tailCount < tailBuffer.Length)
                {
                    tailCount++;
                }
            }

            try
            {
                _cpu.ExecuteCycle(_memoryBus);
            }
            catch (Exception exception)
            {
                throw new TraceChunkExecutionException(
                    BuildTraceSummary(
                        executedThisRun,
                        hotSpotCounter,
                        maxHotSpots,
                        tailBuffer,
                        tailCount,
                        tailIndex,
                        watchEvents,
                        trackedProgramCounterHitCounter,
                        ExecutionStopReason.None),
                    pc,
                    exception);
            }

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

                        if (stopOnWatchWordChangeAddresses is not null &&
                            stopOnWatchWordChangeAddresses.Contains(state.Address))
                        {
                            stopReason = ExecutionStopReason.StopOnWatchWordChange;
                            break;
                        }
                    }
                }
            }

            if (stopReason == ExecutionStopReason.StopOnWatchWordChange)
            {
                break;
            }
        }

        if (stopReason == ExecutionStopReason.None)
        {
            if (_cpu.Halted)
            {
                stopReason = ExecutionStopReason.Halted;
            }
            else if (executedThisRun >= instructionBudget)
            {
                stopReason = ExecutionStopReason.InstructionBudgetReached;
            }
        }

        return BuildTraceSummary(
            executedThisRun,
            hotSpotCounter,
            maxHotSpots,
            tailBuffer,
            tailCount,
            tailIndex,
            watchEvents,
            trackedProgramCounterHitCounter,
            stopReason);
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

    private ExecutionTraceSummary BuildTraceSummary(
        int executedThisRun,
        Dictionary<uint, int>? hotSpotCounter,
        int maxHotSpots,
        uint[]? tailBuffer,
        int tailCount,
        int tailIndex,
        List<MemoryWatchTraceEntry> watchEvents,
        Dictionary<uint, long>? trackedProgramCounterHitCounter,
        ExecutionStopReason stopReason)
    {
        var summary = new ExecutionSummary(
            executedThisRun,
            _cpu.Halted,
            _cpu.ProgramCounter);

        var hotSpots = BuildHotSpots(hotSpotCounter, maxHotSpots);
        var programCounterTail = BuildProgramCounterTail(tailBuffer, tailCount, tailIndex);
        var trackedProgramCounterHits = trackedProgramCounterHitCounter is null
            ? (IReadOnlyDictionary<uint, long>)new Dictionary<uint, long>()
            : trackedProgramCounterHitCounter.ToDictionary(pair => pair.Key, pair => pair.Value);
        var memoryWatchEvents = watchEvents.Count == 0
            ? Array.Empty<MemoryWatchTraceEntry>()
            : watchEvents.ToArray();

        return new ExecutionTraceSummary(
            summary,
            hotSpots,
            programCounterTail,
            memoryWatchEvents,
            trackedProgramCounterHits,
            stopReason);
    }

    private static ExecutionTraceEntry[] BuildHotSpots(
        Dictionary<uint, int>? hotSpotCounter,
        int maxHotSpots)
    {
        if (hotSpotCounter is not { Count: > 0 } ||
            maxHotSpots <= 0)
        {
            return [];
        }

        return hotSpotCounter
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(maxHotSpots)
            .Select(pair => new ExecutionTraceEntry(pair.Key, pair.Value))
            .ToArray();
    }

    private static uint[] BuildProgramCounterTail(
        uint[]? tailBuffer,
        int tailCount,
        int tailIndex)
    {
        if (tailBuffer is null || tailCount <= 0)
        {
            return [];
        }

        var orderedTail = new uint[tailCount];
        var start = tailCount == tailBuffer.Length
            ? tailIndex
            : 0;

        for (var index = 0; index < tailCount; index++)
        {
            orderedTail[index] = tailBuffer[(start + index) % tailBuffer.Length];
        }

        return orderedTail;
    }
}
