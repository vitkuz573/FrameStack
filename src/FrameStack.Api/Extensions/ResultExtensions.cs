using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Api.Extensions;

public static class ResultExtensions
{
    public static IResult ToProblem(this Result result)
    {
        var statusCode = ResolveStatusCode(result.Error.Code);

        return Results.Problem(
            title: result.Error.Code,
            detail: result.Error.Message,
            statusCode: statusCode);
    }

    private static int ResolveStatusCode(string errorCode)
    {
        if (errorCode.EndsWith("not_found", StringComparison.Ordinal) ||
            errorCode.EndsWith("image_not_found", StringComparison.Ordinal))
        {
            return StatusCodes.Status404NotFound;
        }

        if (errorCode.EndsWith("start_failed", StringComparison.Ordinal) ||
            errorCode.EndsWith("stop_failed", StringComparison.Ordinal))
        {
            return StatusCodes.Status409Conflict;
        }

        if (errorCode.Contains("invalid", StringComparison.Ordinal))
        {
            return StatusCodes.Status400BadRequest;
        }

        return StatusCodes.Status400BadRequest;
    }
}
