namespace FrameStack.Application.Abstractions.Runtime;

public sealed record RuntimeStartRequest(
    Guid SessionId,
    string ArtifactPath,
    int CpuCores,
    int MemoryMb,
    int PreflightInstructionBudget = 512);
