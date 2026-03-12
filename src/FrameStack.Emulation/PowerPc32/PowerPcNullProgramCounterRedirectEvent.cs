namespace FrameStack.Emulation.PowerPc32;

public sealed record PowerPcNullProgramCounterRedirectEvent(
    uint RedirectTarget,
    PowerPcNullProgramCounterRedirectSource Source,
    uint CandidateValue,
    uint? StackAddress,
    uint StackPointer,
    uint LinkRegister,
    uint Register30,
    uint Register31,
    uint StackWordMinus24,
    uint StackWordMinus20,
    uint StackWordMinus16,
    uint StackWordAtPointer,
    uint StackWordPlus4,
    uint StackWordPlus8);
