namespace FrameStack.Emulation.PowerPc32;

public sealed record PowerPc32CpuSnapshot(
    uint[] GeneralPurposeRegisters,
    uint ProgramCounter,
    uint LinkRegister,
    uint CounterRegister,
    uint ConditionRegister,
    uint FixedPointExceptionRegister,
    bool Halted,
    uint MachineStateRegister,
    ulong TimeBaseCounter,
    IReadOnlyDictionary<int, uint> ExtendedSpecialPurposeRegisters,
    IReadOnlyDictionary<uint, long> SupervisorCallCounters);
