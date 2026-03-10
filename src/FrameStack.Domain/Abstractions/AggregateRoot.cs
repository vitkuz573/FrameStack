namespace FrameStack.Domain.Abstractions;

public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id) where TId : notnull;
