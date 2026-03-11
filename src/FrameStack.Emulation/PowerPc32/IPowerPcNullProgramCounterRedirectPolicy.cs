namespace FrameStack.Emulation.PowerPc32;

public interface IPowerPcNullProgramCounterRedirectPolicy
{
    bool TryResolveRedirectTarget(
        PowerPc32RegisterFile registers,
        out uint redirectTarget);
}
