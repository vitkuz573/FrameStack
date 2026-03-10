using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Application.Abstractions.Messaging;

public interface IQueryDispatcher
{
    Task<Result<TResult>> Query<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>;
}
