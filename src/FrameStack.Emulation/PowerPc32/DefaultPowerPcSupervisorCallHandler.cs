namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint QueryInstalledRamService = 0x04;

    private readonly uint _reportedMemoryBytes;

    public DefaultPowerPcSupervisorCallHandler(uint reportedMemoryBytes = 64u * 1024u * 1024u)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == QueryInstalledRamService)
        {
            return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
        }

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }
}
