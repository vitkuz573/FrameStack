namespace FrameStack.Emulation.PowerPc32;

public interface IPowerPcSupervisorCallHandler
{
    PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context);
}
