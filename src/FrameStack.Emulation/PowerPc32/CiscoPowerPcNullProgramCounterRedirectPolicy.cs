namespace FrameStack.Emulation.PowerPc32;

public sealed class CiscoPowerPcNullProgramCounterRedirectPolicy
    : IPowerPcNullProgramCounterRedirectPolicy
{
    private const uint CandidateCodeAddressFloor = 0x8000_0000;
    private const uint StackPointerCandidateWindow = 0x0000_4000;
    private static readonly int[] StackProbeOffsets =
    [
        0x04,
        0x14,
        0x1C,
        0x20,
        -0x10,
        -0x14,
        -0x18,
        -0x1C,
        -0x20,
        -0x24,
        -0x28,
        0x24,
        0x28,
        0x2C,
        0x30
    ];

    private readonly uint _fallbackEntryPoint;

    public CiscoPowerPcNullProgramCounterRedirectPolicy(uint fallbackEntryPoint)
    {
        _fallbackEntryPoint = NormalizeCandidate(fallbackEntryPoint);
    }

    public bool TryResolveRedirectTarget(
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out uint redirectTarget)
    {
        if (TryUseCandidate(registers.Lr, readInstructionWord, out redirectTarget))
        {
            return true;
        }

        if (TryResolveStackCandidate(registers, readDataWord, readInstructionWord, out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(_fallbackEntryPoint, readInstructionWord, out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(registers[30], readInstructionWord, out redirectTarget))
        {
            return true;
        }

        if (TryUseCandidate(registers[31], readInstructionWord, out redirectTarget))
        {
            return true;
        }

        redirectTarget = 0;
        return false;
    }

    private static bool TryResolveStackCandidate(
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out uint redirectTarget)
    {
        var stackPointer = registers[1];

        foreach (var offset in StackProbeOffsets)
        {
            var address = unchecked((uint)((int)stackPointer + offset));
            var candidate = readDataWord(address);

            if (IsNearStackPointer(candidate, stackPointer))
            {
                continue;
            }

            if (TryUseCandidate(candidate, readInstructionWord, out redirectTarget))
            {
                return true;
            }
        }

        redirectTarget = 0;
        return false;
    }

    private static bool TryUseCandidate(
        uint candidate,
        Func<uint, uint> readInstructionWord,
        out uint redirectTarget)
    {
        redirectTarget = NormalizeCandidate(candidate);

        if (redirectTarget < CandidateCodeAddressFloor)
        {
            return false;
        }

        return LooksLikeExecutableAddress(redirectTarget, readInstructionWord);
    }

    private static bool IsNearStackPointer(uint candidate, uint stackPointer)
    {
        var delta = candidate >= stackPointer
            ? candidate - stackPointer
            : stackPointer - candidate;

        return delta <= StackPointerCandidateWindow;
    }

    private static bool LooksLikeExecutableAddress(
        uint address,
        Func<uint, uint> readInstructionWord)
    {
        var instructionWord = readInstructionWord(address);

        if (instructionWord == 0)
        {
            return false;
        }

        var opcode = instructionWord >> 26;

        return opcode switch
        {
            2 or 7 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or
            24 or 25 or 26 or 27 or 28 or 29 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or
            39 or 40 or 44 or 46 or 47 => true,
            _ => false
        };
    }

    private static uint NormalizeCandidate(uint candidate)
    {
        return candidate & 0xFFFF_FFFCu;
    }
}
