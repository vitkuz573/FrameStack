using FrameStack.Domain.Abstractions;

namespace FrameStack.Domain.Sessions;

public sealed class SessionResources : ValueObject
{
    public SessionResources(int cpuCores, int memoryMb)
    {
        if (cpuCores <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuCores), "CPU cores must be greater than zero.");
        }

        if (memoryMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMb), "Memory must be greater than zero.");
        }

        CpuCores = cpuCores;
        MemoryMb = memoryMb;
    }

    public int CpuCores { get; }

    public int MemoryMb { get; }

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return CpuCores;
        yield return MemoryMb;
    }
}
