namespace FrameStack.Emulation.PowerPc32;

public sealed class CiscoPowerPcNullProgramCounterRedirectPolicy
    : IPowerPcNullProgramCounterRedirectPolicy
{
    private const uint CandidateCodeAddressFloor = 0x8000_0000;
    private readonly uint _fallbackEntryPoint;

    public CiscoPowerPcNullProgramCounterRedirectPolicy(uint fallbackEntryPoint)
    {
        _fallbackEntryPoint = NormalizeCandidate(fallbackEntryPoint);
    }

    public bool TryResolveRedirectTarget(
        PowerPc32RegisterFile registers,
        out uint redirectTarget)
    {
        if (TryUseCandidate(registers.Lr, out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(registers[30], out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(registers[31], out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(_fallbackEntryPoint, out redirectTarget))
        {
            return true;
        }

        redirectTarget = 0;
        return false;
    }

    private static bool TryUseCandidate(uint candidate, out uint redirectTarget)
    {
        redirectTarget = NormalizeCandidate(candidate);
        return redirectTarget >= CandidateCodeAddressFloor;
    }

    private static uint NormalizeCandidate(uint candidate)
    {
        return candidate & 0xFFFF_FFFCu;
    }
}
