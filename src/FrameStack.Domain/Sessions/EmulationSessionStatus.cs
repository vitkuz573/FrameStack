namespace FrameStack.Domain.Sessions;

public enum EmulationSessionStatus
{
    Created = 0,
    PreparingImage = 1,
    Ready = 2,
    Running = 3,
    Stopped = 4,
    Failed = 5
}
