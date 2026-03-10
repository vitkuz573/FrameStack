using System.Collections.Concurrent;
using FrameStack.Application.Abstractions.Runtime;

namespace FrameStack.Infrastructure.Runtime;

public sealed class NativeRuntimeOrchestrator : IRuntimeOrchestrator
{
    private readonly ConcurrentDictionary<string, RuntimeStartRequest> _running = new();

    public Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken)
    {
        var handle = $"native-{request.SessionId:N}";

        if (!_running.TryAdd(handle, request))
        {
            throw new InvalidOperationException($"Runtime handle '{handle}' already exists.");
        }

        return Task.FromResult(new RuntimeStartResult(handle));
    }

    public Task StopAsync(string runtimeHandle, CancellationToken cancellationToken)
    {
        _running.TryRemove(runtimeHandle, out _);
        return Task.CompletedTask;
    }
}
