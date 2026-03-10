using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Sessions.Commands.CreateSession;

public sealed record CreateSessionCommand(
    Guid ImageId,
    int CpuCores,
    int MemoryMb) : ICommand<SessionDto>;
