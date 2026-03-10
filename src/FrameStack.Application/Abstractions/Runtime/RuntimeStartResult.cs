namespace FrameStack.Application.Abstractions.Runtime;

public sealed record RuntimeStartResult(
    string RuntimeHandle,
    string ImageFormat,
    string ImageArchitecture,
    string Endianness,
    uint EntryPoint,
    int LoadedSegmentCount,
    string BootstrapSummary,
    int PreflightExecutedInstructions,
    bool PreflightHalted,
    uint PreflightProgramCounter);
