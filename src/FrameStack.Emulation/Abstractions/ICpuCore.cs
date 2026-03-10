namespace FrameStack.Emulation.Abstractions;

public interface ICpuCore
{
    uint ProgramCounter { get; }

    bool Halted { get; }

    void Reset(uint entryPoint);

    void ExecuteCycle(IMemoryBus memoryBus);
}
