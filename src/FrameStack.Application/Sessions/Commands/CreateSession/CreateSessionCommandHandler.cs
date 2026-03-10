using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Domain.Images;
using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Sessions.Commands.CreateSession;

public sealed class CreateSessionCommandHandler(
    IImageRepository imageRepository,
    ISessionRepository sessionRepository,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<CreateSessionCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(
        CreateSessionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CpuCores <= 0 || command.MemoryMb <= 0)
        {
            return Result.Failure<SessionDto>(SessionErrors.InvalidResources);
        }

        var image = await imageRepository.GetByIdAsync(new ImageId(command.ImageId), cancellationToken);

        if (image is null)
        {
            return Result.Failure<SessionDto>(SessionErrors.ImageNotFound(command.ImageId));
        }

        var session = EmulationSession.Create(
            new ImageId(command.ImageId),
            new SessionResources(command.CpuCores, command.MemoryMb),
            clock.UtcNow);

        await sessionRepository.AddAsync(session, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success<SessionDto>(session.ToDto());
    }
}
