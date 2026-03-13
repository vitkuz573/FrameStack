namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPcSupervisorCallTraceEntry(
    uint ProgramCounter,
    uint ServiceCode,
    uint Argument0,
    uint Argument1,
    uint Argument2,
    uint Argument3,
    uint LinkRegister,
    uint ReturnValue,
    bool Halt,
    uint? NextProgramCounter,
    uint StackPointer = 0,
    uint Register30 = 0,
    uint Register31 = 0,
    uint StackWordMinus16 = 0,
    uint StackWordAtPointer = 0,
    uint StackWordPlus4 = 0,
    uint StackWordPlus8 = 0)
{
    public uint CallerProgramCounter => LinkRegister >= 4
        ? LinkRegister - 4
        : 0;
}
