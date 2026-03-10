using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Sessions.Queries.GetSessionById;

public sealed record GetSessionByIdQuery(Guid SessionId) : IQuery<SessionDto>;
