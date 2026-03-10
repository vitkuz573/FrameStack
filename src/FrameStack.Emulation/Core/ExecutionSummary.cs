namespace FrameStack.Emulation.Core;

public sealed record ExecutionSummary(
    int ExecutedInstructions,
    bool Halted,
    uint FinalProgramCounter);
