using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Core;
using FrameStack.Emulation.Memory;

namespace FrameStack.Emulation.UnitTests;

public sealed class EmulationMachineTraceTests
{
    private static readonly uint[] WatchWord2000 = [0x2000u];

    [Fact]
    public void RunWithTraceShouldTrackProgramCountersWhenHotSpotsDisabled()
    {
        var machine = CreateMachine(new LoopingTestCpu());

        var summary = machine.RunWithTrace(
            instructionBudget: 5,
            maxHotSpots: 0,
            trackedProgramCounters: new HashSet<uint> { 0x1000, 0x1004 });

        Assert.Equal(ExecutionStopReason.InstructionBudgetReached, summary.StopReason);
        Assert.Empty(summary.HotSpots);
        Assert.Equal(3, summary.TrackedProgramCounterHits[0x1000]);
        Assert.Equal(2, summary.TrackedProgramCounterHits[0x1004]);
    }

    [Fact]
    public void RunWithTraceShouldStopOnProgramCounterHitThreshold()
    {
        var machine = CreateMachine(new LoopingTestCpu());

        var summary = machine.RunWithTrace(
            instructionBudget: 20,
            maxHotSpots: 0,
            stopAtProgramCounterHits: new Dictionary<uint, long>
            {
                [0x1000] = 3,
            },
            trackedProgramCounters: new HashSet<uint> { 0x1000, 0x1004 });

        Assert.Equal(ExecutionStopReason.StopAtProgramCounterHit, summary.StopReason);
        Assert.Equal(4, summary.Summary.ExecutedInstructions);
        Assert.Equal(0x1000u, summary.Summary.FinalProgramCounter);
        Assert.Equal(3, summary.TrackedProgramCounterHits[0x1000]);
    }

    [Fact]
    public void RunWithTraceShouldStopOnWatchedWordChange()
    {
        var machine = CreateMachine(new LoopingTestCpu(writeWatchAddress: 0x2000u));

        var summary = machine.RunWithTrace(
            instructionBudget: 10,
            maxHotSpots: 0,
            watchWordAddresses: WatchWord2000,
            stopOnWatchWordChangeAddresses: new HashSet<uint> { 0x2000u });

        Assert.Equal(ExecutionStopReason.StopOnWatchWordChange, summary.StopReason);
        Assert.Single(summary.MemoryWatchEvents);
        Assert.Equal(1, summary.Summary.ExecutedInstructions);
        Assert.Equal(0x1000u, summary.MemoryWatchEvents[0].ProgramCounter);
    }

    [Fact]
    public void RunWithTraceShouldReportHaltedReason()
    {
        var machine = CreateMachine(new LoopingTestCpu(haltAfterCycles: 1));

        var summary = machine.RunWithTrace(
            instructionBudget: 10,
            maxHotSpots: 0);

        Assert.Equal(ExecutionStopReason.Halted, summary.StopReason);
        Assert.True(summary.Summary.Halted);
        Assert.Equal(1, summary.Summary.ExecutedInstructions);
    }

    private static EmulationMachine CreateMachine(ICpuCore cpu)
    {
        var memory = new ArrayMemoryBus(baseAddress: 0x0000, sizeBytes: 0x10000);
        return new EmulationMachine(cpu, memory, entryPoint: 0x1000);
    }

    private sealed class LoopingTestCpu(
        uint? writeWatchAddress = null,
        int haltAfterCycles = int.MaxValue) : ICpuCore
    {
        private readonly uint? _writeWatchAddress = writeWatchAddress;
        private readonly int _haltAfterCycles = haltAfterCycles;
        private int _executedCycles;

        public uint ProgramCounter { get; private set; }

        public bool Halted { get; private set; }

        public void Reset(uint entryPoint)
        {
            ProgramCounter = entryPoint;
            Halted = false;
            _executedCycles = 0;
        }

        public void ExecuteCycle(IMemoryBus memoryBus)
        {
            if (Halted)
            {
                return;
            }

            _executedCycles++;

            if (_executedCycles == 1 &&
                _writeWatchAddress.HasValue)
            {
                memoryBus.WriteUInt32(_writeWatchAddress.Value, 1);
            }

            if (_executedCycles >= _haltAfterCycles)
            {
                Halted = true;
                return;
            }

            ProgramCounter = ProgramCounter == 0x1000u
                ? 0x1004u
                : 0x1000u;
        }
    }
}
