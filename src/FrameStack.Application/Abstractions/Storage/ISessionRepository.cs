using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Abstractions.Storage;

public interface ISessionRepository
{
    Task AddAsync(EmulationSession session, CancellationToken cancellationToken);

    Task<EmulationSession?> GetByIdAsync(SessionId id, CancellationToken cancellationToken);
}
