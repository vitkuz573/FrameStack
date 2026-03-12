namespace FrameStack.Emulation.PowerPc32;

public enum PowerPcMemoryAccessType
{
    Read = 0,
    Write = 1,
}

public readonly record struct PowerPcMemoryAccessTraceEntry(
    uint ProgramCounter,
    PowerPcMemoryAccessType AccessType,
    uint EffectiveAddress,
    uint PhysicalAddress,
    int SizeBytes,
    uint Value);
