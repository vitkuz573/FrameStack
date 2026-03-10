using FrameStack.Domain.Abstractions;

namespace FrameStack.Domain.Sessions.Events;

public sealed record EmulationSessionStartedDomainEvent(
    SessionId SessionId,
    string RuntimeHandle,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
