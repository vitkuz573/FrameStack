using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.UnitTests;

public sealed class DefaultPowerPcSupervisorCallHandlerTests
{
    [Fact]
    public void HandleShouldReturnReportedRamForServiceFourByDefault()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler(0x0800_0000);

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x04,
            Argument0: 0x1000,
            Argument1: 0x0C00_0000,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(0x0800_0000u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldReturnReportedMemoryForProbeChunkQuery()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler(0x1000_0000);

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x04,
            Argument0: 0x0040_0000,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(0x0040_0000u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldReturnReportedMemoryForSmartInitIoMemorySizingQuery()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler(0x0800_0000);

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x04,
            Argument0: 0x0040_0000,
            Argument1: 0,
            Argument2: 0x8000_5F10,
            Argument3: 0));

        Assert.Equal(0x0800_0000u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldFallbackToReportedMemoryWhenProbeChunkExceedsMemory()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler(0x0800_0000);

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x04,
            Argument0: 0x1000_0000,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(0x0800_0000u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldFallbackToReportedMemoryForNonZeroProbeMode()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler(0x0800_0000);

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x04,
            Argument0: 0x0040_0000,
            Argument1: 0x0C00_0000,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(0x0800_0000u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldReturnZeroForUnknownServices()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler();

        var result = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0x1000,
            ServiceCode: 0x99,
            Argument0: 1,
            Argument1: 2,
            Argument2: 3,
            Argument3: 4));

        Assert.Equal(0u, result.ReturnValue);
    }

    [Fact]
    public void HandleShouldRoundTripIoMemoryProfileUsingSetAndReadServices()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler();

        var setResult = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x3B,
            Argument0: 0x0000_0067, // encoded as 100 + 3
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        var readResult = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x3C,
            Argument0: 0,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(103u, setResult.ReturnValue);
        Assert.Equal(103u, readResult.ReturnValue);
    }

    [Fact]
    public void HandleShouldKeepSmartInitAutoModeWhenSetServiceReceivesZero()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler();

        var setResult = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x3B,
            Argument0: 0,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        var readResult = handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x3C,
            Argument0: 0,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(0u, setResult.ReturnValue);
        Assert.Equal(0u, readResult.ReturnValue);
    }

    [Fact]
    public void HandleShouldSeedIoMemoryProfileDescriptorForBootstrapService()
    {
        var handler = new DefaultPowerPcSupervisorCallHandler();
        var writes = new Dictionary<uint, uint>();
        var writeWord = new PowerPcSupervisorTryWriteUInt32((uint address, uint value) =>
        {
            writes[address] = value;
            return true;
        });

        handler.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0,
            ServiceCode: 0x2C,
            Argument0: 0x1000,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0,
            TryWriteUInt32: writeWord));

        Assert.Equal(0u, writes[0x1000]);
        Assert.Equal(0u, writes[0x1004]);
        Assert.Equal(0x0000_8000u, writes[0x1008]);
    }
}
