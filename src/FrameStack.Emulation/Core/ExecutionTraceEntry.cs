namespace FrameStack.Emulation.Core;

public sealed record ExecutionTraceEntry(
    uint ProgramCounter,
    int Hits);
