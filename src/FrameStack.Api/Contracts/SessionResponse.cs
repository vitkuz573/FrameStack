using FrameStack.Application.Sessions;

namespace FrameStack.Api.Contracts;

public sealed record SessionResponse(
    Guid Id,
    Guid ImageId,
    string Status,
    int CpuCores,
    int MemoryMb,
    string? RuntimeHandle,
    string? RuntimeArtifactPath,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc)
{
    public static SessionResponse FromDto(SessionDto dto)
    {
        return new SessionResponse(
            dto.Id,
            dto.ImageId,
            dto.Status,
            dto.CpuCores,
            dto.MemoryMb,
            dto.RuntimeHandle,
            dto.RuntimeArtifactPath,
            dto.LastError,
            dto.CreatedAtUtc,
            dto.LastTransitionAtUtc);
    }
}
