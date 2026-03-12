namespace FrameStack.Emulation.Core;

public enum ExecutionStopReason
{
    None = 0,
    InstructionBudgetReached = 1,
    Halted = 2,
    StopAtProgramCounter = 3,
    StopAtProgramCounterHit = 4,
    StopOnWatchWordChange = 5,
    StopOnConsoleRepeat = 6,
}
