namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPcNullProgramCounterRedirectResolution(
    uint RedirectTarget,
    PowerPcNullProgramCounterRedirectSource Source,
    uint CandidateValue,
    uint? StackAddress);
