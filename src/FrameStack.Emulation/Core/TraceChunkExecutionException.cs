namespace FrameStack.Emulation.Core;

public sealed class TraceChunkExecutionException : Exception
{
    public TraceChunkExecutionException(
        ExecutionTraceSummary partialTraceSummary,
        uint failureProgramCounter,
        Exception innerException)
        : base(
            $"Trace chunk failed at PC=0x{failureProgramCounter:X8}.",
            innerException)
    {
        PartialTraceSummary = partialTraceSummary;
        FailureProgramCounter = failureProgramCounter;
    }

    public ExecutionTraceSummary PartialTraceSummary { get; }

    public uint FailureProgramCounter { get; }
}
