using System.Collections.Concurrent;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Domain.Sessions;

namespace FrameStack.Infrastructure.Persistence;

public sealed class InMemorySessionRepository : ISessionRepository
{
    private readonly ConcurrentDictionary<SessionId, EmulationSession> _storage = new();

    public Task AddAsync(EmulationSession session, CancellationToken cancellationToken)
    {
        if (!_storage.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException($"Session '{session.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<EmulationSession?> GetByIdAsync(SessionId id, CancellationToken cancellationToken)
    {
        _storage.TryGetValue(id, out var session);
        return Task.FromResult(session);
    }
}
