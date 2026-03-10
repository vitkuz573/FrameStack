using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Sessions;

internal static class SessionMappings
{
    public static SessionDto ToDto(this EmulationSession session)
    {
        return new SessionDto(
            session.Id.Value,
            session.ImageId.Value,
            session.Status.ToString(),
            session.Resources.CpuCores,
            session.Resources.MemoryMb,
            session.RuntimeHandle,
            session.RuntimeArtifactPath,
            session.LastError,
            session.CreatedAtUtc,
            session.LastTransitionAtUtc);
    }
}
