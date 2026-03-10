namespace FrameStack.Application.Abstractions.Results;

public sealed record AppError(string Code, string Message)
{
    public static readonly AppError None = new(string.Empty, string.Empty);
}
