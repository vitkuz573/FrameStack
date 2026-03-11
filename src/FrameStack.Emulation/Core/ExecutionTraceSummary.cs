namespace FrameStack.Emulation.Core;

public sealed record ExecutionTraceSummary(
    ExecutionSummary Summary,
    IReadOnlyList<ExecutionTraceEntry> HotSpots,
    IReadOnlyList<uint> ProgramCounterTail,
    IReadOnlyList<MemoryWatchTraceEntry> MemoryWatchEvents,
    bool StopAtProgramCounterHitReached,
    bool StopOnWatchWordChangeReached);
