using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Application.Abstractions.Messaging;

public interface ICommandDispatcher
{
    Task<Result<TResult>> Send<TCommand, TResult>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>;
}
