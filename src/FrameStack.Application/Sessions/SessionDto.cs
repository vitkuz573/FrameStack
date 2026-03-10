namespace FrameStack.Application.Sessions;

public sealed record SessionDto(
    Guid Id,
    Guid ImageId,
    string Status,
    int CpuCores,
    int MemoryMb,
    string? RuntimeHandle,
    string? RuntimeArtifactPath,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc);
