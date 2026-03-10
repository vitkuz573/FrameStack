namespace FrameStack.Emulation.Core;

public sealed record ExecutionTraceSummary(
    ExecutionSummary Summary,
    IReadOnlyList<ExecutionTraceEntry> HotSpots);
