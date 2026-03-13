using System.Diagnostics;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

const int FastChunkBudget = 20_000_000;
const int DebugChunkBudget = 500_000;
const double StatusIntervalSeconds = 2.0;

if (!RunCommandHandler.TryParse(args, out var invocation, out var cliExitCode))
{
    return cliExitCode;
}

var imagePath = Path.GetFullPath(invocation!.ImagePath);

if (!File.Exists(imagePath))
{
    Console.WriteLine($"Image file does not exist: {imagePath}");
    return 2;
}

var imageBytes = await File.ReadAllBytesAsync(imagePath);
var analyzer = new BinaryImageAnalyzer();
var inspection = analyzer.Analyze(imageBytes);

Console.WriteLine($"Image: {imagePath}");
Console.WriteLine($"Size: {imageBytes.Length} bytes");
Console.WriteLine($"Format: {inspection.Format}");
Console.WriteLine($"Architecture: {inspection.Architecture}");
Console.WriteLine($"Endianness: {inspection.Endianness}");
Console.WriteLine($"EntryPoint: 0x{inspection.EntryPoint:X8}");
Console.WriteLine($"Segments: {inspection.Sections.Count}");
Console.WriteLine($"Summary: {inspection.Summary}");
Console.WriteLine($"MemoryMb: {invocation.MemoryMb}");
Console.WriteLine("Press Ctrl+C to stop execution.");
Console.WriteLine($"RunnerDebug: {invocation.RunnerDebug}");
#if DEBUG
Console.WriteLine("BuildConfiguration: Debug (slow). Use '-c Release' for maximum speed.");
#else
Console.WriteLine("BuildConfiguration: Release");
#endif

var bootstrapper = new RuntimeImageBootstrapper(
    analyzer,
    [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

try
{
    var state = bootstrapper.Bootstrap(
        runtimeHandle: "runner",
        imageBytes,
        invocation.MemoryMb);

    var powerPcCore = state.CpuCore as PowerPc32CpuCore;

    if (powerPcCore is not null)
    {
        powerPcCore.SupervisorCallHandler = new RunnerConsoleOutputSupervisorCallHandler(
            powerPcCore.SupervisorCallHandler,
            static character => Console.Write(character));
    }

    var cancellationRequested = false;
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationRequested = true;
    };

    var executed = 0L;
    var runStopwatch = Stopwatch.StartNew();
    var statusStopwatch = Stopwatch.StartNew();
    var chunkBudget = invocation.RunnerDebug
        ? DebugChunkBudget
        : FastChunkBudget;
    var stopReason = "Halted";

    while (!state.Machine.Halted && !cancellationRequested)
    {
        var summary = state.Machine.Run(chunkBudget);

        if (summary.ExecutedInstructions > 0)
        {
            executed += summary.ExecutedInstructions;
        }

        if (invocation.RunnerDebug &&
            statusStopwatch.Elapsed.TotalSeconds >= StatusIntervalSeconds)
        {
            var ips = runStopwatch.Elapsed.TotalSeconds > 0
                ? executed / runStopwatch.Elapsed.TotalSeconds
                : 0;

            Console.WriteLine(
                $"[runner] executed={executed} pc=0x{state.Machine.ProgramCounter:X8} ips={ips:F0}");
            statusStopwatch.Restart();
        }

        if (summary.ExecutedInstructions == 0)
        {
            stopReason = "NoForwardProgress";
            break;
        }
    }

    if (cancellationRequested)
    {
        stopReason = "Interrupted";
    }
    else if (state.Machine.Halted)
    {
        stopReason = "Halted";
    }

    runStopwatch.Stop();

    Console.WriteLine();
    Console.WriteLine("Run Summary:");
    Console.WriteLine($"StopReason: {stopReason}");
    Console.WriteLine($"ExecutedInstructions: {executed}");
    Console.WriteLine($"Halted: {state.Machine.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{state.Machine.ProgramCounter:X8}");
    Console.WriteLine($"RunWallClockSeconds: {runStopwatch.Elapsed.TotalSeconds:F3}");

    var runInstructionsPerSecond = runStopwatch.Elapsed.TotalSeconds > 0
        ? executed / runStopwatch.Elapsed.TotalSeconds
        : 0;
    Console.WriteLine($"RunInstructionsPerSecond: {runInstructionsPerSecond:F2}");

    if (powerPcCore is not null)
    {
        Console.WriteLine(
            $"FinalRegisters: R0=0x{powerPcCore.Registers[0]:X8} R1=0x{powerPcCore.Registers[1]:X8} R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
            $"R6=0x{powerPcCore.Registers[6]:X8} R7=0x{powerPcCore.Registers[7]:X8} R8=0x{powerPcCore.Registers[8]:X8} R9=0x{powerPcCore.Registers[9]:X8} " +
            $"R27=0x{powerPcCore.Registers[27]:X8} R29=0x{powerPcCore.Registers[29]:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8} " +
            $"LR=0x{powerPcCore.Registers.Lr:X8} CTR=0x{powerPcCore.Registers.Ctr:X8} CR=0x{powerPcCore.Registers.Cr:X8} XER=0x{powerPcCore.Registers.Xer:X8} MSR=0x{powerPcCore.MachineStateRegister:X8}");

        if (powerPcCore.SupervisorCallCounters.Count > 0)
        {
            Console.WriteLine("SupervisorCallsTotal:");

            foreach (var (serviceCode, hits) in powerPcCore.SupervisorCallCounters
                         .OrderByDescending(entry => entry.Value)
                         .ThenBy(entry => entry.Key)
                         .Take(16))
            {
                Console.WriteLine($"  Service=0x{serviceCode:X8} Hits={hits}");
            }
        }
    }

    return 0;
}
catch (Exception exception)
{
    Console.WriteLine();
    Console.WriteLine("Runner Failed:");
    Console.WriteLine(exception.Message);

    var inner = exception.InnerException;

    while (inner is not null)
    {
        Console.WriteLine("InnerException:");
        Console.WriteLine(inner.Message);
        inner = inner.InnerException;
    }

    return 3;
}

file sealed class RunnerConsoleOutputSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint PutCharacterService = 0x01;
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly Action<char> _consoleCharacterSink;

    public RunnerConsoleOutputSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        Action<char> consoleCharacterSink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _consoleCharacterSink = consoleCharacterSink ?? throw new ArgumentNullException(nameof(consoleCharacterSink));
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == PutCharacterService)
        {
            var value = unchecked((byte)context.Argument0);
            char? character = null;

            switch (value)
            {
                case (byte)'\r':
                case (byte)'\n':
                case (byte)'\t':
                    character = (char)value;
                    break;
            }

            if (!character.HasValue &&
                value is >= 0x20 and <= 0x7E)
            {
                character = (char)value;
            }

            if (character.HasValue)
            {
                _consoleCharacterSink(character.Value);
            }
        }

        return _inner.Handle(context);
    }
}
