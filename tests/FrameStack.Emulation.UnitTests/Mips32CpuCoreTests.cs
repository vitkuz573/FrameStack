using FrameStack.Emulation.Core;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.Mips32;

namespace FrameStack.Emulation.UnitTests;

public sealed class Mips32CpuCoreTests
{
    [Fact]
    public void RunShouldExecuteAddiAndBreak()
    {
        var cpu = new Mips32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x2000);

        memory.WriteUInt32(0x1000, 0x2008_0005); // addi t0, zero, 5
        memory.WriteUInt32(0x1004, 0x0000_000D); // break

        var machine = new EmulationMachine(cpu, memory, entryPoint: 0x1000);
        var summary = machine.Run(instructionBudget: 10);

        Assert.Equal(5u, cpu.Registers[8]);
        Assert.True(summary.Halted);
        Assert.Equal(2, summary.ExecutedInstructions);
    }

    [Fact]
    public void RegisterZeroShouldRemainImmutable()
    {
        var registers = new Mips32RegisterFile();
        registers[0] = 123;

        Assert.Equal(0u, registers[0]);
    }
}
