namespace FrameStack.Application.Abstractions.Results;

public class Result
{
    protected Result(bool isSuccess, AppError error)
    {
        if (isSuccess && error != AppError.None)
        {
            throw new ArgumentException("Successful result must not contain an error.", nameof(error));
        }

        if (!isSuccess && error == AppError.None)
        {
            throw new ArgumentException("Failed result must contain an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public AppError Error { get; }

    public static Result Success() => new(true, AppError.None);

    public static Result Failure(AppError error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    public static Result<TValue> Failure<TValue>(AppError error) => new(error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(true, AppError.None)
    {
        _value = value;
    }

    internal Result(AppError error)
        : base(false, error)
    {
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Result does not contain a value.");

}
