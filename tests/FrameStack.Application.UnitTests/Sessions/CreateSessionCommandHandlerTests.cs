using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Application.Sessions.Commands.CreateSession;
using FrameStack.Domain.Images;
using FrameStack.Domain.Sessions;

namespace FrameStack.Application.UnitTests.Sessions;

public sealed class CreateSessionCommandHandlerTests
{
    [Fact]
    public async Task HandleWhenImageDoesNotExistShouldReturnFailure()
    {
        var handler = new CreateSessionCommandHandler(
            new MissingImageRepository(),
            new InMemorySessionRepository(),
            new NoOpUnitOfWork(),
            new FixedClock(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero)));

        var command = new CreateSessionCommand(Guid.NewGuid(), CpuCores: 2, MemoryMb: 2048);

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("sessions.image_not_found", result.Error.Code);
    }

    private sealed class MissingImageRepository : IImageRepository
    {
        public Task AddAsync(EmulatorImage image, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<EmulatorImage?> GetByIdAsync(ImageId id, CancellationToken cancellationToken)
        {
            return Task.FromResult<EmulatorImage?>(null);
        }
    }

    private sealed class InMemorySessionRepository : ISessionRepository
    {
        public Task AddAsync(EmulationSession session, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<EmulationSession?> GetByIdAsync(SessionId id, CancellationToken cancellationToken)
        {
            return Task.FromResult<EmulationSession?>(null);
        }
    }

    private sealed class NoOpUnitOfWork : IUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
