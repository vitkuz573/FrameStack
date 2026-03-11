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
    public void HandleShouldReturnRequestedProbeChunkWhenItFitsReportedMemory()
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
}
