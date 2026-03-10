namespace FrameStack.Emulation.Images;

public sealed record LoadedImageSegment(
    uint VirtualAddress,
    byte[] Data,
    uint MemorySize,
    bool Executable);
