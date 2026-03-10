namespace FrameStack.Emulation.Images;

public sealed record ImageSectionDescriptor(
    uint VirtualAddress,
    uint FileOffset,
    uint FileSize,
    uint MemorySize,
    bool Readable,
    bool Writable,
    bool Executable);
