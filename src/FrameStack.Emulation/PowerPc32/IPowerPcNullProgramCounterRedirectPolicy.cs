namespace FrameStack.Emulation.PowerPc32;

public interface IPowerPcNullProgramCounterRedirectPolicy
{
    bool TryResolveRedirectTarget(
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out PowerPcNullProgramCounterRedirectResolution resolution);
}
