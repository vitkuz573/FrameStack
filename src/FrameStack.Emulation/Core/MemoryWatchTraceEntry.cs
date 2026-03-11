namespace FrameStack.Emulation.Core;

public sealed record MemoryWatchTraceEntry(
    uint ProgramCounter,
    uint Address,
    uint PreviousValue,
    uint CurrentValue);
