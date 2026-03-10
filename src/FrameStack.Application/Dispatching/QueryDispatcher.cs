using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FrameStack.Application.Dispatching;

public sealed class QueryDispatcher(IServiceProvider serviceProvider) : IQueryDispatcher
{
    public Task<Result<TResult>> Query<TQuery, TResult>(
        TQuery query,
        CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>
    {
        var handler = serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
        return handler.Handle(query, cancellationToken);
    }
}
