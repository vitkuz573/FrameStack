namespace FrameStack.Emulation.PowerPc32;

public readonly record struct PowerPc32TlbEntryState(
    int Index,
    uint EffectivePageNumber,
    uint RealPageNumber,
    uint TableWalkControl);
