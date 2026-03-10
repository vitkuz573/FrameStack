using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Runtime;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Sessions.Commands.StartSession;

public sealed class StartSessionCommandHandler(
    ISessionRepository sessionRepository,
    IImageRepository imageRepository,
    IRuntimeOrchestrator runtimeOrchestrator,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<StartSessionCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(
        StartSessionCommand command,
        CancellationToken cancellationToken)
    {
        var session = await sessionRepository.GetByIdAsync(new SessionId(command.SessionId), cancellationToken);

        if (session is null)
        {
            return Result.Failure<SessionDto>(SessionErrors.SessionNotFound(command.SessionId));
        }

        var image = await imageRepository.GetByIdAsync(session.ImageId, cancellationToken);

        if (image is null)
        {
            return Result.Failure<SessionDto>(SessionErrors.ImageNotFound(session.ImageId.Value));
        }

        if (!File.Exists(image.ArtifactPath))
        {
            return Result.Failure<SessionDto>(SessionErrors.ImageArtifactNotFound(image.ArtifactPath));
        }

        try
        {
            session.MarkPreparingImage(clock.UtcNow);
            session.MarkReady(image.ArtifactPath, clock.UtcNow);

            var runtimeResult = await runtimeOrchestrator.StartAsync(
                new RuntimeStartRequest(
                    session.Id.Value,
                    image.ArtifactPath,
                    session.Resources.CpuCores,
                    session.Resources.MemoryMb),
                cancellationToken);

            session.Start(runtimeResult.RuntimeHandle, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success<SessionDto>(session.ToDto());
        }
        catch (Exception exception)
        {
            session.Fail(exception.Message, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Failure<SessionDto>(SessionErrors.StartFailed(command.SessionId, exception.Message));
        }
    }
}
