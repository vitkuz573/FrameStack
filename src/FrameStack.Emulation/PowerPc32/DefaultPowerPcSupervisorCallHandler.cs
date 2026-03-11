namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint QueryInstalledRamService = 0x04;
    private const uint DefaultReportedMemoryBytes = 64u * 1024u * 1024u;

    private readonly uint _reportedMemoryBytes;

    public DefaultPowerPcSupervisorCallHandler(
        uint reportedMemoryBytes = DefaultReportedMemoryBytes)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == QueryInstalledRamService)
        {
            if (context.Argument1 == 0 &&
                context.Argument0 > 0 &&
                context.Argument0 <= _reportedMemoryBytes)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: context.Argument0);
            }

            return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
        }

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }
}
