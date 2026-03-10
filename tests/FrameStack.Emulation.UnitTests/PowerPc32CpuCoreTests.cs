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
    public void AddicShouldTreatR0AsRegularBaseRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x30A0_0001); // addic r5, r0, 1

        cpu.Reset(0x1000);
        cpu.Registers[0] = 0xFFFF_FFFF;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[5]);
        Assert.Equal(0x2000_0000u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void SubficShouldTreatR0AsRegularSourceRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x2120_0000); // subfic r9, r0, 0

        cpu.Reset(0x1000);
        cpu.Registers[0] = 5;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFBu, cpu.Registers[9]);
        Assert.Equal(0u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void MulliShouldTreatR0AsRegularSourceRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x1CC0_0003); // mulli r6, r0, 3

        cpu.Reset(0x1000);
        cpu.Registers[0] = 4;

        cpu.ExecuteCycle(memory);

        Assert.Equal(12u, cpu.Registers[6]);
    }

    [Fact]
    public void StwuShouldStoreOldRsValueBeforeUpdatingBaseRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x9421_FFF8); // stwu r1, -8(r1)

        cpu.Reset(0x1000);
        cpu.Registers[1] = 0x2000;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1FF8u, cpu.Registers[1]);
        Assert.Equal(0x2000u, memory.ReadUInt32(0x1FF8));
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

    [Fact]
    public void SystemCallShouldTrackServiceCodeCounters()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0011); // li r3, 0x11
        memory.WriteUInt32(0x1004, 0x4400_0002); // sc
        memory.WriteUInt32(0x1008, 0x3860_0022); // li r3, 0x22
        memory.WriteUInt32(0x100C, 0x4400_0002); // sc
        memory.WriteUInt32(0x1010, 0x3860_0011); // li r3, 0x11
        memory.WriteUInt32(0x1014, 0x4400_0002); // sc

        cpu.Reset(0x1000);

        for (var step = 0; step < 6; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(2, cpu.SupervisorCallCounters[0x11]);
        Assert.Equal(1, cpu.SupervisorCallCounters[0x22]);
    }

    [Fact]
    public void CompareAndBeqShouldBranchOnEqual()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0000); // li r3, 0
        memory.WriteUInt32(0x1004, 0x2C03_0000); // cmpwi r3, 0
        memory.WriteUInt32(0x1008, 0x4182_000C); // beq 0x1014
        memory.WriteUInt32(0x100C, 0x3880_0001); // li r4, 1
        memory.WriteUInt32(0x1010, 0x4800_0008); // b 0x1018
        memory.WriteUInt32(0x1014, 0x3880_0002); // li r4, 2

        cpu.Reset(0x1000);

        for (var step = 0; step < 4; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0x2u, cpu.Registers[4]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void CompareAndBneShouldBranchOnNotEqual()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0001); // li r3, 1
        memory.WriteUInt32(0x1004, 0x2C03_0000); // cmpwi r3, 0
        memory.WriteUInt32(0x1008, 0x4082_000C); // bne 0x1014
        memory.WriteUInt32(0x100C, 0x3880_0001); // li r4, 1
        memory.WriteUInt32(0x1010, 0x4800_0008); // b 0x1018
        memory.WriteUInt32(0x1014, 0x3880_0002); // li r4, 2

        cpu.Reset(0x1000);

        for (var step = 0; step < 4; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0x2u, cpu.Registers[4]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void SrwiAliasViaRlwinmShouldShiftRightLogical()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x576A_F0BE); // srwi r10, r27, 2

        cpu.Reset(0x1000);
        cpu.Registers[27] = 0x0136_9144;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x004D_A451u, cpu.Registers[10]);
    }

    [Fact]
    public void ClrlwiAliasViaRlwinmShouldClearMostSignificantBits()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x57E0_043E); // clrlwi r0, r31, 16

        cpu.Reset(0x1000);
        cpu.Registers[31] = 0xABCD_1234;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x0000_1234u, cpu.Registers[0]);
    }

    [Fact]
    public void SlwShouldZeroResultWhenShiftAmountIsAtLeastThirtyTwo()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA41830); // slw r4, r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[5] = 0xDEAD_BEEF;
        cpu.Registers[3] = 32;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[4]);
    }

    [Fact]
    public void SrwShouldZeroResultWhenShiftAmountIsAtLeastThirtyTwo()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA41830 + (uint)((536 - 24) << 1)); // srw r4, r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[5] = 0xDEAD_BEEF;
        cpu.Registers[3] = 63;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[4]);
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
