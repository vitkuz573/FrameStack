namespace FrameStack.Emulation.Images;

public sealed record LoadedImage(
    ImageContainerFormat Format,
    ImageArchitecture Architecture,
    ImageEndianness Endianness,
    uint EntryPoint,
    IReadOnlyList<LoadedImageSegment> Segments);
