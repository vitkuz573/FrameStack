namespace FrameStack.Domain.Images;

public readonly record struct ImageId(Guid Value)
{
    public static ImageId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
