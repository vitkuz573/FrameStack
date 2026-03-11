namespace FrameStack.Emulation.Core;

public sealed record ExecutionTraceSummary(
    ExecutionSummary Summary,
    IReadOnlyList<ExecutionTraceEntry> HotSpots,
    IReadOnlyList<uint> ProgramCounterTail,
    IReadOnlyList<MemoryWatchTraceEntry> MemoryWatchEvents,
    IReadOnlyDictionary<uint, long> TrackedProgramCounterHits,
    ExecutionStopReason StopReason)
{
    public bool StopAtProgramCounterReached => StopReason == ExecutionStopReason.StopAtProgramCounter;

    public bool StopAtProgramCounterHitReached => StopReason == ExecutionStopReason.StopAtProgramCounterHit;

    public bool StopOnWatchWordChangeReached => StopReason == ExecutionStopReason.StopOnWatchWordChange;
}
