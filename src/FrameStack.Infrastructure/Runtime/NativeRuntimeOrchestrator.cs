using System.Collections.Concurrent;
using FrameStack.Application.Abstractions.Runtime;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Runtime;

namespace FrameStack.Infrastructure.Runtime;

public sealed class NativeRuntimeOrchestrator : IRuntimeOrchestrator
{
    private readonly ConcurrentDictionary<string, RuntimeSessionState> _running = new();
    private readonly RuntimeImageBootstrapper _bootstrapper;

    public NativeRuntimeOrchestrator()
    {
        _bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [
                new Elf32ImageLoader(),
                new RawBinaryImageLoader()
            ]);
    }

    public async Task<RuntimeStartResult> StartAsync(
        RuntimeStartRequest request,
        CancellationToken cancellationToken)
    {
        var handle = $"native-{request.SessionId:N}";

        if (_running.ContainsKey(handle))
        {
            throw new InvalidOperationException($"Runtime handle '{handle}' already exists.");
        }

        var imageBytes = await File.ReadAllBytesAsync(request.ArtifactPath, cancellationToken);
        var state = _bootstrapper.Bootstrap(handle, imageBytes, request.MemoryMb);
        var preflight = state.Machine.Run(request.PreflightInstructionBudget);

        if (!_running.TryAdd(handle, state))
        {
            throw new InvalidOperationException($"Runtime handle '{handle}' already exists.");
        }

        return new RuntimeStartResult(
            handle,
            state.BootstrapReport.Format.ToString(),
            state.BootstrapReport.Architecture.ToString(),
            state.BootstrapReport.Endianness.ToString(),
            state.BootstrapReport.EntryPoint,
            state.BootstrapReport.SegmentCount,
            state.BootstrapReport.Summary,
            preflight.ExecutedInstructions,
            preflight.Halted,
            preflight.FinalProgramCounter);
    }

    public Task StopAsync(string runtimeHandle, CancellationToken cancellationToken)
    {
        _running.TryRemove(runtimeHandle, out _);
        return Task.CompletedTask;
    }
}
