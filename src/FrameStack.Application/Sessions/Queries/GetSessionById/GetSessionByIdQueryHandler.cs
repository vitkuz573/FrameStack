using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Domain.Sessions;

namespace FrameStack.Application.Sessions.Queries.GetSessionById;

public sealed class GetSessionByIdQueryHandler(
    ISessionRepository sessionRepository)
    : IQueryHandler<GetSessionByIdQuery, SessionDto>
{
    public async Task<Result<SessionDto>> Handle(GetSessionByIdQuery query, CancellationToken cancellationToken)
    {
        var session = await sessionRepository.GetByIdAsync(new SessionId(query.SessionId), cancellationToken);

        if (session is null)
        {
            return Result.Failure<SessionDto>(SessionErrors.SessionNotFound(query.SessionId));
        }

        return Result.Success<SessionDto>(session.ToDto());
    }
}
