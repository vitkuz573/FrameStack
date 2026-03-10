namespace FrameStack.Application.Abstractions.Runtime;

public interface IRuntimeOrchestrator
{
    Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken);

    Task StopAsync(string runtimeHandle, CancellationToken cancellationToken);
}
