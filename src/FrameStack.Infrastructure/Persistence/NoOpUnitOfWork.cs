using FrameStack.Application.Abstractions.Persistence;

namespace FrameStack.Infrastructure.Persistence;

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
