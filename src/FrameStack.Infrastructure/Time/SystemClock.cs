using FrameStack.Application.Abstractions.Time;

namespace FrameStack.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
