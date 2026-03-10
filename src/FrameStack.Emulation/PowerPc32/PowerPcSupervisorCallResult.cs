namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPcSupervisorCallResult(
    uint ReturnValue,
    bool Halt = false,
    uint? NextProgramCounter = null);
