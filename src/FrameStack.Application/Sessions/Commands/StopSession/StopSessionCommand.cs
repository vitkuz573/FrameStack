using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Sessions.Commands.StopSession;

public sealed record StopSessionCommand(Guid SessionId) : ICommand<SessionDto>;
