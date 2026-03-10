namespace FrameStack.Emulation.Images;

public sealed record ImageInspectionResult(
    ImageContainerFormat Format,
    ImageArchitecture Architecture,
    ImageEndianness Endianness,
    uint EntryPoint,
    IReadOnlyList<ImageSectionDescriptor> Sections,
    string Summary);
