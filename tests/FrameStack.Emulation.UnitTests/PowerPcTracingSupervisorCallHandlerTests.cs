using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.UnitTests;

public sealed class PowerPcTracingSupervisorCallHandlerTests
{
    [Fact]
    public void HandleShouldTrackServiceAndSubserviceCounters()
    {
        var inner = new StaticHandler(new PowerPcSupervisorCallResult(ReturnValue: 7));
        var tracing = new PowerPcTracingSupervisorCallHandler(inner);

        tracing.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0x1000,
            ServiceCode: 0x2B,
            Argument0: 0x17,
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        tracing.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0x1004,
            ServiceCode: 0x2B,
            Argument0: 0x17,
            Argument1: 1,
            Argument2: 2,
            Argument3: 3));

        tracing.Handle(new PowerPcSupervisorCallContext(
            ProgramCounter: 0x1008,
            ServiceCode: 0x01,
            Argument0: (uint)'A',
            Argument1: 0,
            Argument2: 0,
            Argument3: 0));

        Assert.Equal(2, tracing.ServiceCounters[0x2B]);
        Assert.Equal(1, tracing.ServiceCounters[0x01]);

        var key = new PowerPcSupervisorSubcallKey(ServiceCode: 0x2B, SubserviceCode: 0x17);
        Assert.Equal(2, tracing.SubserviceCounters[key]);

        Assert.Equal("A", tracing.ConsoleOutput);
        Assert.Equal(3, inner.InvocationCount);
    }

    [Fact]
    public void HandleShouldKeepControlCharactersForReadableConsoleOutput()
    {
        var tracing = new PowerPcTracingSupervisorCallHandler(
            new StaticHandler(new PowerPcSupervisorCallResult(ReturnValue: 0)));

        tracing.Handle(new PowerPcSupervisorCallContext(0x1000, 0x01, (uint)'H', 0, 0, 0));
        tracing.Handle(new PowerPcSupervisorCallContext(0x1004, 0x01, (uint)'i', 0, 0, 0));
        tracing.Handle(new PowerPcSupervisorCallContext(0x1008, 0x01, (uint)'\n', 0, 0, 0));
        tracing.Handle(new PowerPcSupervisorCallContext(0x100C, 0x01, 0x07, 0, 0, 0));

        Assert.Equal("Hi\n", tracing.ConsoleOutput);
    }

    private sealed class StaticHandler(PowerPcSupervisorCallResult result) : IPowerPcSupervisorCallHandler
    {
        public int InvocationCount { get; private set; }

        public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
        {
            _ = context;
            InvocationCount++;
            return result;
        }
    }
}
