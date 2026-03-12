using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.UnitTests;

public sealed class CiscoPowerPcNullProgramCounterRedirectPolicyTests
{
    [Fact]
    public void TryResolveRedirectTargetShouldPreferLinkRegisterWhenExecutable()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers.Lr = 0x8000_1000;
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0,
            address => address == 0x8000_1000 || address == 0x8000_1004 ? 0x3860_0001u : 0x0000_0000u,
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_1000u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.LinkRegister, resolution.Source);
        Assert.Null(resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldUseStackReturnCandidateWhenExecutable()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address == 0x1004 ? 0x8000_2000u : 0u,
            address => address == 0x8000_2000 || address == 0x8000_2004 ? 0x4E80_0020u : 0x0000_0000u,
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_2000u, resolution.RedirectTarget);
        Assert.Contains(
            resolution.Source,
            new[]
            {
                PowerPcNullProgramCounterRedirectSource.StackSlot,
                PowerPcNullProgramCounterRedirectSource.StackFrameChain
            });
        Assert.Equal(0x1004u, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldUseStackFrameChainReturnCandidateWhenExecutable()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address switch
            {
                0x1004 => 0x1000u, // frame-pointer-like value close to SP
                0x1008 => 0x8000_2600u,
                _ => 0u
            },
            address => address == 0x8000_2600 || address == 0x8000_2604 ? 0x4E80_0020u : 0x0000_0000u,
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_2600u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.StackFrameChain, resolution.Source);
        Assert.Equal(0x1008u, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldPreferFallbackBeforeR30AndR31()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[30] = 0x8000_3000;
        registers[31] = 0x8000_4000;
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            _ => 0x4E80_0020u,
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_8000u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint, resolution.Source);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldPreferLastKnownTargetBeforeFallback()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers.Lr = 0x8000_1110;

        var firstResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address == 0x8000_1110 || address == 0x8000_1114 ? 0x4E80_0020u : 0u,
            out var firstResolution);

        Assert.True(firstResolved);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.LinkRegister, firstResolution.Source);
        Assert.Equal(0x8000_1110u, firstResolution.RedirectTarget);

        registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;
        var secondResolved = policy.TryResolveRedirectTarget(
            registers,
            address => address == 0x1000 ? 1u : 0u,
            _ => 0x4E80_0020u,
            out var secondResolution);

        Assert.True(secondResolved);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.LastKnownTarget, secondResolution.Source);
        Assert.Equal(0x8000_1110u, secondResolution.RedirectTarget);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldUseLastKnownTargetWithoutStackSignal()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers.Lr = 0x8000_1110;

        var firstResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address switch
            {
                0x8000_1110 or 0x8000_1114 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out _);

        Assert.True(firstResolved);

        registers = new PowerPc32RegisterFile();
        registers[1] = 0x2000;
        var secondResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address switch
            {
                0x8000_1110 or 0x8000_1114 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var secondResolution);

        Assert.True(secondResolved);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.LastKnownTarget, secondResolution.Source);
        Assert.Equal(0x8000_1110u, secondResolution.RedirectTarget);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldPreferStackCandidateWithFrameBackChainSignal()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address switch
            {
                0x0FF0 => 0x8000_2600u,
                0x100C => 0x8000_2700u,
                0x1008 => 0x1000u, // previous word for 0x100C looks like a live frame back-chain
                _ => 0u
            },
            address => address switch
            {
                0x8000_2600 or 0x8000_2604 => 0x4E80_0020u,
                0x8000_2700 or 0x8000_2704 => 0x4E80_0020u,
                _ => 0u
            },
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_2700u, resolution.RedirectTarget);
        Assert.Equal(0x100Cu, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldIgnoreStaleFrameChainSlotsBelowStackPointer()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address switch
            {
                0x0FE8 => 0x1000u,      // stale frame-pointer-like word below SP
                0x0FEC => 0x8000_8084u, // stale return slot below SP
                0x0FF0 => 0x8003_462Cu, // viable continuation slot
                _ => 0u
            },
            address => address switch
            {
                0x8000_8084 or 0x8000_8088 => 0x4E80_0020u,
                0x8003_462C or 0x8003_4630 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8003_462Cu, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.StackSlot, resolution.Source);
        Assert.Equal(0x0FF0u, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldUseBackChainSlotWhenItIsOnlyAlternativeToFallback()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address switch
            {
                0x0FE8 => 0x1000u,      // frame back-chain signal
                0x0FEC => 0x8000_8084u, // continuation candidate below SP
                _ => 0u
            },
            address => address switch
            {
                0x8000_8084 or 0x8000_8088 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_8084u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.StackSlot, resolution.Source);
        Assert.Equal(0x0FECu, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldConsiderDeepStackSlotWhenNearSlotLooksStale()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address switch
            {
                0x0FE8 => 0x1000u,      // back-chain for stale near return slot (SP-0x14)
                0x0FEC => 0x8000_8084u, // stale near return slot
                0x0FC8 => 0x9000_0000u, // keep deep slot from being treated as frame-chain stale
                0x0FCC => 0x8003_1020u, // deeper return slot at SP-0x34
                _ => 0u
            },
            address => address switch
            {
                0x8000_8084 or 0x8000_8088 => 0x4E80_0020u,
                0x8003_1020 or 0x8003_1024 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8003_1020u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.StackSlot, resolution.Source);
        Assert.Equal(0x0FCCu, resolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldAvoidImmediateRepeatAndUseAlternativeCandidate()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;
        registers.Lr = 0x8000_1110;

        var firstResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address switch
            {
                0x8000_1110 or 0x8000_1114 => 0x4E80_0020u,
                0x8000_2000 or 0x8000_2004 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var firstResolution);

        Assert.True(firstResolved);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.LinkRegister, firstResolution.Source);
        Assert.Equal(0x8000_1110u, firstResolution.RedirectTarget);

        var secondResolved = policy.TryResolveRedirectTarget(
            registers,
            address => address == 0x1004 ? 0x8000_2000u : 0u,
            address => address switch
            {
                0x8000_1110 or 0x8000_1114 => 0x4E80_0020u,
                0x8000_2000 or 0x8000_2004 => 0x4E80_0020u,
                0x8000_8000 or 0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var secondResolution);

        Assert.True(secondResolved);
        Assert.Contains(
            secondResolution.Source,
            new[]
            {
                PowerPcNullProgramCounterRedirectSource.StackSlot,
                PowerPcNullProgramCounterRedirectSource.StackFrameChain
            });
        Assert.Equal(0x8000_2000u, secondResolution.RedirectTarget);
        Assert.Equal(0x1004u, secondResolution.StackAddress);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldRejectNonExecutableStackCandidate()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address == 0x1004 ? 0x8000_2000u : 0u,
            address => address == 0x8000_8000 || address == 0x8000_8004 ? 0x4E80_0020u : 0x0000_0000u,
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_8000u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint, resolution.Source);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldRejectHighDataRangeCandidateEvenIfOpcodesLookValid()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var resolved = policy.TryResolveRedirectTarget(
            registers,
            address => address == 0x1004 ? 0x8263_35DCu : 0u,
            address => address switch
            {
                0x8263_35DC => 0x8263_3604u, // opcode-looking data words
                0x8263_35E0 => 0x8263_35F0u,
                0x8000_8000 => 0x4E80_0020u,
                0x8000_8004 => 0x4E80_0020u,
                _ => 0u
            },
            out var resolution);

        Assert.True(resolved);
        Assert.Equal(0x8000_8000u, resolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint, resolution.Source);
    }

    [Fact]
    public void TryResolveRedirectTargetShouldAllowSuppressedFallbackAsLastResort()
    {
        var policy = new CiscoPowerPcNullProgramCounterRedirectPolicy(0x8000_8000);
        var registers = new PowerPc32RegisterFile();
        registers[1] = 0x1000;

        var firstResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address == 0x8000_8000 || address == 0x8000_8004 ? 0x4E80_0020u : 0u,
            out var firstResolution);

        Assert.True(firstResolved);
        Assert.Equal(0x8000_8000u, firstResolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint, firstResolution.Source);

        var secondResolved = policy.TryResolveRedirectTarget(
            registers,
            _ => 0u,
            address => address == 0x8000_8000 || address == 0x8000_8004 ? 0x4E80_0020u : 0u,
            out var secondResolution);

        Assert.True(secondResolved);
        Assert.Equal(0x8000_8000u, secondResolution.RedirectTarget);
        Assert.Equal(PowerPcNullProgramCounterRedirectSource.FallbackEntryPoint, secondResolution.Source);
    }
}
