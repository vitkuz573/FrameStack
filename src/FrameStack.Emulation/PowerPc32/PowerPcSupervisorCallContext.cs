namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPcSupervisorCallContext(
    uint ProgramCounter,
    uint ServiceCode,
    uint Argument0,
    uint Argument1,
    uint Argument2,
    uint Argument3);
