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
            address => address == 0x8000_1000 ? 0x3860_0001u : 0x0000_0000u,
            out var target);

        Assert.True(resolved);
        Assert.Equal(0x8000_1000u, target);
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
            address => address == 0x8000_2000 ? 0x4E80_0020u : 0x0000_0000u,
            out var target);

        Assert.True(resolved);
        Assert.Equal(0x8000_2000u, target);
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
            out var target);

        Assert.True(resolved);
        Assert.Equal(0x8000_8000u, target);
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
            address => address == 0x8000_8000 ? 0x4E80_0020u : 0x0000_0000u,
            out var target);

        Assert.True(resolved);
        Assert.Equal(0x8000_8000u, target);
    }
}
