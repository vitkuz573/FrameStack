namespace FrameStack.Emulation.PowerPc32;

public sealed class CiscoPowerPcNullProgramCounterRedirectPolicy
    : IPowerPcNullProgramCounterRedirectPolicy
{
    private const uint CandidateCodeAddressFloor = 0x8000_0000;
    private const uint CandidateCodeAddressCeilingExclusive = 0x8200_0000;
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
    private static readonly int[] StackFrameReturnProbeOffsets =
    [
        0x04,
        0x08,
        0x0C,
        0x10,
        0x14,
        0x18,
        0x1C
    ];

    private readonly uint _fallbackEntryPoint;
    private uint _lastResolvedNonFallbackTarget;

    public CiscoPowerPcNullProgramCounterRedirectPolicy(uint fallbackEntryPoint)
    {
        _fallbackEntryPoint = NormalizeCandidate(fallbackEntryPoint);
    }

    public bool TryResolveRedirectTarget(
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out PowerPcNullProgramCounterRedirectResolution resolution)
    {
        if (TryUseCandidate(
                registers.Lr,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.LinkRegister,
                stackAddress: null,
                out resolution))
        {
            RememberResolution(resolution);
            return true;
        }

        if (TryResolveStackCandidate(registers, readDataWord, readInstructionWord, out resolution))
        {
            RememberResolution(resolution);
            return true;
        }

        if (_lastResolvedNonFallbackTarget != 0 &&
            HasStackSignal(registers[1], readDataWord) &&
            TryUseCandidate(
                _lastResolvedNonFallbackTarget,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.LastKnownTarget,
                stackAddress: null,
                out resolution))
        {
            return true;
        }

        if (TryUseCandidate(
                _fallbackEntryPoint,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint,
                stackAddress: null,
                out resolution))
        {
            return true;
        }

        if (TryUseCandidate(
                registers[30],
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.Register30,
                stackAddress: null,
                out resolution))
        {
            return true;
        }

        if (TryUseCandidate(
                registers[31],
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.Register31,
                stackAddress: null,
                out resolution))
        {
            return true;
        }

        resolution = default;
        return false;
    }

    private void RememberResolution(PowerPcNullProgramCounterRedirectResolution resolution)
    {
        _lastResolvedNonFallbackTarget = resolution.RedirectTarget;
    }

    private static bool HasStackSignal(uint stackPointer, Func<uint, uint> readDataWord)
    {
        var probeAddresses = new uint[]
        {
            unchecked(stackPointer - 0x18),
            unchecked(stackPointer - 0x14),
            unchecked(stackPointer - 0x10),
            stackPointer,
            unchecked(stackPointer + 0x04),
            unchecked(stackPointer + 0x08)
        };

        foreach (var address in probeAddresses)
        {
            if (readDataWord(address) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveStackCandidate(
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out PowerPcNullProgramCounterRedirectResolution resolution)
    {
        var stackPointer = registers[1];

        foreach (var offset in StackProbeOffsets)
        {
            var address = unchecked((uint)((int)stackPointer + offset));
            var candidate = readDataWord(address);

            if (IsNearStackPointer(candidate, stackPointer))
            {
                if (TryResolveStackFrameChainCandidate(
                        stackPointer,
                        address,
                        candidate,
                        readDataWord,
                        readInstructionWord,
                        out resolution))
                {
                    return true;
                }

                continue;
            }

            if (TryUseCandidate(
                    candidate,
                    readInstructionWord,
                    PowerPcNullProgramCounterRedirectSource.StackSlot,
                    address,
                    out resolution))
            {
                return true;
            }
        }

        resolution = default;
        return false;
    }

    private static bool TryResolveStackFrameChainCandidate(
        uint stackPointer,
        uint stackSlotAddress,
        uint framePointerCandidate,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord,
        out PowerPcNullProgramCounterRedirectResolution resolution)
    {
        if ((framePointerCandidate & 0x3) != 0)
        {
            resolution = default;
            return false;
        }

        foreach (var probeOffset in StackFrameReturnProbeOffsets)
        {
            var returnSlotAddress = unchecked(stackSlotAddress + (uint)probeOffset);
            var returnAddressCandidate = readDataWord(returnSlotAddress);

            if (IsNearStackPointer(returnAddressCandidate, stackPointer))
            {
                continue;
            }

            if (TryUseCandidate(
                    returnAddressCandidate,
                    readInstructionWord,
                    PowerPcNullProgramCounterRedirectSource.StackFrameChain,
                    returnSlotAddress,
                    out resolution))
            {
                return true;
            }
        }

        foreach (var probeOffset in StackFrameReturnProbeOffsets)
        {
            var returnSlotAddress = unchecked(framePointerCandidate + (uint)probeOffset);
            var returnAddressCandidate = readDataWord(returnSlotAddress);

            if (IsNearStackPointer(returnAddressCandidate, stackPointer))
            {
                continue;
            }

            if (TryUseCandidate(
                    returnAddressCandidate,
                    readInstructionWord,
                    PowerPcNullProgramCounterRedirectSource.StackFrameChain,
                    returnSlotAddress,
                    out resolution))
            {
                return true;
            }
        }

        resolution = default;
        return false;
    }

    private static bool TryUseCandidate(
        uint candidate,
        Func<uint, uint> readInstructionWord,
        PowerPcNullProgramCounterRedirectSource source,
        uint? stackAddress,
        out PowerPcNullProgramCounterRedirectResolution resolution)
    {
        var redirectTarget = NormalizeCandidate(candidate);

        if (redirectTarget < CandidateCodeAddressFloor ||
            redirectTarget >= CandidateCodeAddressCeilingExclusive)
        {
            resolution = default;
            return false;
        }

        if (!LooksLikeExecutableAddress(redirectTarget, readInstructionWord))
        {
            resolution = default;
            return false;
        }

        resolution = new PowerPcNullProgramCounterRedirectResolution(
            RedirectTarget: redirectTarget,
            Source: source,
            CandidateValue: candidate,
            StackAddress: stackAddress);
        return true;
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
        var firstInstruction = readInstructionWord(address);
        var secondInstruction = readInstructionWord(unchecked(address + 4));

        if (!LooksLikeInstructionWord(firstInstruction) ||
            !LooksLikeInstructionWord(secondInstruction))
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeInstructionWord(uint instructionWord)
    {
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
