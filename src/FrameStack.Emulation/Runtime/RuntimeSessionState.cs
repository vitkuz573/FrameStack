using FrameStack.Emulation.Core;

namespace FrameStack.Emulation.Runtime;

public sealed record RuntimeSessionState(
    string Handle,
    EmulationMachine Machine,
    RuntimeBootstrapReport BootstrapReport);
