using FrameStack.Emulation.Images;

namespace FrameStack.Emulation.Runtime;

public sealed record RuntimeBootstrapReport(
    ImageContainerFormat Format,
    ImageArchitecture Architecture,
    ImageEndianness Endianness,
    uint EntryPoint,
    int SegmentCount,
    string Summary);
