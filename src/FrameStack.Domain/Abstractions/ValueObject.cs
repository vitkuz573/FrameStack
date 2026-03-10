namespace FrameStack.Domain.Abstractions;

public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetAtomicValues();

    public override bool Equals(object? obj)
    {
        if (obj is not ValueObject other || GetType() != obj.GetType())
        {
            return false;
        }

        return GetAtomicValues().SequenceEqual(other.GetAtomicValues());
    }

    public override int GetHashCode()
    {
        return GetAtomicValues()
            .Select(value => value?.GetHashCode() ?? 0)
            .Aggregate(0, HashCode.Combine);
    }
}
