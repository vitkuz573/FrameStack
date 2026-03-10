namespace FrameStack.Api.Contracts;

public sealed class CreateSessionRequest
{
    public Guid ImageId { get; init; }

    public int CpuCores { get; init; } = 2;

    public int MemoryMb { get; init; } = 2048;
}
