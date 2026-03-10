namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        _ = context;

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }
}
