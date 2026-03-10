using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Application.Abstractions.Messaging;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}
