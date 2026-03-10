using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Sessions.Commands.StartSession;

public sealed record StartSessionCommand(Guid SessionId) : ICommand<SessionDto>;
