namespace FrameStack.Emulation.PowerPc32;

public enum PowerPcNullProgramCounterRedirectSource
{
    LinkRegister = 0,
    StackSlot = 1,
    StackFrameChain = 2,
    LastKnownTarget = 3,
    FallbackEntryPoint = 4,
    Register30 = 5,
    Register31 = 6
}
