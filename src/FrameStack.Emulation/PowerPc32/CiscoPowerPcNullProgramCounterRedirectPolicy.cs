namespace FrameStack.Emulation.PowerPc32;

public sealed class CiscoPowerPcNullProgramCounterRedirectPolicy
    : IPowerPcNullProgramCounterRedirectPolicy
{
    private const int RepeatTargetSuppressionEvents = 2;
    private const int MaxSuppressedTargetCount = 16;
    private const int LinkRegisterCandidateScore = 900;
    private const int StackFrameChainCandidateScore = 820;
    private const int StackSlotCandidateScore = 760;
    private const int LastKnownCandidateScore = 620;
    private const int FallbackCandidateScore = 420;
    private const int Register30CandidateScore = 300;
    private const int Register31CandidateScore = 280;
    private const uint CandidateCodeAddressFloor = 0x8000_0000;
    private const uint CandidateCodeAddressCeilingExclusive = 0x8200_0000;
    private const uint StackPointerCandidateWindow = 0x0000_4000;
    private static readonly int[] StackProbeOffsets =
    [
        0x04,
        0x08,
        0x0C,
        0x10,
        0x14,
        0x18,
        0x1C,
        0x20,
        -0x04,
        -0x08,
        -0x0C,
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
    private readonly Dictionary<uint, int> _suppressedTargets = new();
    private uint _lastResolvedNonFallbackTarget;
    private uint _lastResolutionTarget;
    private uint _lastResolutionStackPointer;
    private uint _lastResolutionLinkRegister;
    private PowerPcNullProgramCounterRedirectSource _lastResolutionSource;
    private uint _lastKnownWithoutStackSignalTarget;
    private int _lastKnownWithoutStackSignalHits;

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
        TickSuppressedTargets();

        var candidates = new List<RedirectCandidate>(32);
        var stackPointer = registers[1];

        TryAddCandidate(
            candidates,
            registers.Lr,
            readInstructionWord,
            PowerPcNullProgramCounterRedirectSource.LinkRegister,
            stackAddress: null,
            score: LinkRegisterCandidateScore);

        CollectStackCandidates(
            candidates,
            registers,
            readDataWord,
            readInstructionWord);

        var hasStackSignal = HasStackSignal(stackPointer, readDataWord);

        if (_lastResolvedNonFallbackTarget != 0 &&
            (hasStackSignal || ShouldUseLastKnownWithoutStackSignal(registers)))
        {
            TryAddCandidate(
                candidates,
                _lastResolvedNonFallbackTarget,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.LastKnownTarget,
                stackAddress: null,
                score: LastKnownCandidateScore);
        }

        var fallbackScore = hasStackSignal
            ? FallbackCandidateScore - 60
            : FallbackCandidateScore;

        TryAddCandidate(
            candidates,
            registers[30],
            readInstructionWord,
            PowerPcNullProgramCounterRedirectSource.Register30,
            stackAddress: null,
            score: Register30CandidateScore);

        TryAddCandidate(
            candidates,
            registers[31],
            readInstructionWord,
            PowerPcNullProgramCounterRedirectSource.Register31,
            stackAddress: null,
            score: Register31CandidateScore);

        TryAddCandidate(
            candidates,
            _fallbackEntryPoint,
            readInstructionWord,
            PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint,
            stackAddress: null,
            score: fallbackScore);

        RedirectCandidate? bestCandidate = null;
        RedirectCandidate? bestSuppressedCandidate = null;

        foreach (var candidate in candidates)
        {
            var isSuppressed = IsTargetSuppressed(candidate.Resolution.RedirectTarget) ||
                               IsImmediateRepeat(registers, candidate.Resolution);

            if (isSuppressed)
            {
                if (candidate.Resolution.Source != PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint &&
                    IsBetterCandidate(candidate, bestSuppressedCandidate))
                {
                    bestSuppressedCandidate = candidate;
                }

                continue;
            }

            if (IsBetterCandidate(candidate, bestCandidate))
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate.HasValue)
        {
            resolution = bestCandidate.Value.Resolution;
            RememberResolution(registers, resolution, hasStackSignal);
            return true;
        }

        if (bestSuppressedCandidate.HasValue)
        {
            resolution = bestSuppressedCandidate.Value.Resolution;
            RememberResolution(registers, resolution, hasStackSignal);
            return true;
        }

        resolution = default;
        return false;
    }

    private static void TryAddCandidate(
        List<RedirectCandidate> candidates,
        uint candidate,
        Func<uint, uint> readInstructionWord,
        PowerPcNullProgramCounterRedirectSource source,
        uint? stackAddress,
        int score)
    {
        if (!TryUseCandidate(
                candidate,
                readInstructionWord,
                source,
                stackAddress,
                out var resolution))
        {
            return;
        }

        candidates.Add(new RedirectCandidate(resolution, score));
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

    private static void CollectStackCandidates(
        List<RedirectCandidate> candidates,
        PowerPc32RegisterFile registers,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord)
    {
        var stackPointer = registers[1];

        foreach (var offset in StackProbeOffsets)
        {
            var address = unchecked((uint)((int)stackPointer + offset));
            var candidate = readDataWord(address);

            if (IsNearStackPointer(candidate, stackPointer))
            {
                CollectStackFrameChainCandidates(
                    candidates,
                    stackPointer,
                    address,
                    candidate,
                    readDataWord,
                    readInstructionWord);
                continue;
            }

            if (LooksLikeStaleBackChainReturnSlot(
                    stackPointer,
                    address,
                    readDataWord,
                    readInstructionWord))
            {
                continue;
            }

            var score = ComputeStackSlotScore(stackPointer, address, offset, readDataWord);
            TryAddCandidate(
                candidates,
                candidate,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.StackSlot,
                address,
                score);
        }
    }

    private static bool LooksLikeStaleBackChainReturnSlot(
        uint stackPointer,
        uint stackSlotAddress,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord)
    {
        if (stackSlotAddress >= stackPointer ||
            !HasFrameBackChainSignal(stackPointer, stackSlotAddress, readDataWord))
        {
            return false;
        }

        var framePointerCandidate = readDataWord(unchecked(stackSlotAddress - 4));

        if ((framePointerCandidate & 0x3) != 0 ||
            !IsNearStackPointer(framePointerCandidate, stackPointer))
        {
            return false;
        }

        foreach (var probeOffset in StackFrameReturnProbeOffsets)
        {
            var returnSlotAddress = unchecked(framePointerCandidate + (uint)probeOffset);

            if (returnSlotAddress < stackPointer)
            {
                continue;
            }

            var returnAddressCandidate = readDataWord(returnSlotAddress);

            if (IsNearStackPointer(returnAddressCandidate, stackPointer))
            {
                continue;
            }

            if (TryUseCandidate(
                    returnAddressCandidate,
                    readInstructionWord,
                    PowerPcNullProgramCounterRedirectSource.StackSlot,
                    returnSlotAddress,
                    out _))
            {
                return false;
            }
        }

        return true;
    }

    private static int ComputeStackSlotScore(
        uint stackPointer,
        uint stackSlotAddress,
        int stackSlotOffset,
        Func<uint, uint> readDataWord)
    {
        var score = StackSlotCandidateScore;
        var distancePenalty = Math.Min(96, Math.Abs(stackSlotOffset) * 2);
        score -= distancePenalty;

        if (stackSlotOffset is 0x04 or 0x08)
        {
            score += 34;
        }
        else if (stackSlotOffset == 0x14)
        {
            score += 24;
        }
        else if (stackSlotOffset == -0x14)
        {
            score += 4;
        }
        else if (stackSlotOffset is -0x10 or 0x10 or 0x1C)
        {
            score += 12;
        }

        if (HasFrameBackChainSignal(stackPointer, stackSlotAddress, readDataWord))
        {
            score += stackSlotAddress >= stackPointer
                ? 36
                : -12;
        }

        return score;
    }

    private static bool HasFrameBackChainSignal(
        uint stackPointer,
        uint stackSlotAddress,
        Func<uint, uint> readDataWord)
    {
        var previousWordAddress = unchecked(stackSlotAddress - 4);
        var previousWord = readDataWord(previousWordAddress);
        return IsNearStackPointer(previousWord, stackPointer) &&
               (previousWord & 0x3) == 0;
    }

    private static void CollectStackFrameChainCandidates(
        List<RedirectCandidate> candidates,
        uint stackPointer,
        uint stackSlotAddress,
        uint framePointerCandidate,
        Func<uint, uint> readDataWord,
        Func<uint, uint> readInstructionWord)
    {
        if ((framePointerCandidate & 0x3) != 0)
        {
            return;
        }

        foreach (var probeOffset in StackFrameReturnProbeOffsets)
        {
            var returnSlotAddress = unchecked(stackSlotAddress + (uint)probeOffset);

            // Words below the current SP are often stale remnants from already-popped
            // frames; treating them as live returns tends to bounce into reset stubs.
            if (returnSlotAddress < stackPointer)
            {
                continue;
            }

            var returnAddressCandidate = readDataWord(returnSlotAddress);

            if (IsNearStackPointer(returnAddressCandidate, stackPointer))
            {
                continue;
            }

            var score = StackFrameChainCandidateScore + Math.Max(0, 24 - probeOffset);
            TryAddCandidate(
                candidates,
                returnAddressCandidate,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.StackFrameChain,
                returnSlotAddress,
                score);
        }

        foreach (var probeOffset in StackFrameReturnProbeOffsets)
        {
            var returnSlotAddress = unchecked(framePointerCandidate + (uint)probeOffset);

            if (returnSlotAddress < stackPointer)
            {
                continue;
            }

            var returnAddressCandidate = readDataWord(returnSlotAddress);

            if (IsNearStackPointer(returnAddressCandidate, stackPointer))
            {
                continue;
            }

            var score = StackFrameChainCandidateScore + Math.Max(0, 32 - probeOffset);
            TryAddCandidate(
                candidates,
                returnAddressCandidate,
                readInstructionWord,
                PowerPcNullProgramCounterRedirectSource.StackFrameChain,
                returnSlotAddress,
                score);
        }
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

    private bool IsImmediateRepeat(
        PowerPc32RegisterFile registers,
        PowerPcNullProgramCounterRedirectResolution resolution)
    {
        if (_lastResolutionTarget == 0)
        {
            return false;
        }

        var isImmediateRepeat = resolution.RedirectTarget == _lastResolutionTarget &&
                                registers[1] == _lastResolutionStackPointer &&
                                registers.Lr == _lastResolutionLinkRegister &&
                                resolution.Source == _lastResolutionSource;

        if (isImmediateRepeat)
        {
            SuppressTarget(resolution.RedirectTarget, RepeatTargetSuppressionEvents);
        }

        return isImmediateRepeat;
    }

    private bool IsTargetSuppressed(uint redirectTarget)
    {
        return _suppressedTargets.TryGetValue(redirectTarget, out var remainingEvents) &&
               remainingEvents > 0;
    }

    private static bool IsBetterCandidate(RedirectCandidate candidate, RedirectCandidate? currentBest)
    {
        if (!currentBest.HasValue)
        {
            return true;
        }

        if (candidate.Score != currentBest.Value.Score)
        {
            return candidate.Score > currentBest.Value.Score;
        }

        return candidate.Resolution.Source < currentBest.Value.Resolution.Source;
    }

    private void TickSuppressedTargets()
    {
        if (_suppressedTargets.Count == 0)
        {
            return;
        }

        var expiredTargets = new List<uint>();
        var updatedTargets = new List<(uint Target, int RemainingEvents)>();

        foreach (var (target, remainingEvents) in _suppressedTargets)
        {
            var next = remainingEvents - 1;

            if (next <= 0)
            {
                expiredTargets.Add(target);
                continue;
            }

            updatedTargets.Add((target, next));
        }

        foreach (var (target, remainingEvents) in updatedTargets)
        {
            _suppressedTargets[target] = remainingEvents;
        }

        foreach (var target in expiredTargets)
        {
            _suppressedTargets.Remove(target);
        }
    }

    private void SuppressTarget(uint redirectTarget, int events)
    {
        if (redirectTarget == 0 || events <= 0)
        {
            return;
        }

        if (_suppressedTargets.Count >= MaxSuppressedTargetCount &&
            !_suppressedTargets.ContainsKey(redirectTarget))
        {
            using var enumerator = _suppressedTargets.Keys.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _suppressedTargets.Remove(enumerator.Current);
            }
        }

        _suppressedTargets[redirectTarget] = events;
    }

    private void RememberResolution(
        PowerPc32RegisterFile registers,
        PowerPcNullProgramCounterRedirectResolution resolution,
        bool hasStackSignal)
    {
        if (resolution.Source is not PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint and
            not PowerPcNullProgramCounterRedirectSource.Register30 and
            not PowerPcNullProgramCounterRedirectSource.Register31)
        {
            _lastResolvedNonFallbackTarget = resolution.RedirectTarget;
        }

        if (resolution.Source == PowerPcNullProgramCounterRedirectSource.LastKnownTarget &&
            !hasStackSignal)
        {
            if (_lastKnownWithoutStackSignalTarget == resolution.RedirectTarget)
            {
                _lastKnownWithoutStackSignalHits++;
            }
            else
            {
                _lastKnownWithoutStackSignalTarget = resolution.RedirectTarget;
                _lastKnownWithoutStackSignalHits = 1;
            }
        }
        else if (hasStackSignal)
        {
            _lastKnownWithoutStackSignalTarget = 0;
            _lastKnownWithoutStackSignalHits = 0;
        }

        _lastResolutionTarget = resolution.RedirectTarget;
        _lastResolutionStackPointer = registers[1];
        _lastResolutionLinkRegister = registers.Lr;
        _lastResolutionSource = resolution.Source;
    }

    private bool ShouldUseLastKnownWithoutStackSignal(PowerPc32RegisterFile registers)
    {
        if (registers.Lr != 0 ||
            registers[30] != 0 ||
            registers[31] != 0)
        {
            return false;
        }

        return _lastKnownWithoutStackSignalTarget != _lastResolvedNonFallbackTarget ||
               _lastKnownWithoutStackSignalHits == 0;
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

    private readonly record struct RedirectCandidate(
        PowerPcNullProgramCounterRedirectResolution Resolution,
        int Score);
}
