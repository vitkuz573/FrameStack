namespace FrameStack.Emulation.Memory;

public sealed record SparseMemoryPageSnapshot(
    uint PageIndex,
    byte[] Data);
