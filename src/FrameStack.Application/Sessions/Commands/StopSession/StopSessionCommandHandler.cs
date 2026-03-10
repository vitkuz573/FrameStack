using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Runtime;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Sessions.Commands.StopSession;

public sealed class StopSessionCommandHandler(
    ISessionRepository sessionRepository,
    IRuntimeOrchestrator runtimeOrchestrator,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<StopSessionCommand, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(
        StopSessionCommand command,
        CancellationToken cancellationToken)
    {
        var session = await sessionRepository.GetByIdAsync(new SessionId(command.SessionId), cancellationToken);

        if (session is null)
        {
            return Result.Failure<SessionDto>(SessionErrors.SessionNotFound(command.SessionId));
        }

        if (string.IsNullOrWhiteSpace(session.RuntimeHandle))
        {
            return Result.Failure<SessionDto>(
                SessionErrors.StopFailed(command.SessionId, "Runtime handle is missing."));
        }

        try
        {
            await runtimeOrchestrator.StopAsync(session.RuntimeHandle, cancellationToken);
            session.Stop(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success<SessionDto>(session.ToDto());
        }
        catch (Exception exception)
        {
            session.Fail(exception.Message, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Failure<SessionDto>(SessionErrors.StopFailed(command.SessionId, exception.Message));
        }
    }
}
