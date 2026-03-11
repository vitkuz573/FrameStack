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
    int LastMpc8xxControlSpr,
    IReadOnlyDictionary<int, uint> ExtendedSpecialPurposeRegisters,
    IReadOnlyDictionary<uint, long> SupervisorCallCounters,
    IReadOnlyList<PowerPc32TlbEntryState> InstructionTlbEntries,
    IReadOnlyList<PowerPc32TlbEntryState> DataTlbEntries);
