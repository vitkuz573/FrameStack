namespace FrameStack.Domain.Abstractions;

public interface IDomainEvent
{
    DateTimeOffset OccurredAtUtc { get; }
}
