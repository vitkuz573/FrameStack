using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FrameStack.Application.Dispatching;

public sealed class CommandDispatcher(IServiceProvider serviceProvider) : ICommandDispatcher
{
    public Task<Result<TResult>> Send<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>
    {
        var handler = serviceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>();
        return handler.Handle(command, cancellationToken);
    }
}
