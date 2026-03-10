using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.UnitTests;

public sealed class PowerPc32CpuCoreTests
{
    [Fact]
    public void BlrlShouldBranchToPreviousLrValueAndUpdateLinkRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x4E80_0021); // blrl

        cpu.Reset(0x1000);
        cpu.Registers.Lr = 0x1100;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1100u, cpu.ProgramCounter);
        Assert.Equal(0x1004u, cpu.Registers.Lr);
    }

    [Fact]
    public void BctrlShouldBranchToCtrAndUpdateLinkRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x4E80_0421); // bctrl

        cpu.Reset(0x1000);
        cpu.Registers.Ctr = 0x1200;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1200u, cpu.ProgramCounter);
        Assert.Equal(0x1004u, cpu.Registers.Lr);
    }

    [Fact]
    public void OriShouldReadRsAndWriteRa()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x6064_0001); // ori r4, r3, 1

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x10;
        cpu.Registers[4] = 0xDEAD_BEEF;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x11u, cpu.Registers[4]);
        Assert.Equal(0x10u, cpu.Registers[3]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void AddicDotShouldUpdateCr0AndCarryFlag()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x34A0_FFFF); // addic. r5, r0, -1

        cpu.Reset(0x1000);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFFu, cpu.Registers[5]);

        // CR0 should be LT (1000b) for negative signed result.
        var cr0 = (cpu.Registers.Cr >> 28) & 0xF;
        Assert.Equal(0b1000u, cr0);

        // CA should be clear for 0 + 0xFFFFFFFF.
        Assert.Equal(0u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void SystemCallShouldDefaultReturnCodeToZero()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_002B); // li r3, 0x2B
        memory.WriteUInt32(0x1004, 0x4400_0002); // sc

        cpu.Reset(0x1000);
        cpu.ExecuteCycle(memory); // li
        cpu.ExecuteCycle(memory); // sc

        Assert.Equal(0u, cpu.Registers[3]);
        Assert.Equal(0x1008u, cpu.ProgramCounter);
    }

    [Fact]
    public void SystemCallShouldUseInjectedSupervisorHandler()
    {
        var handler = new StaticSupervisorCallHandler(
            new PowerPcSupervisorCallResult(
                ReturnValue: 0xABCD1234,
                Halt: true,
                NextProgramCounter: 0x2000));

        var cpu = new PowerPc32CpuCore(handler);
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);
        memory.WriteUInt32(0x1000, 0x4400_0002); // sc

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x77;
        cpu.Registers[4] = 0x11;
        cpu.Registers[5] = 0x22;
        cpu.Registers[6] = 0x33;
        cpu.Registers[7] = 0x44;

        cpu.ExecuteCycle(memory);

        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD1234u, cpu.Registers[3]);
        Assert.Equal(0x2000u, cpu.ProgramCounter);

        Assert.NotNull(handler.LastContext);
        Assert.Equal(0x1000u, handler.LastContext.Value.ProgramCounter);
        Assert.Equal(0x77u, handler.LastContext.Value.ServiceCode);
        Assert.Equal(0x11u, handler.LastContext.Value.Argument0);
        Assert.Equal(0x22u, handler.LastContext.Value.Argument1);
        Assert.Equal(0x33u, handler.LastContext.Value.Argument2);
        Assert.Equal(0x44u, handler.LastContext.Value.Argument3);
    }

    private sealed class StaticSupervisorCallHandler(PowerPcSupervisorCallResult result)
        : IPowerPcSupervisorCallHandler
    {
        public PowerPcSupervisorCallContext? LastContext { get; private set; }

        public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
        {
            LastContext = context;
            return result;
        }
    }
}
