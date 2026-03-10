using FrameStack.Domain.Images;
using FrameStack.Domain.Sessions;

namespace FrameStack.Domain.UnitTests.Sessions;

public sealed class EmulationSessionTests
{
    [Fact]
    public void StartStopLifecycleShouldTransitionToExpectedStatuses()
    {
        var session = EmulationSession.Create(
            ImageId.New(),
            new SessionResources(cpuCores: 2, memoryMb: 2048),
            DateTimeOffset.UtcNow);

        session.MarkPreparingImage(DateTimeOffset.UtcNow);
        session.MarkReady("/tmp/ios.bin", DateTimeOffset.UtcNow);
        session.Start("runtime-test", DateTimeOffset.UtcNow);

        Assert.Equal(EmulationSessionStatus.Running, session.Status);
        Assert.Equal("runtime-test", session.RuntimeHandle);

        session.Stop(DateTimeOffset.UtcNow);

        Assert.Equal(EmulationSessionStatus.Stopped, session.Status);
    }
}
