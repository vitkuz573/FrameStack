using FrameStack.Emulation.Core;
using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Runtime;

public sealed record RuntimeSessionState(
    string Handle,
    EmulationMachine Machine,
    RuntimeBootstrapReport BootstrapReport,
    ICpuCore CpuCore,
    CiscoC2600ConsoleUartIoDevice? CiscoC2600ConsoleUartDevice = null);
