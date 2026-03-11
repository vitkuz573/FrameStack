using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

const int DefaultChunkBudget = 200_000_000;
const int DefaultTailLength = 64;
const int DefaultMaxHotSpots = 4096;
const int DefaultMaxMemoryWatchEvents = 1024;
const long DefaultCheckpointAtInstructions = 2_200_000_000;
const uint CheckpointMagic = 0x4653_504E; // "FSPN"
const int CheckpointVersion = 1;
const int CheckpointPageSize = 4096;

if (args.Length == 0)
{
    Console.WriteLine(
        "Usage: dotnet run --project tools/FrameStack.ImageProbe -- <image-path> [instruction-budget] [memory-mb] [timeline-steps] " +
        "[register=value ...] [--checkpoint-file <path>] [--checkpoint-at <instructions>] [--checkpoint-force-rebuild] [--resume-halted] [--chunk-budget <instructions>] " +
        "[--svc-return <service>=<value>] [--svc-return-caller <service>@<caller>=<value>] [--svc-return-caller-hit <service>@<caller>#<hit>=<value>] [--poke32 <address>=<value>] [--stop-at-pc <address>] [--stop-on-svc <service>] [--tail-length <count>] [--save-checkpoint <path>] " +
        "[--window <address>:<before>:<after>] [--watch32 <address>] [--stop-on-watch32-change <address>] " +
        "[--watch32-reg <reg>:<offset>] [--watch32-ea <effective-address>] [--stop-on-watch32-change-reg <reg>:<offset>] [--stop-on-watch32-change-ea <effective-address>] [--global32 <name>=<address>] [--global32-ea <name>=<effective-address>] " +
        "[--count-pc <address>] [--stop-at-pc-hit <address>=<hit-count>] [--max-hotspots <count>] [--full-hotspots] " +
        "[--progress-every <instructions>] [--report-json <path>] [--profile <name>] [--disable-null-pc-redirect]");
    return 1;
}

var imagePath = Path.GetFullPath(args[0]);
var instructionBudget = ParseInstructionBudget(args, 1, 2048);
var memoryMb = ParseOrDefault(args, 2, 256);
var timelineSteps = ParseOrDefault(args, 3, 0);
var cliOptions = ParseProbeCliOptions(args.Skip(4).ToArray());
var registerOverrides = ParseRegisterOverrides(cliOptions.RegisterOverrideTokens);

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

if (registerOverrides.Count > 0)
{
    Console.WriteLine(
        $"RegisterOverrides: {string.Join(", ", registerOverrides.Select(pair => $"{pair.Key}=0x{pair.Value:X8}"))}");
}

if (cliOptions.SupervisorReturnOverrides.Count > 0)
{
    Console.WriteLine(
        $"SupervisorOverrides: {string.Join(", ", cliOptions.SupervisorReturnOverrides.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X8}=0x{pair.Value:X8}"))}");
}

if (cliOptions.SupervisorReturnCallerOverrides.Count > 0)
{
    Console.WriteLine(
        $"SupervisorCallerOverrides: {string.Join(", ", cliOptions.SupervisorReturnCallerOverrides.OrderBy(pair => pair.Key.ServiceCode).ThenBy(pair => pair.Key.CallerProgramCounter).Select(pair => $"0x{pair.Key.ServiceCode:X8}@0x{pair.Key.CallerProgramCounter:X8}=0x{pair.Value:X8}"))}");
}

if (cliOptions.SupervisorReturnCallerHitOverrides.Count > 0)
{
    Console.WriteLine(
        $"SupervisorCallerHitOverrides: {string.Join(", ", cliOptions.SupervisorReturnCallerHitOverrides.OrderBy(pair => pair.Key.ServiceCode).ThenBy(pair => pair.Key.CallerProgramCounter).ThenBy(pair => pair.Key.Hit).Select(pair => $"0x{pair.Key.ServiceCode:X8}@0x{pair.Key.CallerProgramCounter:X8}#{pair.Key.Hit}=0x{pair.Value:X8}"))}");
}

if (cliOptions.MemoryWriteOverrides.Count > 0)
{
    Console.WriteLine(
        $"MemoryOverrides: {string.Join(", ", cliOptions.MemoryWriteOverrides.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X8}=0x{pair.Value:X8}"))}");
}

if (cliOptions.StopAtProgramCounter.HasValue)
{
    Console.WriteLine($"StopAtProgramCounter: 0x{cliOptions.StopAtProgramCounter.Value:X8}");
}

if (cliOptions.StopAtProgramCounterHits.Count > 0)
{
    Console.WriteLine(
        $"StopAtProgramCounterHits: {string.Join(", ", cliOptions.StopAtProgramCounterHits.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X8}={pair.Value}"))}");
}

if (cliOptions.StopOnSupervisorService.HasValue)
{
    Console.WriteLine($"StopOnSupervisorService: 0x{cliOptions.StopOnSupervisorService.Value:X8}");
}

if (cliOptions.TailLength != DefaultTailLength)
{
    Console.WriteLine($"TailLength: {cliOptions.TailLength}");
}

if (cliOptions.AdditionalInstructionWindows.Count > 0)
{
    Console.WriteLine(
        $"AdditionalWindows: {string.Join(", ", cliOptions.AdditionalInstructionWindows.Select(window => $"0x{window.Address:X8}:{window.Before}:{window.After}"))}");
}

if (cliOptions.WatchWordAddresses.Count > 0)
{
    Console.WriteLine(
        $"Watch32: {string.Join(", ", cliOptions.WatchWordAddresses.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.DynamicWatchWordRequests.Count > 0)
{
    Console.WriteLine(
        $"Watch32Reg: {string.Join(", ", cliOptions.DynamicWatchWordRequests.Select(FormatDynamicWatchWordRequest))}");
}

if (cliOptions.WatchWordEffectiveAddresses.Count > 0)
{
    Console.WriteLine(
        $"Watch32EA: {string.Join(", ", cliOptions.WatchWordEffectiveAddresses.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.StopOnWatchWordChangeAddresses.Count > 0)
{
    Console.WriteLine(
        $"StopOnWatch32Change: {string.Join(", ", cliOptions.StopOnWatchWordChangeAddresses.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.DynamicStopOnWatchWordChangeRequests.Count > 0)
{
    Console.WriteLine(
        $"StopOnWatch32ChangeReg: {string.Join(", ", cliOptions.DynamicStopOnWatchWordChangeRequests.Select(FormatDynamicWatchWordRequest))}");
}

if (cliOptions.StopOnWatchWordChangeEffectiveAddresses.Count > 0)
{
    Console.WriteLine(
        $"StopOnWatch32ChangeEA: {string.Join(", ", cliOptions.StopOnWatchWordChangeEffectiveAddresses.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.NamedGlobalAddresses.Count > 0)
{
    Console.WriteLine(
        $"Global32: {string.Join(", ", cliOptions.NamedGlobalAddresses.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => $"{entry.Name}=0x{entry.Address:X8}"))}");
}

if (cliOptions.NamedGlobalEffectiveAddresses.Count > 0)
{
    Console.WriteLine(
        $"Global32EA: {string.Join(", ", cliOptions.NamedGlobalEffectiveAddresses.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => $"{entry.Name}=0x{entry.Address:X8}"))}");
}

if (cliOptions.TrackedProgramCounters.Count > 0)
{
    Console.WriteLine(
        $"CountPc: {string.Join(", ", cliOptions.TrackedProgramCounters.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.ProfileNames.Count > 0)
{
    Console.WriteLine($"Profiles: {string.Join(", ", cliOptions.ProfileNames)}");
}

Console.WriteLine(
    $"MaxHotSpots: {(cliOptions.MaxHotSpots == int.MaxValue ? "full" : cliOptions.MaxHotSpots.ToString(CultureInfo.InvariantCulture))}");

if (cliOptions.ProgressEveryInstructions > 0)
{
    Console.WriteLine($"ProgressEveryInstructions: {cliOptions.ProgressEveryInstructions}");
}

if (cliOptions.CheckpointFilePath is not null)
{
    Console.WriteLine($"CheckpointFile: {cliOptions.CheckpointFilePath}");
}

if (cliOptions.ResumeHalted)
{
    Console.WriteLine("ResumeHalted: True");
}

if (cliOptions.SaveCheckpointFilePath is not null)
{
    Console.WriteLine($"SaveCheckpointFile: {cliOptions.SaveCheckpointFilePath}");
}

if (cliOptions.ReportJsonPath is not null)
{
    Console.WriteLine($"ReportJson: {cliOptions.ReportJsonPath}");
}

if (cliOptions.DisableNullProgramCounterRedirect)
{
    Console.WriteLine("DisableNullPcRedirect: True");
}

if (TryMapVirtualAddressToFileOffset(inspection.Sections, inspection.EntryPoint, out var entryOffset))
{
    Console.WriteLine($"EntryOffset: 0x{entryOffset:X8}");
    Console.WriteLine($"EntryBytes: {FormatBytes(imageBytes.AsSpan(entryOffset, Math.Min(32, imageBytes.Length - entryOffset)))}");
}
else
{
    Console.WriteLine("EntryOffset: <not mapped to a loadable section>");
}

var bootstrapper = new RuntimeImageBootstrapper(
    analyzer,
    [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

try
{
    var state = bootstrapper.Bootstrap(
        runtimeHandle: "probe",
        imageBytes,
        memoryMb);

    long baseExecutedInstructions = 0;

    if (cliOptions.CheckpointFilePath is not null)
    {
        if (!cliOptions.CheckpointForceRebuild &&
            File.Exists(cliOptions.CheckpointFilePath))
        {
            var checkpoint = LoadCheckpoint(cliOptions.CheckpointFilePath);
            RestoreCheckpoint(state, checkpoint, memoryMb);
            baseExecutedInstructions = checkpoint.ExecutedInstructionsFromBoot;
            Console.WriteLine(
                $"Checkpoint: loaded {cliOptions.CheckpointFilePath} " +
                $"(base instructions: {baseExecutedInstructions}).");

            if (cliOptions.ResumeHalted &&
                state.CpuCore is PowerPc32CpuCore resumedPowerPcCore &&
                resumedPowerPcCore.Halted)
            {
                resumedPowerPcCore.SetHalted(false);
                Console.WriteLine("Checkpoint: resumed halted CPU state (--resume-halted).");
            }
        }
        else
        {
            var checkpointAt = cliOptions.CheckpointAtInstructions ?? DefaultCheckpointAtInstructions;
            Console.WriteLine(
                $"Checkpoint: building at {checkpointAt} instructions " +
                $"(chunk={cliOptions.ChunkBudget}).");

            var checkpointExecuted = RunBudgetWithoutTrace(state.Machine, checkpointAt, cliOptions.ChunkBudget);
            var checkpoint = CreateCheckpoint(state, checkpointExecuted, memoryMb);
            SaveCheckpoint(cliOptions.CheckpointFilePath, checkpoint);

            baseExecutedInstructions = checkpointExecuted;
            Console.WriteLine(
                $"Checkpoint: saved {cliOptions.CheckpointFilePath} " +
                $"(base instructions: {baseExecutedInstructions}).");
        }
    }

    ApplyMemoryOverrides(state.Machine.MemoryBus, cliOptions.MemoryWriteOverrides);
    ApplyRegisterOverrides(state.CpuCore, registerOverrides);

    PowerPcTracingSupervisorCallHandler? supervisorTracer = null;
    StopOnSupervisorServicePowerPcSupervisorCallHandler? stopOnSupervisorServiceHandler = null;
    var powerPcCore = state.CpuCore as PowerPc32CpuCore;

    if (powerPcCore is null &&
        (cliOptions.DynamicWatchWordRequests.Count > 0 ||
         cliOptions.WatchWordEffectiveAddresses.Count > 0 ||
         cliOptions.DynamicStopOnWatchWordChangeRequests.Count > 0 ||
         cliOptions.StopOnWatchWordChangeEffectiveAddresses.Count > 0 ||
         cliOptions.NamedGlobalEffectiveAddresses.Count > 0))
    {
        throw new NotSupportedException(
            "PowerPC-specific options (--watch32-reg, --watch32-ea, --stop-on-watch32-change-reg, --stop-on-watch32-change-ea, --global32-ea) are currently supported only for PowerPC32 images.");
    }

    if (powerPcCore is not null)
    {
        if (cliOptions.DisableNullProgramCounterRedirect)
        {
            powerPcCore.NullProgramCounterRedirectEnabled = false;
        }

        if (cliOptions.SupervisorReturnOverrides.Count > 0 ||
            cliOptions.SupervisorReturnCallerOverrides.Count > 0 ||
            cliOptions.SupervisorReturnCallerHitOverrides.Count > 0)
        {
            powerPcCore.SupervisorCallHandler = new OverrideReturnPowerPcSupervisorCallHandler(
                powerPcCore.SupervisorCallHandler,
                cliOptions.SupervisorReturnOverrides,
                cliOptions.SupervisorReturnCallerOverrides,
                cliOptions.SupervisorReturnCallerHitOverrides);
        }

        supervisorTracer = new PowerPcTracingSupervisorCallHandler(powerPcCore.SupervisorCallHandler);
        IPowerPcSupervisorCallHandler activeSupervisorHandler = supervisorTracer;

        if (cliOptions.StopOnSupervisorService.HasValue)
        {
            stopOnSupervisorServiceHandler = new StopOnSupervisorServicePowerPcSupervisorCallHandler(
                activeSupervisorHandler,
                cliOptions.StopOnSupervisorService.Value);
            activeSupervisorHandler = stopOnSupervisorServiceHandler;
        }

        powerPcCore.SupervisorCallHandler = activeSupervisorHandler;
    }

    var supervisorCallCountersBaseline = SnapshotSupervisorCallCounters(powerPcCore);
    var timelineExecuted = 0L;

    if (timelineSteps > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Timeline (first {timelineSteps} step(s)):");

        for (var index = 0; index < timelineSteps; index++)
        {
            var pc = state.Machine.ProgramCounter;
            var (instructionWord, hasImageWord, imageWord) =
                ReadInstructionWord(state.Machine, inspection.Sections, imageBytes, pc, powerPcCore);
            var opcode = (instructionWord >> 26).ToString("X2", CultureInfo.InvariantCulture);
            var source = FormatInstructionSource(instructionWord, hasImageWord, imageWord);

            Console.WriteLine(
                $"  #{index + 1:D4} PC=0x{pc:X8} INSN=0x{instructionWord:X8} OPCODE=0x{opcode} SRC={source}");

            if (powerPcCore is not null)
            {
                Console.WriteLine(
                    $"       R0=0x{powerPcCore.Registers[0]:X8} R1=0x{powerPcCore.Registers[1]:X8} R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} " +
                    $"R5=0x{powerPcCore.Registers[5]:X8} R9=0x{powerPcCore.Registers[9]:X8} R10=0x{powerPcCore.Registers[10]:X8} " +
                    $"R11=0x{powerPcCore.Registers[11]:X8} R27=0x{powerPcCore.Registers[27]:X8} R30=0x{powerPcCore.Registers[30]:X8} " +
                    $"R31=0x{powerPcCore.Registers[31]:X8} LR=0x{powerPcCore.Registers.Lr:X8} CTR=0x{powerPcCore.Registers.Ctr:X8}");
            }

            var stepSummary = state.Machine.Run(1);
            timelineExecuted += stepSummary.ExecutedInstructions;

            if (stepSummary.Halted)
            {
                Console.WriteLine($"  Halted after step {index + 1} at PC=0x{stepSummary.FinalProgramCounter:X8}");
                break;
            }

            if (stepSummary.ExecutedInstructions == 0)
            {
                break;
            }
        }
    }

    var remainingBudget = Math.Max(0L, instructionBudget - timelineExecuted);
    var hotSpotCounters = new Dictionary<uint, long>();
    var trackedProgramCounterHits = cliOptions.TrackedProgramCounters
        .Distinct()
        .ToDictionary(address => address, _ => 0L);
    var runStopwatch = Stopwatch.StartNew();
    var traceRun = RunBudgetWithTrace(
        state.Machine,
        remainingBudget,
        cliOptions.ChunkBudget,
        hotSpotCounters,
        trackedProgramCounterHits,
        cliOptions.MaxHotSpots,
        cliOptions.StopAtProgramCounter,
        cliOptions.StopAtProgramCounterHits,
        cliOptions.TailLength,
        cliOptions.WatchWordAddresses,
        cliOptions.DynamicWatchWordRequests,
        cliOptions.WatchWordEffectiveAddresses,
        cliOptions.StopOnWatchWordChangeAddresses,
        cliOptions.DynamicStopOnWatchWordChangeRequests,
        cliOptions.StopOnWatchWordChangeEffectiveAddresses,
        powerPcCore,
        cliOptions.ProgressEveryInstructions,
        progress =>
        {
            var progressExecutedThisRun = timelineExecuted + progress.ExecutedInstructions;
            var progressExecutedFromBoot = baseExecutedInstructions + progressExecutedThisRun;
            Console.WriteLine(
                $"Progress: Executed={progressExecutedThisRun} " +
                $"FromBoot={progressExecutedFromBoot} " +
                $"Remaining={progress.RemainingInstructions} " +
                $"PC=0x{progress.ProgramCounter:X8} " +
                $"IPS={progress.InstructionsPerSecond:F2} " +
                $"LastChunkStop={progress.LastChunkStopReason}");
        });
    runStopwatch.Stop();

    var executedThisRun = timelineExecuted + traceRun.ExecutedInstructions;
    var executedFromBoot = baseExecutedInstructions + executedThisRun;
    var runInstructionsPerSecond = runStopwatch.Elapsed.TotalSeconds > 0
        ? executedThisRun / runStopwatch.Elapsed.TotalSeconds
        : 0;
    var nullProgramCounterRedirectCount = powerPcCore?.NullProgramCounterRedirectCount ?? 0;
    var supervisorCallCountersTotal = SnapshotSupervisorCallCounters(powerPcCore);
    var supervisorCallCountersDelta = ComputeCounterDelta(
        supervisorCallCountersBaseline,
        supervisorCallCountersTotal);

    Console.WriteLine();
    Console.WriteLine("Preflight Run:");
    Console.WriteLine($"ExecutedInstructions: {executedThisRun}");
    if (baseExecutedInstructions > 0)
    {
        Console.WriteLine($"ExecutedInstructionsFromBoot: {executedFromBoot}");
    }

    Console.WriteLine($"StopReason: {traceRun.StopReason}");

    if (cliOptions.StopAtProgramCounter.HasValue)
    {
        Console.WriteLine(
            $"StopAtProgramCounterReached: {traceRun.StopReason == ExecutionStopReason.StopAtProgramCounter}");
    }

    if (cliOptions.StopAtProgramCounterHits.Count > 0)
    {
        Console.WriteLine(
            $"StopAtProgramCounterHitReached: {traceRun.StopReason == ExecutionStopReason.StopAtProgramCounterHit}");
    }

    var stopOnSupervisorServiceReached = stopOnSupervisorServiceHandler?.StopReached ?? false;

    if (cliOptions.StopOnSupervisorService.HasValue)
    {
        Console.WriteLine($"StopOnSupervisorServiceReached: {stopOnSupervisorServiceReached}");
    }

    if (cliOptions.StopOnWatchWordChangeAddresses.Count > 0 ||
        cliOptions.DynamicStopOnWatchWordChangeRequests.Count > 0)
    {
        Console.WriteLine(
            $"StopOnWatch32ChangeReached: {traceRun.StopReason == ExecutionStopReason.StopOnWatchWordChange}");
    }

    Console.WriteLine($"RunWallClockSeconds: {runStopwatch.Elapsed.TotalSeconds:F3}");
    Console.WriteLine($"RunInstructionsPerSecond: {runInstructionsPerSecond:F2}");

    Console.WriteLine($"Halted: {state.Machine.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{state.Machine.ProgramCounter:X8}");
    Console.WriteLine($"NullProgramCounterRedirects: {nullProgramCounterRedirectCount}");

    if (traceRun.ProgramCounterTail.Count > 0)
    {
        Console.WriteLine("ProgramCounterTail:");

        foreach (var pc in traceRun.ProgramCounterTail)
        {
            var (instructionWord, hasImageWord, imageWord) =
                ReadInstructionWord(state.Machine, inspection.Sections, imageBytes, pc, powerPcCore);
            var source = FormatInstructionSource(instructionWord, hasImageWord, imageWord);

            Console.WriteLine(
                $"  PC=0x{pc:X8} INSN=0x{instructionWord:X8} SRC={source} {DescribeInstruction(pc, instructionWord)}");
        }
    }

    if (traceRun.MemoryWatchEvents.Count > 0)
    {
        Console.WriteLine("MemoryWatchEvents:");

        foreach (var watchEvent in traceRun.MemoryWatchEvents)
        {
            Console.WriteLine(
                $"  PC=0x{watchEvent.ProgramCounter:X8} ADDR=0x{watchEvent.Address:X8} " +
                $"OLD=0x{watchEvent.PreviousValue:X8} NEW=0x{watchEvent.CurrentValue:X8}");
        }
    }

    if (powerPcCore is not null)
    {
        Console.WriteLine(
            $"FinalRegisters: R0=0x{powerPcCore.Registers[0]:X8} R1=0x{powerPcCore.Registers[1]:X8} R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
            $"R6=0x{powerPcCore.Registers[6]:X8} R7=0x{powerPcCore.Registers[7]:X8} " +
            $"R8=0x{powerPcCore.Registers[8]:X8} R9=0x{powerPcCore.Registers[9]:X8} R10=0x{powerPcCore.Registers[10]:X8} " +
            $"R27=0x{powerPcCore.Registers[27]:X8} R29=0x{powerPcCore.Registers[29]:X8} R30=0x{powerPcCore.Registers[30]:X8} " +
            $"R31=0x{powerPcCore.Registers[31]:X8} LR=0x{powerPcCore.Registers.Lr:X8} CTR=0x{powerPcCore.Registers.Ctr:X8} " +
            $"CR=0x{powerPcCore.Registers.Cr:X8} XER=0x{powerPcCore.Registers.Xer:X8} MSR=0x{powerPcCore.MachineStateRegister:X8}");
        PrintSpecialPurposeRegisters(powerPcCore);
    }

    var namedGlobals = ReadNamedGlobals(
        state.Machine,
        cliOptions.NamedGlobalAddresses,
        powerPcCore,
        cliOptions.NamedGlobalEffectiveAddresses);
    PrintNamedGlobals(namedGlobals);
    PrintDynamicWatch(state.Machine, powerPcCore, cliOptions.DynamicWatchWordRequests);
    PrintEffectiveWatch(state.Machine, powerPcCore, cliOptions.WatchWordEffectiveAddresses);
    Console.WriteLine("HotSpots:");

    var topHotSpots = hotSpotCounters
        .OrderByDescending(entry => entry.Value)
        .ThenBy(entry => entry.Key)
        .Take(12)
        .ToArray();

    if (cliOptions.MaxHotSpots <= 0)
    {
        Console.WriteLine("  <disabled>");
    }
    else if (topHotSpots.Length == 0)
    {
        Console.WriteLine("  <empty>");
    }

    foreach (var hotSpot in topHotSpots)
    {
        var (instructionWord, hasImageWord, imageWord) =
            ReadInstructionWord(state.Machine, inspection.Sections, imageBytes, hotSpot.Key, powerPcCore);
        var majorOpcode = instructionWord >> 26;
        var source = FormatInstructionSource(instructionWord, hasImageWord, imageWord);

        Console.WriteLine(
            $"  PC=0x{hotSpot.Key:X8} Hits={hotSpot.Value} INSN=0x{instructionWord:X8} OPCODE=0x{majorOpcode:X2} SRC={source}");
    }

    if (cliOptions.TrackedProgramCounters.Count > 0)
    {
        Console.WriteLine("TrackedProgramCounterHits:");

        foreach (var trackedPc in cliOptions.TrackedProgramCounters.Distinct().OrderBy(address => address))
        {
            trackedProgramCounterHits.TryGetValue(trackedPc, out var hits);
            var (instructionWord, hasImageWord, imageWord) =
                ReadInstructionWord(state.Machine, inspection.Sections, imageBytes, trackedPc, powerPcCore);
            var source = FormatInstructionSource(instructionWord, hasImageWord, imageWord);

            Console.WriteLine(
                $"  PC=0x{trackedPc:X8} Hits={hits} INSN=0x{instructionWord:X8} SRC={source} {DescribeInstruction(trackedPc, instructionWord)}");
        }
    }

    Console.WriteLine("InstructionWindows:");
    PrintInstructionWindow(
        state.Machine,
        inspection.Sections,
        imageBytes,
        state.Machine.ProgramCounter,
        before: 5,
        after: 5,
        label: "FinalProgramCounter",
        powerPcCore);

    if (topHotSpots.Length > 0 && topHotSpots[0].Key != state.Machine.ProgramCounter)
    {
        PrintInstructionWindow(
            state.Machine,
            inspection.Sections,
            imageBytes,
            topHotSpots[0].Key,
            before: 5,
            after: 5,
            label: "TopHotSpot",
            powerPcCore);
    }

    if (cliOptions.AdditionalInstructionWindows.Count > 0)
    {
        foreach (var window in cliOptions.AdditionalInstructionWindows)
        {
            PrintInstructionWindow(
                state.Machine,
                inspection.Sections,
                imageBytes,
                window.Address,
                window.Before,
                window.After,
                label: $"Window@0x{window.Address:X8}",
                powerPcCore);
        }
    }

    if (powerPcCore is not null)
    {
        var linkRegister = powerPcCore.Registers.Lr;
        PrintInstructionWindow(
            state.Machine,
            inspection.Sections,
            imageBytes,
            linkRegister,
            before: 16,
            after: 8,
            label: "LinkRegister",
            powerPcCore);

        if (linkRegister >= 4)
        {
            PrintInstructionWindow(
                state.Machine,
                inspection.Sections,
                imageBytes,
                linkRegister - 4,
                before: 24,
                after: 12,
                label: "LinkReturnSite",
                powerPcCore);
        }
    }

    PrintSupervisorCallCounters("SupervisorCallsDelta", supervisorCallCountersDelta);
    PrintSupervisorCallCounters("SupervisorCallsTotal", supervisorCallCountersTotal);

    if (supervisorTracer is not null &&
        supervisorTracer.SubserviceCounters.Count > 0)
    {
        Console.WriteLine("SupervisorSubcalls:");

        foreach (var (subcall, hits) in supervisorTracer.SubserviceCounters
                     .OrderByDescending(entry => entry.Value)
                     .ThenBy(entry => entry.Key.ServiceCode)
                     .ThenBy(entry => entry.Key.SubserviceCode)
                     .Take(12))
        {
            Console.WriteLine(
                $"  Service=0x{subcall.ServiceCode:X8} Sub=0x{subcall.SubserviceCode:X8} Hits={hits}");
        }
    }

    if (supervisorTracer is not null &&
        supervisorTracer.CallTrace.Count > 0)
    {
        Console.WriteLine("SupervisorTrace:");

        foreach (var entry in supervisorTracer.CallTrace.Take(40))
        {
            var nextPc = entry.NextProgramCounter.HasValue
                ? $"0x{entry.NextProgramCounter.Value:X8}"
                : "-";
            var callerPc = entry.CallerProgramCounter != 0
                ? $"0x{entry.CallerProgramCounter:X8}"
                : "-";

            Console.WriteLine(
                $"  PC=0x{entry.ProgramCounter:X8} SVC=0x{entry.ServiceCode:X8} " +
                $"LR=0x{entry.LinkRegister:X8} CALLER={callerPc} " +
                $"A0=0x{entry.Argument0:X8} A1=0x{entry.Argument1:X8} " +
                $"A2=0x{entry.Argument2:X8} A3=0x{entry.Argument3:X8} " +
                $"RET=0x{entry.ReturnValue:X8} HALT={entry.Halt} NEXT={nextPc}");
        }
    }

    if (supervisorTracer is not null &&
        supervisorTracer.ConsoleOutput.Length > 0)
    {
        Console.WriteLine("ConsoleOutput:");
        Console.WriteLine(supervisorTracer.ConsoleOutput);
    }

    if (cliOptions.ReportJsonPath is not null)
    {
        var probeReport = CreateProbeReport(
            imagePath,
            imageBytes.Length,
            inspection,
            instructionBudget,
            memoryMb,
            baseExecutedInstructions,
            executedThisRun,
            executedFromBoot,
            runStopwatch.Elapsed.TotalSeconds,
            runInstructionsPerSecond,
            state,
            traceRun,
            stopOnSupervisorServiceReached,
            topHotSpots,
            trackedProgramCounterHits,
            namedGlobals,
            cliOptions.ProfileNames,
            nullProgramCounterRedirectCount,
            supervisorCallCountersTotal,
            supervisorCallCountersDelta,
            supervisorTracer?.CallTrace ?? Array.Empty<PowerPcSupervisorCallTraceEntry>(),
            supervisorTracer?.ConsoleOutput ?? string.Empty);
        SaveProbeReport(cliOptions.ReportJsonPath, probeReport);
        Console.WriteLine($"ReportJsonSaved: {cliOptions.ReportJsonPath}");
    }

    if (cliOptions.SaveCheckpointFilePath is not null)
    {
        var finalCheckpoint = CreateCheckpoint(state, executedFromBoot, memoryMb);
        SaveCheckpoint(cliOptions.SaveCheckpointFilePath, finalCheckpoint);
        Console.WriteLine($"FinalCheckpointSaved: {cliOptions.SaveCheckpointFilePath}");
    }

    return 0;
}
catch (Exception exception)
{
    Console.WriteLine();
    Console.WriteLine("Preflight Run Failed:");
    Console.WriteLine(exception.Message);

    if (exception.InnerException is not null)
    {
        Console.WriteLine("InnerException:");
        Console.WriteLine(exception.InnerException.Message);
    }

    return 3;
}

static int ParseOrDefault(string[] input, int index, int fallback)
{
    if (input.Length <= index)
    {
        return fallback;
    }

    return int.TryParse(input[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : fallback;
}

static long ParseInstructionBudget(string[] input, int index, long fallback)
{
    if (input.Length <= index)
    {
        return fallback;
    }

    var token = input[index];

    if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed > 0
            ? parsed
            : fallback;
    }

    return fallback;
}

static ProbeCliOptions ParseProbeCliOptions(string[] tokens)
{
    string? checkpointFilePath = null;
    string? saveCheckpointFilePath = null;
    string? reportJsonPath = null;
    long? checkpointAtInstructions = null;
    var checkpointForceRebuild = false;
    var resumeHalted = false;
    var chunkBudget = DefaultChunkBudget;
    var maxHotSpots = DefaultMaxHotSpots;
    var progressEveryInstructions = 0L;
    var disableNullProgramCounterRedirect = false;
    uint? stopAtProgramCounter = null;
    uint? stopOnSupervisorService = null;
    var stopAtProgramCounterHits = new Dictionary<uint, long>();
    var tailLength = DefaultTailLength;
    var supervisorReturnOverrides = new Dictionary<uint, uint>();
    var supervisorReturnCallerOverrides = new Dictionary<SupervisorCallsiteKey, uint>();
    var supervisorReturnCallerHitOverrides = new Dictionary<SupervisorCallsiteHitKey, uint>();
    var memoryWriteOverrides = new Dictionary<uint, uint>();
    var additionalInstructionWindows = new List<InstructionWindowRequest>();
    var watchWordAddresses = new List<uint>();
    var dynamicWatchWordRequests = new List<DynamicWatchWordRequest>();
    var watchWordEffectiveAddresses = new List<uint>();
    var stopOnWatchWordChangeAddresses = new HashSet<uint>();
    var dynamicStopOnWatchWordChangeRequests = new List<DynamicWatchWordRequest>();
    var stopOnWatchWordChangeEffectiveAddresses = new HashSet<uint>();
    var trackedProgramCounters = new List<uint>();
    var namedGlobalAddresses = new List<NamedAddress>();
    var namedGlobalEffectiveAddresses = new List<NamedAddress>();
    var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var registerOverrideTokens = new List<string>();

    for (var index = 0; index < tokens.Length; index++)
    {
        var token = tokens[index];

        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            registerOverrideTokens.Add(token);
            continue;
        }

        var optionName = token;
        string? optionValue = null;
        var separator = token.IndexOf('=');

        if (separator >= 0)
        {
            optionName = token[..separator];
            optionValue = token[(separator + 1)..];
        }

        switch (optionName)
        {
            case "--checkpoint-file":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                checkpointFilePath = Path.GetFullPath(optionValue);
                break;
            case "--checkpoint-at":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                checkpointAtInstructions = ParsePositiveLongOption(optionName, optionValue);
                break;
            case "--save-checkpoint":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                saveCheckpointFilePath = Path.GetFullPath(optionValue);
                break;
            case "--checkpoint-force-rebuild":
                checkpointForceRebuild = true;
                break;
            case "--resume-halted":
                resumeHalted = true;
                break;
            case "--chunk-budget":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                chunkBudget = ParsePositiveIntOption(optionName, optionValue);
                break;
            case "--max-hotspots":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                maxHotSpots = ParseNonNegativeIntOption(optionName, optionValue);
                break;
            case "--full-hotspots":
                maxHotSpots = int.MaxValue;
                break;
            case "--progress-every":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                progressEveryInstructions = ParsePositiveLongOption(optionName, optionValue);
                break;
            case "--report-json":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                reportJsonPath = Path.GetFullPath(optionValue);
                break;
            case "--profile":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                profileNames.Add(optionValue.Trim());
                break;
            case "--disable-null-pc-redirect":
                disableNullProgramCounterRedirect = true;
                break;
            case "--svc-return":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var supervisorOverride = ParseSupervisorReturnOverride(optionValue);
                supervisorReturnOverrides[supervisorOverride.ServiceCode] = supervisorOverride.ReturnValue;
                break;
            case "--svc-return-caller":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var supervisorCallerOverride = ParseSupervisorReturnCallerOverride(optionValue);
                supervisorReturnCallerOverrides[supervisorCallerOverride.Callsite] = supervisorCallerOverride.ReturnValue;
                break;
            case "--svc-return-caller-hit":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var supervisorCallerHitOverride = ParseSupervisorReturnCallerHitOverride(optionValue);
                supervisorReturnCallerHitOverrides[supervisorCallerHitOverride.CallsiteHit] = supervisorCallerHitOverride.ReturnValue;
                break;
            case "--poke32":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var memoryOverride = ParseSupervisorReturnOverride(optionValue);
                memoryWriteOverrides[memoryOverride.ServiceCode] = memoryOverride.ReturnValue;
                break;
            case "--stop-at-pc":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                stopAtProgramCounter = ParseUInt32Flexible(optionValue);
                break;
            case "--stop-at-pc-hit":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var stopAtPcHit = ParseProgramCounterHitStop(optionName, optionValue);
                stopAtProgramCounterHits[stopAtPcHit.ProgramCounter] = stopAtPcHit.RequiredHits;
                break;
            case "--stop-on-svc":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                stopOnSupervisorService = ParseUInt32Flexible(optionValue);
                break;
            case "--tail-length":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                tailLength = ParseNonNegativeIntOption(optionName, optionValue);
                break;
            case "--window":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                additionalInstructionWindows.Add(ParseInstructionWindowRequest(optionValue));
                break;
            case "--watch32":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                watchWordAddresses.Add(ParseUInt32Flexible(optionValue));
                break;
            case "--watch32-reg":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                AddDistinct(dynamicWatchWordRequests, ParseDynamicWatchWordRequest(optionName, optionValue));
                break;
            case "--watch32-ea":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                AddDistinct(watchWordEffectiveAddresses, ParseUInt32Flexible(optionValue));
                break;
            case "--stop-on-watch32-change":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                stopOnWatchWordChangeAddresses.Add(ParseUInt32Flexible(optionValue));
                break;
            case "--stop-on-watch32-change-reg":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                AddDistinct(dynamicStopOnWatchWordChangeRequests, ParseDynamicWatchWordRequest(optionName, optionValue));
                break;
            case "--stop-on-watch32-change-ea":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                stopOnWatchWordChangeEffectiveAddresses.Add(ParseUInt32Flexible(optionValue));
                break;
            case "--global32":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                AddDistinct(namedGlobalAddresses, ParseNamedAddress(optionName, optionValue));
                break;
            case "--global32-ea":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                AddDistinct(namedGlobalEffectiveAddresses, ParseNamedAddress(optionName, optionValue));
                break;
            case "--count-pc":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                trackedProgramCounters.Add(ParseUInt32Flexible(optionValue));
                break;
            default:
                throw new ArgumentException($"Unsupported option '{optionName}'.");
        }
    }

    ApplyProbeProfiles(
        profileNames,
        additionalInstructionWindows,
        watchWordAddresses,
        trackedProgramCounters,
        namedGlobalAddresses,
        dynamicWatchWordRequests);

    return new ProbeCliOptions(
        checkpointFilePath,
        saveCheckpointFilePath,
        reportJsonPath,
        checkpointAtInstructions,
        checkpointForceRebuild,
        resumeHalted,
        chunkBudget,
        maxHotSpots,
        progressEveryInstructions,
        stopAtProgramCounter,
        stopAtProgramCounterHits,
        stopOnSupervisorService,
        tailLength,
        supervisorReturnOverrides,
        supervisorReturnCallerOverrides,
        supervisorReturnCallerHitOverrides,
        memoryWriteOverrides,
        additionalInstructionWindows,
        watchWordAddresses,
        dynamicWatchWordRequests,
        watchWordEffectiveAddresses,
        stopOnWatchWordChangeAddresses.ToArray(),
        dynamicStopOnWatchWordChangeRequests,
        stopOnWatchWordChangeEffectiveAddresses.ToArray(),
        trackedProgramCounters,
        namedGlobalAddresses,
        namedGlobalEffectiveAddresses,
        profileNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
        disableNullProgramCounterRedirect,
        registerOverrideTokens.ToArray());
}

static void ApplyProbeProfiles(
    IReadOnlySet<string> profileNames,
    IList<InstructionWindowRequest> additionalInstructionWindows,
    IList<uint> watchWordAddresses,
    IList<uint> trackedProgramCounters,
    IList<NamedAddress> namedGlobalAddresses,
    IList<DynamicWatchWordRequest> dynamicWatchWordRequests)
{
    foreach (var profileNameRaw in profileNames)
    {
        var profileName = profileNameRaw.Trim();

        if (profileName.Equals("c2600-ram-probe", StringComparison.OrdinalIgnoreCase) ||
            profileName.Equals("c2600-boot-probe", StringComparison.OrdinalIgnoreCase) ||
            profileName.Equals("cisco-c2600-boot", StringComparison.OrdinalIgnoreCase))
        {
            const uint baseGlobal = 0x82F40774;

            for (var index = 0; index <= 11; index++)
            {
                AddDistinct(watchWordAddresses, unchecked(baseGlobal + (uint)(index * 4)));
            }

            for (var offset = 0x774; offset <= 0x7A0; offset += 4)
            {
                AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(9, offset));
            }

            AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(10, 0x77C));
            AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(10, 0x780));

            var descriptorWatchAddresses = new uint[]
            {
                0x82F406A8,
                0x82F406AC,
                0x82F406B0,
                0x82F406B4,
                0x82F406B8,
                0x82F406BC,
                0x82F406C0,
                0x82F406C4,
                0x82F406C8,
                0x82F406CC,
            };

            foreach (var address in descriptorWatchAddresses)
            {
                AddDistinct(watchWordAddresses, address);
            }

            var trackedPcs = new uint[]
            {
                0x816E2928,
                0x816E292C,
                0x816E29BC,
                0x816E2DD4,
                0x816E2F70,
            };

            foreach (var trackedPc in trackedPcs)
            {
                AddDistinct(trackedProgramCounters, trackedPc);
            }

            AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E2928, 12, 12));
            AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E29BC, 8, 8));
            AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E2DD4, 8, 8));

            var c2600Globals = new NamedAddress[]
            {
                new("g_0x8000BCEC", 0x8000BCEC),
                new("g_0x8000BCF0", 0x8000BCF0),
                new("g_0x8000BCF4", 0x8000BCF4),
                new("g_0x8000BCF8", 0x8000BCF8),
                new("g_0x8000BCFC", 0x8000BCFC),
                new("g_0x8000BD00", 0x8000BD00),
                new("g_0x8000BD04", 0x8000BD04),
                new("g_0x8000BD4C", 0x8000BD4C),
                new("g_0x8000BD50", 0x8000BD50),
                new("g_0x8000BD54", 0x8000BD54),
                new("g_0x80090780", 0x80090780),
                new("g_0x80090784", 0x80090784),
                new("g_0x82F40774", 0x82F40774),
                new("g_0x82F40778", 0x82F40778),
                new("g_0x82F4077C", 0x82F4077C),
                new("g_0x82F40780", 0x82F40780),
                new("g_0x82F40784", 0x82F40784),
                new("g_0x82F40788", 0x82F40788),
                new("g_0x82F4078C", 0x82F4078C),
                new("g_0x82F40790", 0x82F40790),
                new("g_0x82F40794", 0x82F40794),
                new("g_0x82F40798", 0x82F40798),
                new("g_0x82F4079C", 0x82F4079C),
                new("g_0x82F407A0", 0x82F407A0),
            };

            foreach (var global in c2600Globals)
            {
                AddDistinct(namedGlobalAddresses, global);
            }
            continue;
        }

        throw new ArgumentException(
            $"Unsupported profile '{profileNameRaw}'. Supported profiles: c2600-ram-probe, c2600-boot-probe, cisco-c2600-boot.");
    }

}

static void AddDistinct<T>(ICollection<T> destination, T value)
{
    if (destination.Contains(value))
    {
        return;
    }

    destination.Add(value);
}

static string ReadRequiredOptionValue(string[] tokens, ref int index, string optionName)
{
    if (index + 1 >= tokens.Length)
    {
        throw new ArgumentException($"Option '{optionName}' requires a value.");
    }

    index++;
    return tokens[index];
}

static long ParsePositiveLongOption(string optionName, string value)
{
    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
        parsed <= 0)
    {
        throw new ArgumentException($"Option '{optionName}' requires a positive integer value.");
    }

    return parsed;
}

static int ParsePositiveIntOption(string optionName, string value)
{
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
        parsed <= 0)
    {
        throw new ArgumentException($"Option '{optionName}' requires a positive integer value.");
    }

    return parsed;
}

static int ParseNonNegativeIntOption(string optionName, string value)
{
    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
        parsed < 0)
    {
        throw new ArgumentException($"Option '{optionName}' requires a non-negative integer value.");
    }

    return parsed;
}

static (uint ServiceCode, uint ReturnValue) ParseSupervisorReturnOverride(string token)
{
    var separator = token.IndexOf('=');

    if (separator <= 0 || separator == token.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid supervisor override '{token}'. Expected format '<service>=<value>'.");
    }

    var serviceCode = ParseUInt32Flexible(token[..separator]);
    var returnValue = ParseUInt32Flexible(token[(separator + 1)..]);

    return (serviceCode, returnValue);
}

static (SupervisorCallsiteKey Callsite, uint ReturnValue) ParseSupervisorReturnCallerOverride(string token)
{
    var equalsSeparator = token.IndexOf('=');

    if (equalsSeparator <= 0 || equalsSeparator == token.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid supervisor caller override '{token}'. Expected format '<service>@<caller>=<value>'.");
    }

    var callsiteToken = token[..equalsSeparator];
    var returnValue = ParseUInt32Flexible(token[(equalsSeparator + 1)..]);
    var atSeparator = callsiteToken.IndexOf('@');

    if (atSeparator <= 0 || atSeparator == callsiteToken.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid supervisor caller override '{token}'. Expected format '<service>@<caller>=<value>'.");
    }

    var serviceCode = ParseUInt32Flexible(callsiteToken[..atSeparator]);
    var callerProgramCounter = ParseUInt32Flexible(callsiteToken[(atSeparator + 1)..]);
    return (new SupervisorCallsiteKey(serviceCode, callerProgramCounter), returnValue);
}

static (SupervisorCallsiteHitKey CallsiteHit, uint ReturnValue) ParseSupervisorReturnCallerHitOverride(string token)
{
    var equalsSeparator = token.IndexOf('=');

    if (equalsSeparator <= 0 || equalsSeparator == token.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid supervisor caller-hit override '{token}'. Expected format '<service>@<caller>#<hit>=<value>'.");
    }

    var callsiteToken = token[..equalsSeparator];
    var returnValue = ParseUInt32Flexible(token[(equalsSeparator + 1)..]);
    var hashSeparator = callsiteToken.IndexOf('#');

    if (hashSeparator <= 0 || hashSeparator == callsiteToken.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid supervisor caller-hit override '{token}'. Expected format '<service>@<caller>#<hit>=<value>'.");
    }

    var callsitePart = callsiteToken[..hashSeparator];
    var hitToken = callsiteToken[(hashSeparator + 1)..];
    var callerOverride = ParseSupervisorReturnCallerOverride($"{callsitePart}=0");
    var hit = ParsePositiveLongOption("--svc-return-caller-hit", hitToken);

    if (hit > int.MaxValue)
    {
        throw new ArgumentException("Option '--svc-return-caller-hit' requires hit count <= 2147483647.");
    }

    var key = new SupervisorCallsiteHitKey(
        callerOverride.Callsite.ServiceCode,
        callerOverride.Callsite.CallerProgramCounter,
        (int)hit);
    return (key, returnValue);
}

static (uint ProgramCounter, long RequiredHits) ParseProgramCounterHitStop(string optionName, string token)
{
    var separator = token.IndexOf('=');

    if (separator <= 0 || separator == token.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid '{optionName}' value '{token}'. Expected format '<address>=<hit-count>'.");
    }

    var programCounter = ParseUInt32Flexible(token[..separator]);
    var requiredHits = ParsePositiveLongOption(optionName, token[(separator + 1)..]);
    return (programCounter, requiredHits);
}

static InstructionWindowRequest ParseInstructionWindowRequest(string token)
{
    var parts = token.Split(':', StringSplitOptions.TrimEntries);

    if (parts.Length != 3)
    {
        throw new ArgumentException(
            $"Invalid window specification '{token}'. Expected '<address>:<before>:<after>'.");
    }

    var address = ParseUInt32Flexible(parts[0]);
    var before = ParseNonNegativeIntOption("--window", parts[1]);
    var after = ParseNonNegativeIntOption("--window", parts[2]);

    return new InstructionWindowRequest(address, before, after);
}

static Dictionary<string, uint> ParseRegisterOverrides(string[] tokens)
{
    var overrides = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

    foreach (var token in tokens)
    {
        var separator = token.IndexOf('=');

        if (separator <= 0 || separator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid register override '{token}'. Expected format '<register>=<value>'.");
        }

        var registerName = token[..separator].Trim();
        var valueToken = token[(separator + 1)..].Trim();
        var value = ParseUInt32Flexible(valueToken);

        overrides[registerName] = value;
    }

    return overrides;
}

static uint ParseUInt32Flexible(string input)
{
    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return uint.Parse(input[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    return uint.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture);
}

static int ParseInt32Flexible(string input)
{
    var token = input.Trim();
    var negative = token.Length > 0 && token[0] == '-';
    var numericPart = negative ? token[1..] : token;

    if (numericPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        var parsed = int.Parse(numericPart[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return negative ? -parsed : parsed;
    }

    return int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
}

static NamedAddress ParseNamedAddress(string optionName, string token)
{
    var separator = token.IndexOf('=');

    if (separator <= 0 || separator == token.Length - 1)
    {
        throw new ArgumentException(
            $"Invalid '{optionName}' value '{token}'. Expected format '<name>=<address>'.");
    }

    var name = token[..separator].Trim();

    if (string.IsNullOrEmpty(name))
    {
        throw new ArgumentException($"Invalid '{optionName}' value '{token}'. Global name cannot be empty.");
    }

    var address = ParseUInt32Flexible(token[(separator + 1)..].Trim());
    return new NamedAddress(name, address);
}

static DynamicWatchWordRequest ParseDynamicWatchWordRequest(string optionName, string token)
{
    var parts = token.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length != 2)
    {
        throw new ArgumentException(
            $"Invalid '{optionName}' value '{token}'. Expected format '<register>:<offset>'.");
    }

    var registerToken = parts[0];

    if (registerToken.Length < 2 ||
        (registerToken[0] != 'r' && registerToken[0] != 'R') ||
        !int.TryParse(registerToken[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var registerIndex) ||
        registerIndex is < 0 or > 31)
    {
        throw new ArgumentException(
            $"Invalid register '{registerToken}' in '{optionName}'. Expected r0..r31.");
    }

    var offset = ParseInt32Flexible(parts[1]);
    return new DynamicWatchWordRequest(registerIndex, offset);
}

static string FormatDynamicWatchWordRequest(DynamicWatchWordRequest request)
{
    var offsetPrefix = request.Offset >= 0 ? "+" : "-";
    var offsetHex = Math.Abs((long)request.Offset).ToString("X", CultureInfo.InvariantCulture);
    return $"r{request.RegisterIndex}{offsetPrefix}0x{offsetHex}";
}

static IReadOnlyList<uint> ResolveWatchWordAddresses(
    IReadOnlyList<uint> staticAddresses,
    IReadOnlyList<DynamicWatchWordRequest> dynamicRequests,
    IReadOnlyList<uint> effectiveAddresses,
    PowerPc32CpuCore? powerPcCore)
{
    if (dynamicRequests.Count == 0 &&
        effectiveAddresses.Count == 0)
    {
        return staticAddresses;
    }

    if (powerPcCore is null)
    {
        return staticAddresses;
    }

    var resolvedAddresses = new List<uint>(staticAddresses.Count + dynamicRequests.Count);

    foreach (var address in staticAddresses)
    {
        AddDistinct(resolvedAddresses, address);
    }

    foreach (var request in dynamicRequests)
    {
        var registerValue = powerPcCore.Registers[request.RegisterIndex];
        var resolvedAddress = unchecked(registerValue + (uint)request.Offset);
        AddDistinct(resolvedAddresses, resolvedAddress);
    }

    foreach (var effectiveAddress in effectiveAddresses)
    {
        var translatedAddress = powerPcCore.TranslateDataAddressForDebug(effectiveAddress);
        AddDistinct(resolvedAddresses, translatedAddress);
    }

    return resolvedAddresses;
}

static void ApplyRegisterOverrides(
    FrameStack.Emulation.Abstractions.ICpuCore cpuCore,
    IReadOnlyDictionary<string, uint> registerOverrides)
{
    if (registerOverrides.Count == 0)
    {
        return;
    }

    if (cpuCore is not PowerPc32CpuCore powerPc)
    {
        throw new NotSupportedException(
            "Register overrides are currently implemented only for PowerPC32 images.");
    }

    foreach (var (nameRaw, value) in registerOverrides)
    {
        var name = nameRaw.Trim();

        if (name.Equals("lr", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Lr = value;
            continue;
        }

        if (name.Equals("ctr", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Ctr = value;
            continue;
        }

        if (name.Equals("cr", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Cr = value;
            continue;
        }

        if (name.Equals("xer", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Xer = value;
            continue;
        }

        if (name.Equals("msr", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.WriteMachineStateRegister(value);
            continue;
        }

        if (name.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Pc = value;
            continue;
        }

        if (name.Length >= 4 &&
            name.StartsWith("spr", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var spr) &&
            spr is >= 0 and <= 4095)
        {
            powerPc.WriteSpecialPurposeRegister(spr, value);
            continue;
        }

        if (name.Length >= 2 &&
            (name[0] == 'r' || name[0] == 'R') &&
            int.TryParse(name[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index is >= 0 and < 32)
        {
            powerPc.Registers[index] = value;
            continue;
        }

        throw new ArgumentException($"Unsupported register override key '{nameRaw}'.");
    }
}

static void ApplyMemoryOverrides(
    FrameStack.Emulation.Abstractions.IMemoryBus memoryBus,
    IReadOnlyDictionary<uint, uint> memoryWriteOverrides)
{
    foreach (var (address, value) in memoryWriteOverrides)
    {
        memoryBus.WriteUInt32(address, value);
    }
}

static long RunBudgetWithoutTrace(
    EmulationMachine machine,
    long budget,
    int chunkBudget)
{
    var executed = 0L;
    var remaining = Math.Max(0, budget);

    while (remaining > 0 && !machine.Halted)
    {
        var currentChunk = (int)Math.Min(remaining, chunkBudget);
        var summary = machine.Run(currentChunk);
        executed += summary.ExecutedInstructions;
        remaining -= summary.ExecutedInstructions;

        if (summary.ExecutedInstructions == 0)
        {
            break;
        }
    }

    return executed;
}

static TracedRunResult RunBudgetWithTrace(
    EmulationMachine machine,
    long budget,
    int chunkBudget,
    IDictionary<uint, long> hotSpotCounters,
    IDictionary<uint, long> trackedProgramCounterHits,
    int maxHotSpots,
    uint? stopAtProgramCounter,
    IReadOnlyDictionary<uint, long> stopAtProgramCounterHits,
    int tailLength,
    IReadOnlyList<uint> staticWatchWordAddresses,
    IReadOnlyList<DynamicWatchWordRequest> dynamicWatchWordRequests,
    IReadOnlyList<uint> effectiveWatchWordAddresses,
    IReadOnlyList<uint> stopOnWatchWordChangeAddresses,
    IReadOnlyList<DynamicWatchWordRequest> dynamicStopOnWatchWordChangeRequests,
    IReadOnlyList<uint> stopOnWatchWordChangeEffectiveAddresses,
    PowerPc32CpuCore? powerPcCore,
    long progressEveryInstructions,
    Action<TraceProgress>? progressCallback)
{
    var executed = 0L;
    var remaining = Math.Max(0, budget);
    var stopReason = ExecutionStopReason.None;
    var tail = tailLength > 0
        ? new List<uint>(tailLength)
        : null;
    var memoryWatchEvents = staticWatchWordAddresses.Count > 0 ||
                            dynamicWatchWordRequests.Count > 0 ||
                            effectiveWatchWordAddresses.Count > 0 ||
                            stopOnWatchWordChangeAddresses.Count > 0 ||
                            dynamicStopOnWatchWordChangeRequests.Count > 0 ||
                            stopOnWatchWordChangeEffectiveAddresses.Count > 0
        ? new List<MemoryWatchTraceEntry>()
        : null;
    HashSet<uint>? trackedProgramCounterSet = null;
    var runStopwatch = Stopwatch.StartNew();
    var nextProgressCheckpoint = progressEveryInstructions > 0
        ? progressEveryInstructions
        : long.MaxValue;
    IReadOnlyList<uint> currentWatchWordAddresses = staticWatchWordAddresses;

    if (trackedProgramCounterHits.Count > 0)
    {
        trackedProgramCounterSet = trackedProgramCounterHits.Keys.ToHashSet();
    }

    while (remaining > 0 && !machine.Halted)
    {
        var currentChunk = (int)Math.Min(remaining, chunkBudget);
        currentWatchWordAddresses = ResolveWatchWordAddresses(
            staticWatchWordAddresses,
            dynamicWatchWordRequests,
            effectiveWatchWordAddresses,
            powerPcCore);
        var currentStopOnWatchWordChangeAddresses = ResolveWatchWordAddresses(
            stopOnWatchWordChangeAddresses,
            dynamicStopOnWatchWordChangeRequests,
            stopOnWatchWordChangeEffectiveAddresses,
            powerPcCore);
        var currentTraceWatchWordAddresses = currentWatchWordAddresses
            .Concat(currentStopOnWatchWordChangeAddresses)
            .Distinct()
            .ToArray();
        var stopOnWatchWordChangeSet = currentStopOnWatchWordChangeAddresses.Count > 0
            ? currentStopOnWatchWordChangeAddresses.ToHashSet()
            : null;
        ExecutionTraceSummary traceSummary;

        try
        {
            traceSummary = machine.RunWithTrace(
                currentChunk,
                maxHotSpots: maxHotSpots,
                tailLength: tailLength,
                stopAtProgramCounter: stopAtProgramCounter,
                stopAtProgramCounterHits: stopAtProgramCounterHits,
                trackedProgramCounters: trackedProgramCounterSet,
                watchWordAddresses: currentTraceWatchWordAddresses,
                stopOnWatchWordChangeAddresses: stopOnWatchWordChangeSet,
                maxMemoryWatchEvents: DefaultMaxMemoryWatchEvents);
        }
        catch (Exception exception)
        {
            if (exception is TraceChunkExecutionException traceChunkException)
            {
                var partialTrace = traceChunkException.PartialTraceSummary;
                var partialExecuted = partialTrace.Summary.ExecutedInstructions;

                if (partialExecuted > 0)
                {
                    executed += partialExecuted;
                    remaining -= partialExecuted;
                }

                MergeHotSpots(hotSpotCounters, partialTrace.HotSpots);
                MergeTrackedProgramCounterHits(trackedProgramCounterHits, partialTrace.TrackedProgramCounterHits);
                MergeProgramCounterTail(tail, partialTrace.ProgramCounterTail, tailLength);
                MergeMemoryWatchEvents(memoryWatchEvents, partialTrace.MemoryWatchEvents, DefaultMaxMemoryWatchEvents);
            }

            var failurePc = exception is TraceChunkExecutionException traceException
                ? traceException.FailureProgramCounter
                : machine.ProgramCounter;
            var failureInstruction = machine.ReadUInt32(failurePc);
            var errorDetails = new StringBuilder();
            errorDetails.AppendLine("Trace chunk failed.");
            errorDetails.AppendLine(
                string.Format(CultureInfo.InvariantCulture, "ExecutedBeforeFailure: {0}", executed));
            errorDetails.AppendLine(
                string.Format(CultureInfo.InvariantCulture, "FailureProgramCounter: 0x{0:X8}", failurePc));
            errorDetails.AppendLine(
                string.Format(CultureInfo.InvariantCulture, "FailureInstruction: 0x{0:X8}", failureInstruction));
            errorDetails.AppendLine("FailureWindow:");

            for (var offset = -16; offset <= 16; offset += 4)
            {
                var address = unchecked((uint)((int)failurePc + offset));
                var marker = offset == 0 ? "=>" : "  ";
                errorDetails.AppendLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} 0x{1:X8}: 0x{2:X8}",
                        marker,
                        address,
                        machine.ReadUInt32(address)));
            }

            if (tail is { Count: > 0 })
            {
                errorDetails.AppendLine("ProgramCounterTailBeforeFailure:");
                var start = Math.Max(0, tail.Count - Math.Min(tail.Count, 64));

                for (var index = start; index < tail.Count; index++)
                {
                    errorDetails.AppendLine(
                        string.Format(CultureInfo.InvariantCulture, "  PC=0x{0:X8}", tail[index]));
                }
            }

            if (memoryWatchEvents is { Count: > 0 })
            {
                errorDetails.AppendLine("MemoryWatchBeforeFailure:");
                var start = Math.Max(0, memoryWatchEvents.Count - Math.Min(memoryWatchEvents.Count, 16));

                for (var index = start; index < memoryWatchEvents.Count; index++)
                {
                    var watchEvent = memoryWatchEvents[index];
                    errorDetails.AppendLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "  PC=0x{0:X8} ADDR=0x{1:X8} OLD=0x{2:X8} NEW=0x{3:X8}",
                            watchEvent.ProgramCounter,
                            watchEvent.Address,
                            watchEvent.PreviousValue,
                            watchEvent.CurrentValue));
                }
            }

            if (currentTraceWatchWordAddresses.Length > 0)
            {
                errorDetails.AppendLine("WatchedWordValuesAtFailure:");
                var count = Math.Min(currentTraceWatchWordAddresses.Length, 32);

                for (var index = 0; index < count; index++)
                {
                    var address = currentTraceWatchWordAddresses[index];
                    errorDetails.AppendLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "  0x{0:X8}=0x{1:X8}",
                            address,
                            machine.ReadUInt32(address)));
                }
            }

            if (powerPcCore is not null)
            {
                var registers = powerPcCore.Registers;
                var instructionTlbEntries = powerPcCore.GetInstructionTlbEntries()
                    .OrderBy(entry => entry.Index)
                    .ToArray();
                var dataTlbEntries = powerPcCore.GetDataTlbEntries()
                    .OrderBy(entry => entry.Index)
                    .ToArray();

                errorDetails.AppendLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "FailurePowerPcMsr: 0x{0:X8}",
                        powerPcCore.MachineStateRegister));
                errorDetails.AppendLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "FailurePowerPcRegisters: R0=0x{0:X8} R1=0x{1:X8} R3=0x{2:X8} R4=0x{3:X8} R5=0x{4:X8} R6=0x{5:X8} R7=0x{6:X8} R8=0x{7:X8} R9=0x{8:X8} R10=0x{9:X8} R27=0x{10:X8} R29=0x{11:X8} R30=0x{12:X8} R31=0x{13:X8} LR=0x{14:X8} CTR=0x{15:X8} CR=0x{16:X8} XER=0x{17:X8}",
                        registers[0],
                        registers[1],
                        registers[3],
                        registers[4],
                        registers[5],
                        registers[6],
                        registers[7],
                        registers[8],
                        registers[9],
                        registers[10],
                        registers[27],
                        registers[29],
                        registers[30],
                        registers[31],
                        registers.Lr,
                        registers.Ctr,
                        registers.Cr,
                        registers.Xer));

                var nonZeroSpr = powerPcCore.ExtendedSpecialPurposeRegisters
                    .Where(entry => entry.Value != 0)
                    .OrderBy(entry => entry.Key)
                    .ToArray();

                if (nonZeroSpr.Length > 0)
                {
                    errorDetails.AppendLine("FailurePowerPcSpr:");

                    foreach (var (spr, value) in nonZeroSpr)
                    {
                        errorDetails.AppendLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "  SPR[{0}]=0x{1:X8}",
                                spr,
                                value));
                        }
                }

                if (instructionTlbEntries.Length > 0)
                {
                    errorDetails.AppendLine("FailureInstructionTlb:");

                    foreach (var entry in instructionTlbEntries)
                    {
                        var pageSize = DecodeMpc8xxPageSizeForDebug(entry.TableWalkControl, entry.RealPageNumber);
                        var hasMatch = TryTranslateViaMpc8xxTlbEntry(entry, failurePc, out var translatedAddress);
                        var valid = (entry.EffectivePageNumber & 0x0000_0200) != 0;
                        errorDetails.AppendLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "  IDX={0:D2} EPN=0x{1:X8} RPN=0x{2:X8} TWC=0x{3:X8} PAGE=0x{4:X8} VALID={5} MATCH={6}{7}",
                                entry.Index,
                                entry.EffectivePageNumber,
                                entry.RealPageNumber,
                                entry.TableWalkControl,
                                pageSize,
                                valid ? 1 : 0,
                                hasMatch ? 1 : 0,
                                hasMatch
                                    ? string.Format(CultureInfo.InvariantCulture, " TRANSLATED=0x{0:X8}", translatedAddress)
                                    : string.Empty));
                    }
                }

                if (dataTlbEntries.Length > 0)
                {
                    errorDetails.AppendLine("FailureDataTlb:");

                    foreach (var entry in dataTlbEntries)
                    {
                        var pageSize = DecodeMpc8xxPageSizeForDebug(entry.TableWalkControl, entry.RealPageNumber);
                        var hasMatch = TryTranslateViaMpc8xxTlbEntry(entry, powerPcCore.Registers[1], out var translatedAddress);
                        var valid = (entry.EffectivePageNumber & 0x0000_0200) != 0;
                        errorDetails.AppendLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "  IDX={0:D2} EPN=0x{1:X8} RPN=0x{2:X8} TWC=0x{3:X8} PAGE=0x{4:X8} VALID={5} STACK_MATCH={6}{7}",
                                entry.Index,
                                entry.EffectivePageNumber,
                                entry.RealPageNumber,
                                entry.TableWalkControl,
                                pageSize,
                                valid ? 1 : 0,
                                hasMatch ? 1 : 0,
                                hasMatch
                                    ? string.Format(CultureInfo.InvariantCulture, " STACK_TRANSLATED=0x{0:X8}", translatedAddress)
                                    : string.Empty));
                    }
                }

                errorDetails.AppendLine("FailureStackWords:");
                var stackPointer = registers[1];

                for (var offset = -16; offset <= 32; offset += 4)
                {
                    var effectiveAddress = unchecked((uint)((int)stackPointer + offset));
                    var marker = offset == 0x14 ? "*" : " ";
                    var directWord = machine.ReadUInt32(effectiveAddress);
                    var translated = TryTranslateViaMpc8xxTlbEntries(dataTlbEntries, effectiveAddress, out var translatedAddress);
                    var translatedWord = translated
                        ? machine.ReadUInt32(translatedAddress)
                        : 0u;

                    errorDetails.AppendLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} EA=0x{1:X8} DIRECT=0x{2:X8}{3}",
                            marker,
                            effectiveAddress,
                            directWord,
                            translated
                                ? string.Format(
                                    CultureInfo.InvariantCulture,
                                    " PA=0x{0:X8} VALUE=0x{1:X8}",
                                    translatedAddress,
                                    translatedWord)
                                : string.Empty));
                }
            }

            throw new InvalidOperationException(errorDetails.ToString(), exception);
        }

        var chunkExecuted = traceSummary.Summary.ExecutedInstructions;

        executed += chunkExecuted;
        remaining -= chunkExecuted;
        MergeHotSpots(hotSpotCounters, traceSummary.HotSpots);
        MergeTrackedProgramCounterHits(trackedProgramCounterHits, traceSummary.TrackedProgramCounterHits);
        MergeProgramCounterTail(tail, traceSummary.ProgramCounterTail, tailLength);
        MergeMemoryWatchEvents(memoryWatchEvents, traceSummary.MemoryWatchEvents, DefaultMaxMemoryWatchEvents);

        if (progressEveryInstructions > 0 &&
            progressCallback is not null &&
            (executed >= nextProgressCheckpoint ||
             traceSummary.StopReason != ExecutionStopReason.InstructionBudgetReached))
        {
            var instructionsPerSecond = runStopwatch.Elapsed.TotalSeconds > 0
                ? executed / runStopwatch.Elapsed.TotalSeconds
                : 0;

            progressCallback(new TraceProgress(
                executed,
                remaining,
                machine.ProgramCounter,
                instructionsPerSecond,
                traceSummary.StopReason));

            while (nextProgressCheckpoint <= executed)
            {
                nextProgressCheckpoint += progressEveryInstructions;
            }
        }

        if (chunkExecuted == 0)
        {
            break;
        }

        if (traceSummary.StopReason != ExecutionStopReason.InstructionBudgetReached &&
            traceSummary.StopReason != ExecutionStopReason.None)
        {
            stopReason = traceSummary.StopReason;
            break;
        }
    }

    if (stopReason == ExecutionStopReason.None)
    {
        if (machine.Halted)
        {
            stopReason = ExecutionStopReason.Halted;
        }
        else if (remaining <= 0)
        {
            stopReason = ExecutionStopReason.InstructionBudgetReached;
        }
    }

    return new TracedRunResult(
        executed,
        tail?.ToArray() ?? Array.Empty<uint>(),
        stopReason,
        memoryWatchEvents?.ToArray() ?? Array.Empty<MemoryWatchTraceEntry>());
}

static ProbeCheckpoint CreateCheckpoint(
    RuntimeSessionState state,
    long executedInstructionsFromBoot,
    int memoryMb)
{
    if (state.CpuCore is not PowerPc32CpuCore cpu)
    {
        throw new NotSupportedException("Checkpointing currently supports only PowerPC32 CPU state.");
    }

    if (state.Machine.MemoryBus is not SparseMemoryBus memory)
    {
        throw new NotSupportedException("Checkpointing currently supports only SparseMemoryBus.");
    }

    return new ProbeCheckpoint(
        executedInstructionsFromBoot,
        memoryMb,
        cpu.CreateSnapshot(),
        memory.CreateSnapshot());
}

static void RestoreCheckpoint(
    RuntimeSessionState state,
    ProbeCheckpoint checkpoint,
    int configuredMemoryMb)
{
    if (state.CpuCore is not PowerPc32CpuCore cpu)
    {
        throw new NotSupportedException("Checkpoint restore currently supports only PowerPC32 CPU state.");
    }

    if (state.Machine.MemoryBus is not SparseMemoryBus memory)
    {
        throw new NotSupportedException("Checkpoint restore currently supports only SparseMemoryBus.");
    }

    if (checkpoint.MemoryMb > configuredMemoryMb)
    {
        throw new InvalidOperationException(
            $"Checkpoint requires {checkpoint.MemoryMb} MB, but runtime was configured with {configuredMemoryMb} MB.");
    }

    memory.RestoreSnapshot(checkpoint.MemoryPages);
    cpu.RestoreSnapshot(checkpoint.CpuSnapshot);
}

static void SaveCheckpoint(string checkpointFilePath, ProbeCheckpoint checkpoint)
{
    var directory = Path.GetDirectoryName(checkpointFilePath);

    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    using var fileStream = File.Create(checkpointFilePath);
    using var compressionStream = new GZipStream(fileStream, CompressionLevel.Optimal);
    using var writer = new BinaryWriter(compressionStream);

    writer.Write(CheckpointMagic);
    writer.Write(CheckpointVersion);
    writer.Write(checkpoint.ExecutedInstructionsFromBoot);
    writer.Write(checkpoint.MemoryMb);
    WriteCpuSnapshot(writer, checkpoint.CpuSnapshot);
    WriteMemorySnapshot(writer, checkpoint.MemoryPages);
}

static ProbeCheckpoint LoadCheckpoint(string checkpointFilePath)
{
    using var fileStream = File.OpenRead(checkpointFilePath);
    using var compressionStream = new GZipStream(fileStream, CompressionMode.Decompress);
    using var reader = new BinaryReader(compressionStream);

    var magic = reader.ReadUInt32();

    if (magic != CheckpointMagic)
    {
        throw new InvalidOperationException(
            $"Unsupported checkpoint header in '{checkpointFilePath}'.");
    }

    var version = reader.ReadInt32();

    if (version != CheckpointVersion)
    {
        throw new InvalidOperationException(
            $"Unsupported checkpoint version {version} in '{checkpointFilePath}'.");
    }

    var executedInstructions = reader.ReadInt64();
    var memoryMb = reader.ReadInt32();
    var cpuSnapshot = ReadCpuSnapshot(reader);
    var memorySnapshot = ReadMemorySnapshot(reader);

    return new ProbeCheckpoint(
        executedInstructions,
        memoryMb,
        cpuSnapshot,
        memorySnapshot);
}

static void WriteCpuSnapshot(BinaryWriter writer, PowerPc32CpuSnapshot snapshot)
{
    if (snapshot.GeneralPurposeRegisters.Length != 32)
    {
        throw new InvalidOperationException(
            $"CPU snapshot has invalid GPR length {snapshot.GeneralPurposeRegisters.Length}, expected 32.");
    }

    foreach (var registerValue in snapshot.GeneralPurposeRegisters)
    {
        writer.Write(registerValue);
    }

    writer.Write(snapshot.ProgramCounter);
    writer.Write(snapshot.LinkRegister);
    writer.Write(snapshot.CounterRegister);
    writer.Write(snapshot.ConditionRegister);
    writer.Write(snapshot.FixedPointExceptionRegister);
    writer.Write(snapshot.Halted);
    writer.Write(snapshot.MachineStateRegister);
    writer.Write(snapshot.TimeBaseCounter);
    writer.Write(snapshot.LastMpc8xxControlSpr);

    writer.Write(snapshot.ExtendedSpecialPurposeRegisters.Count);

    foreach (var (spr, value) in snapshot.ExtendedSpecialPurposeRegisters.OrderBy(entry => entry.Key))
    {
        writer.Write(spr);
        writer.Write(value);
    }

    writer.Write(snapshot.SupervisorCallCounters.Count);

    foreach (var (serviceCode, hits) in snapshot.SupervisorCallCounters.OrderBy(entry => entry.Key))
    {
        writer.Write(serviceCode);
        writer.Write(hits);
    }

    WriteTlbEntries(writer, snapshot.InstructionTlbEntries);
    WriteTlbEntries(writer, snapshot.DataTlbEntries);
}

static PowerPc32CpuSnapshot ReadCpuSnapshot(BinaryReader reader)
{
    var gpr = new uint[32];

    for (var index = 0; index < gpr.Length; index++)
    {
        gpr[index] = reader.ReadUInt32();
    }

    var pc = reader.ReadUInt32();
    var lr = reader.ReadUInt32();
    var ctr = reader.ReadUInt32();
    var cr = reader.ReadUInt32();
    var xer = reader.ReadUInt32();
    var halted = reader.ReadBoolean();
    var msr = reader.ReadUInt32();
    var timeBase = reader.ReadUInt64();
    var lastMpc8xxControlSpr = reader.ReadInt32();

    var extendedSprCount = reader.ReadInt32();
    var extendedSpr = new Dictionary<int, uint>(extendedSprCount);

    for (var index = 0; index < extendedSprCount; index++)
    {
        var spr = reader.ReadInt32();
        var value = reader.ReadUInt32();
        extendedSpr[spr] = value;
    }

    var supervisorCounterCount = reader.ReadInt32();
    var supervisorCounters = new Dictionary<uint, long>(supervisorCounterCount);

    for (var index = 0; index < supervisorCounterCount; index++)
    {
        var serviceCode = reader.ReadUInt32();
        var hits = reader.ReadInt64();
        supervisorCounters[serviceCode] = hits;
    }

    var instructionTlbEntries = ReadTlbEntries(reader);
    var dataTlbEntries = ReadTlbEntries(reader);

    return new PowerPc32CpuSnapshot(
        gpr,
        pc,
        lr,
        ctr,
        cr,
        xer,
        halted,
        msr,
        timeBase,
        lastMpc8xxControlSpr,
        extendedSpr,
        supervisorCounters,
        instructionTlbEntries,
        dataTlbEntries);
}

static void WriteTlbEntries(
    BinaryWriter writer,
    IReadOnlyList<PowerPc32TlbEntryState> entries)
{
    writer.Write(entries.Count);

    foreach (var entry in entries.OrderBy(item => item.Index))
    {
        writer.Write(entry.Index);
        writer.Write(entry.EffectivePageNumber);
        writer.Write(entry.RealPageNumber);
        writer.Write(entry.TableWalkControl);
    }
}

static IReadOnlyList<PowerPc32TlbEntryState> ReadTlbEntries(BinaryReader reader)
{
    var entryCount = reader.ReadInt32();

    if (entryCount < 0)
    {
        throw new InvalidOperationException("Checkpoint TLB entry count is negative.");
    }

    var entries = new List<PowerPc32TlbEntryState>(entryCount);

    for (var index = 0; index < entryCount; index++)
    {
        entries.Add(
            new PowerPc32TlbEntryState(
                reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32()));
    }

    return entries;
}

static void WriteMemorySnapshot(BinaryWriter writer, IReadOnlyList<SparseMemoryPageSnapshot> pages)
{
    writer.Write(CheckpointPageSize);
    writer.Write(pages.Count);

    foreach (var page in pages.OrderBy(entry => entry.PageIndex))
    {
        if (page.Data.Length != CheckpointPageSize)
        {
            throw new InvalidOperationException(
                $"Snapshot page {page.PageIndex} has invalid length {page.Data.Length}, expected {CheckpointPageSize}.");
        }

        writer.Write(page.PageIndex);
        writer.Write(page.Data);
    }
}

static IReadOnlyList<SparseMemoryPageSnapshot> ReadMemorySnapshot(BinaryReader reader)
{
    var pageSize = reader.ReadInt32();

    if (pageSize != CheckpointPageSize)
    {
        throw new InvalidOperationException(
            $"Unsupported checkpoint page size {pageSize}, expected {CheckpointPageSize}.");
    }

    var pageCount = reader.ReadInt32();

    if (pageCount < 0)
    {
        throw new InvalidOperationException("Checkpoint page count is negative.");
    }

    var pages = new List<SparseMemoryPageSnapshot>(pageCount);

    for (var index = 0; index < pageCount; index++)
    {
        var pageIndex = reader.ReadUInt32();
        var pageData = reader.ReadBytes(pageSize);

        if (pageData.Length != pageSize)
        {
            throw new InvalidOperationException(
                $"Checkpoint ended while reading page {pageIndex}.");
        }

        pages.Add(new SparseMemoryPageSnapshot(pageIndex, pageData));
    }

    return pages;
}

static bool TryMapVirtualAddressToFileOffset(
    IReadOnlyList<ImageSectionDescriptor> sections,
    uint virtualAddress,
    out int fileOffset)
{
    foreach (var section in sections)
    {
        if (section.FileSize == 0)
        {
            continue;
        }

        var start = section.VirtualAddress;
        var end = unchecked(section.VirtualAddress + section.FileSize);

        if (virtualAddress < start || virtualAddress >= end)
        {
            continue;
        }

        fileOffset = checked((int)(section.FileOffset + (virtualAddress - start)));
        return true;
    }

    fileOffset = 0;
    return false;
}

static string FormatBytes(ReadOnlySpan<byte> bytes)
{
    return string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
}

static bool TryReadWordAtVirtualAddress(
    IReadOnlyList<ImageSectionDescriptor> sections,
    byte[] imageBytes,
    uint virtualAddress,
    out uint value)
{
    if (!TryMapVirtualAddressToFileOffset(sections, virtualAddress, out var fileOffset) ||
        fileOffset < 0 ||
        fileOffset + 4 > imageBytes.Length)
    {
        value = 0;
        return false;
    }

    value = ((uint)imageBytes[fileOffset] << 24) |
            ((uint)imageBytes[fileOffset + 1] << 16) |
            ((uint)imageBytes[fileOffset + 2] << 8) |
            imageBytes[fileOffset + 3];

    return true;
}

static void PrintInstructionWindow(
    EmulationMachine machine,
    IReadOnlyList<ImageSectionDescriptor> sections,
    byte[] imageBytes,
    uint centerAddress,
    int before,
    int after,
    string label,
    PowerPc32CpuCore? powerPcCore = null)
{
    Console.WriteLine($"  {label} center=0x{centerAddress:X8}:");

    for (var offset = -before; offset <= after; offset++)
    {
        var address = unchecked((uint)((long)centerAddress + (offset * 4L)));
        var (instruction, hasImageWord, imageWord) =
            ReadInstructionWord(machine, sections, imageBytes, address, powerPcCore);
        var source = FormatInstructionSource(instruction, hasImageWord, imageWord);
        var marker = offset == 0 ? "=>" : "  ";
        var description = DescribeInstruction(address, instruction);

        Console.WriteLine($"  {marker} 0x{address:X8}: 0x{instruction:X8} [{source}] {description}");
    }
}

static (uint MemoryWord, bool HasImageWord, uint ImageWord) ReadInstructionWord(
    EmulationMachine machine,
    IReadOnlyList<ImageSectionDescriptor> sections,
    byte[] imageBytes,
    uint virtualAddress,
    PowerPc32CpuCore? powerPcCore = null)
{
    var memoryAddress = powerPcCore is null
        ? virtualAddress
        : powerPcCore.TranslateInstructionAddressForDebug(virtualAddress);
    var memoryWord = machine.ReadUInt32(memoryAddress);
    var hasImageWord = TryReadWordAtVirtualAddress(sections, imageBytes, virtualAddress, out var imageWord);

    return (memoryWord, hasImageWord, imageWord);
}

static string FormatInstructionSource(
    uint memoryWord,
    bool hasImageWord,
    uint imageWord)
{
    if (!hasImageWord)
    {
        return "mem-only";
    }

    return memoryWord == imageWord
        ? "mem=img"
        : $"mem!=img(0x{imageWord:X8})";
}

static string DescribeInstruction(uint programCounter, uint instructionWord)
{
    var opcode = instructionWord >> 26;

    return opcode switch
    {
        7 => DescribeMultiplyLowImmediate(instructionWord),
        8 => DescribeSubtractFromImmediateCarrying(instructionWord),
        10 => DescribeCompareLogicalImmediate(instructionWord),
        11 => DescribeCompareImmediate(instructionWord),
        12 => DescribeAddImmediateCarrying(instructionWord),
        13 => DescribeAddImmediateCarryingRecord(instructionWord),
        14 => DescribeAddImmediate(instructionWord),
        15 => DescribeAddImmediateShifted(instructionWord),
        16 => DescribeConditionalBranch(programCounter, instructionWord),
        18 => DescribeUnconditionalBranch(programCounter, instructionWord),
        19 => DescribeOpcode19(instructionWord),
        21 => DescribeRotateLeftWordImmediateAndMask(instructionWord),
        24 => DescribeOrImmediate(instructionWord),
        25 => DescribeOrImmediateShifted(instructionWord),
        26 => DescribeXorImmediate(instructionWord),
        27 => DescribeXorImmediateShifted(instructionWord),
        28 => DescribeAndImmediate(instructionWord),
        29 => DescribeAndImmediateShifted(instructionWord),
        31 => DescribeXForm(instructionWord),
        32 => DescribeLoadWord(instructionWord),
        33 => DescribeLoadWordUpdate(instructionWord),
        34 => DescribeLoadByte(instructionWord),
        35 => DescribeLoadByteUpdate(instructionWord),
        36 => DescribeStoreWord(instructionWord),
        37 => DescribeStoreWordUpdate(instructionWord),
        38 => DescribeStoreByte(instructionWord),
        39 => DescribeStoreByteUpdate(instructionWord),
        40 => DescribeLoadHalfWord(instructionWord),
        41 => DescribeLoadHalfWordUpdate(instructionWord),
        42 => DescribeLoadHalfWordAlgebraic(instructionWord),
        43 => DescribeLoadHalfWordAlgebraicUpdate(instructionWord),
        44 => DescribeStoreHalfWord(instructionWord),
        45 => DescribeStoreHalfWordUpdate(instructionWord),
        46 => DescribeLoadMultipleWord(instructionWord),
        47 => DescribeStoreMultipleWord(instructionWord),
        _ => $"op=0x{opcode:X2}",
    };
}

static string DescribeMultiplyLowImmediate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"mulli rt=r{rt} ra=r{ra} imm={immediate}";
}

static string DescribeSubtractFromImmediateCarrying(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"subfic rt=r{rt} ra=r{ra} imm={immediate}";
}

static string DescribeCompareLogicalImmediate(uint instructionWord)
{
    var crField = (int)((instructionWord >> 23) & 0x7);
    var l = (instructionWord & (1u << 21)) != 0;
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    var mnemonic = l ? "cmpldi" : "cmplwi";
    return $"{mnemonic} crf={crField} ra=r{ra} imm=0x{immediate:X4}";
}

static string DescribeCompareImmediate(uint instructionWord)
{
    var crField = (int)((instructionWord >> 23) & 0x7);
    var l = (instructionWord & (1u << 21)) != 0;
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    var mnemonic = l ? "cmpdi" : "cmpwi";
    return $"{mnemonic} crf={crField} ra=r{ra} imm={immediate}";
}

static string DescribeAddImmediateCarrying(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"addic rt=r{rt} ra=r{ra} imm={immediate}";
}

static string DescribeAddImmediateCarryingRecord(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"addic. rt=r{rt} ra=r{ra} imm={immediate}";
}

static string DescribeAddImmediate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"addi rt=r{rt} ra={DescribeBaseRegister(ra)} imm={immediate}";
}

static string DescribeAddImmediateShifted(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = ExtractSignedImmediate(instructionWord);
    return $"addis rt=r{rt} ra={DescribeBaseRegister(ra)} imm={immediate}";
}

static string DescribeOrImmediate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"ori ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeOrImmediateShifted(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"oris ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeXorImmediate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"xori ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeXorImmediateShifted(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"xoris ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeAndImmediate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"andi. ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeAndImmediateShifted(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var immediate = instructionWord & 0xFFFF;
    return $"andis. ra=r{ra} rs=r{rs} imm=0x{immediate:X4}";
}

static string DescribeRotateLeftWordImmediateAndMask(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var shift = (int)((instructionWord >> 11) & 0x1F);
    var mb = (int)((instructionWord >> 6) & 0x1F);
    var me = (int)((instructionWord >> 1) & 0x1F);
    var record = (instructionWord & 0x1) != 0 ? "." : string.Empty;
    return $"rlwinm{record} ra=r{ra} rs=r{rs} sh={shift} mb={mb} me={me}";
}

static string DescribeLoadWord(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lwz rt=r{rt} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeLoadWordUpdate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lwzu rt=r{rt} d={displacement}(r{ra})";
}

static string DescribeLoadByte(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lbz rt=r{rt} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeLoadByteUpdate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lbzu rt=r{rt} d={displacement}(r{ra})";
}

static string DescribeStoreWord(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"stw rs=r{rs} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeStoreWordUpdate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"stwu rs=r{rs} d={displacement}(r{ra})";
}

static string DescribeStoreByte(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"stb rs=r{rs} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeStoreByteUpdate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"stbu rs=r{rs} d={displacement}(r{ra})";
}

static string DescribeLoadHalfWord(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lhz rt=r{rt} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeLoadHalfWordUpdate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lhzu rt=r{rt} d={displacement}(r{ra})";
}

static string DescribeLoadHalfWordAlgebraic(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lha rt=r{rt} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeLoadHalfWordAlgebraicUpdate(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lhau rt=r{rt} d={displacement}(r{ra})";
}

static string DescribeStoreHalfWord(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"sth rs=r{rs} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeStoreHalfWordUpdate(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"sthu rs=r{rs} d={displacement}(r{ra})";
}

static string DescribeLoadMultipleWord(uint instructionWord)
{
    var rt = ExtractRt(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"lmw rt=r{rt} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeStoreMultipleWord(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var displacement = ExtractSignedImmediate(instructionWord);
    return $"stmw rs=r{rs} d={displacement}({DescribeBaseRegister(ra)})";
}

static string DescribeOpcode19(uint instructionWord)
{
    var xo = (int)((instructionWord >> 1) & 0x3FF);
    var bo = (instructionWord >> 21) & 0x1F;
    var bi = (instructionWord >> 16) & 0x1F;
    var link = (instructionWord & 0x1) != 0;

    return xo switch
    {
        16 => $"bclr bo={bo} bi={bi}{(link ? " lk" : string.Empty)}",
        50 => "rfi",
        150 => "isync",
        528 => $"bcctr bo={bo} bi={bi}{(link ? " lk" : string.Empty)}",
        _ => $"op=0x13 xo=0x{xo:X3}"
    };
}

static string DescribeXForm(uint instructionWord)
{
    var xo = (int)((instructionWord >> 1) & 0x3FF);
    var rt = ExtractRt(instructionWord);
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var rb = ExtractRb(instructionWord);
    var record = (instructionWord & 0x1) != 0 ? "." : string.Empty;
    var overflow = (instructionWord & 0x400) != 0 ? "o" : string.Empty;
    var crField = (int)((instructionWord >> 23) & 0x7);

    return xo switch
    {
        0 => $"cmpw crf={crField} ra=r{ra} rb=r{rb}",
        8 => $"subfc{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        10 => $"addc{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        11 => $"mulhwu{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        19 => $"mfcr rt=r{rt}",
        23 => $"lwzx rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        24 => $"slw ra=r{ra} rs=r{rs} rb=r{rb}{record}",
        28 => $"and{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        32 => $"cmplw crf={crField} ra=r{ra} rb=r{rb}",
        40 => $"subf{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        55 => $"lwzux rt=r{rt} ra=r{ra} rb=r{rb}",
        60 => $"andc{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        75 => $"mulhw{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        83 => $"mfmsr rt=r{rt}",
        87 => $"lbzx rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        104 => $"neg{overflow}{record} rt=r{rt} ra=r{ra}",
        119 => $"lbzux rt=r{rt} ra=r{ra} rb=r{rb}",
        124 => $"nor{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        136 => $"subfe{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        144 => $"mtcrf mask=0x{((instructionWord >> 12) & 0xFF):X2} rs=r{rs}",
        146 => $"mtmsr rs=r{rs}",
        151 => $"stwx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        183 => $"stwux rs=r{rs} ra=r{ra} rb=r{rb}",
        200 => $"subfze{overflow}{record} rt=r{rt} ra=r{ra}",
        202 => $"addze{overflow}{record} rt=r{rt} ra=r{ra}",
        215 => $"stbx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        235 => $"mullw{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        247 => $"stbux rs=r{rs} ra=r{ra} rb=r{rb}",
        266 => $"add{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        279 => $"lhzx rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        311 => $"lhzux rt=r{rt} ra=r{ra} rb=r{rb}",
        316 => $"xor{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        339 => $"mfspr rt=r{rt} spr={DescribeSpr(DecodeSpr(instructionWord))}",
        343 => $"lhax rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        371 => $"mftb rt=r{rt} spr={DescribeSpr(DecodeSpr(instructionWord))}",
        375 => $"lhaux rt=r{rt} ra=r{ra} rb=r{rb}",
        407 => $"sthx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        439 => $"sthux rs=r{rs} ra=r{ra} rb=r{rb}",
        444 => $"or{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        459 => $"divwu{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        467 => $"mtspr spr={DescribeSpr(DecodeSpr(instructionWord))} rs=r{rs}",
        476 => $"nand{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        491 => $"divw{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        824 =>
            $"srawi{record} ra=r{ra} rs=r{rs} sh={(instructionWord >> 11) & 0x1F}",
        _ => $"op=0x1F xo=0x{xo:X3}",
    };
}

static int ExtractRt(uint instructionWord)
{
    return (int)((instructionWord >> 21) & 0x1F);
}

static int ExtractRs(uint instructionWord)
{
    return (int)((instructionWord >> 21) & 0x1F);
}

static int ExtractRa(uint instructionWord)
{
    return (int)((instructionWord >> 16) & 0x1F);
}

static int ExtractRb(uint instructionWord)
{
    return (int)((instructionWord >> 11) & 0x1F);
}

static int ExtractSignedImmediate(uint instructionWord)
{
    return unchecked((short)(instructionWord & 0xFFFF));
}

static string DescribeBaseRegister(int register)
{
    return register == 0
        ? "0"
        : $"r{register}";
}

static int DecodeSpr(uint instructionWord)
{
    var sprHighBits = (int)((instructionWord >> 16) & 0x1F);
    var sprLowBits = (int)((instructionWord >> 11) & 0x1F);
    return (sprLowBits << 5) | sprHighBits;
}

static string DescribeSpr(int spr)
{
    var name = spr switch
    {
        8 => "LR",
        9 => "CTR",
        18 => "DSISR",
        19 => "DAR",
        22 => "DEC",
        26 => "SRR0",
        27 => "SRR1",
        268 => "TBL",
        269 => "TBU",
        784 => "MI_CTR",
        787 => "MI_EPN",
        789 => "MI_TWC",
        790 => "MI_RPN",
        792 => "MD_CTR",
        795 => "MD_EPN",
        796 => "M_TWB",
        797 => "MD_TWC",
        798 => "MD_RPN",
        _ => null
    };

    return name is null
        ? spr.ToString(CultureInfo.InvariantCulture)
        : $"{spr}({name})";
}

static string DescribeUnconditionalBranch(uint programCounter, uint instructionWord)
{
    var displacement = (int)(instructionWord & 0x03FF_FFFC);

    if ((displacement & 0x0200_0000) != 0)
    {
        displacement |= unchecked((int)0xFC00_0000);
    }

    var absolute = (instructionWord & 0x2) != 0;
    var link = (instructionWord & 0x1) != 0;
    var target = absolute
        ? unchecked((uint)displacement)
        : unchecked(programCounter + (uint)displacement);
    var selfLoop = target == programCounter ? " self-loop" : string.Empty;

    return $"b target=0x{target:X8} {(absolute ? "abs" : "rel")}{(link ? " lk" : string.Empty)}{selfLoop}";
}

static string DescribeConditionalBranch(uint programCounter, uint instructionWord)
{
    var bo = (instructionWord >> 21) & 0x1F;
    var bi = (instructionWord >> 16) & 0x1F;
    var displacement = (int)(instructionWord & 0x0000_FFFC);

    if ((displacement & 0x0000_8000) != 0)
    {
        displacement |= unchecked((int)0xFFFF_0000);
    }

    var absolute = (instructionWord & 0x2) != 0;
    var link = (instructionWord & 0x1) != 0;
    var target = absolute
        ? unchecked((uint)displacement)
        : unchecked(programCounter + (uint)displacement);

    return $"bc bo={bo} bi={bi} target=0x{target:X8} {(absolute ? "abs" : "rel")}{(link ? " lk" : string.Empty)}";
}

static void PrintSpecialPurposeRegisters(PowerPc32CpuCore powerPcCore)
{
    var nonZeroSpr = powerPcCore.ExtendedSpecialPurposeRegisters
        .Where(entry => entry.Value != 0)
        .OrderBy(entry => entry.Key)
        .ToArray();

    if (nonZeroSpr.Length == 0)
    {
        return;
    }

    const int maxEntries = 24;
    Console.WriteLine("SpecialPurposeRegisters:");

    foreach (var (spr, value) in nonZeroSpr.Take(maxEntries))
    {
        Console.WriteLine($"  SPR[{spr}]=0x{value:X8}");
    }

    if (nonZeroSpr.Length > maxEntries)
    {
        Console.WriteLine($"  ... {nonZeroSpr.Length - maxEntries} more non-zero SPR entries");
    }
}

static IReadOnlyDictionary<string, uint> ReadNamedGlobals(
    EmulationMachine machine,
    IReadOnlyList<NamedAddress> namedAddresses,
    PowerPc32CpuCore? powerPcCore,
    IReadOnlyList<NamedAddress> namedEffectiveAddresses)
{
    if (namedAddresses.Count == 0 &&
        namedEffectiveAddresses.Count == 0)
    {
        return new Dictionary<string, uint>(0, StringComparer.Ordinal);
    }

    var values = new Dictionary<string, uint>(namedAddresses.Count + namedEffectiveAddresses.Count, StringComparer.Ordinal);

    foreach (var namedAddress in namedAddresses)
    {
        values[namedAddress.Name] = machine.ReadUInt32(namedAddress.Address);
    }

    if (namedEffectiveAddresses.Count > 0)
    {
        if (powerPcCore is null)
        {
            throw new NotSupportedException(
                "Effective global addresses (--global32-ea) require PowerPC32 runtime state.");
        }

        foreach (var namedAddress in namedEffectiveAddresses)
        {
            var translatedAddress = powerPcCore.TranslateDataAddressForDebug(namedAddress.Address);
            values[namedAddress.Name] = machine.ReadUInt32(translatedAddress);
        }
    }

    return values;
}

static void PrintNamedGlobals(IReadOnlyDictionary<string, uint> globals)
{
    if (globals.Count == 0)
    {
        return;
    }

    Console.WriteLine("Globals32:");

    foreach (var global in globals.OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {global.Key}=0x{global.Value:X8}");
    }
}

static void PrintDynamicWatch(
    EmulationMachine machine,
    PowerPc32CpuCore? powerPcCore,
    IReadOnlyList<DynamicWatchWordRequest> dynamicWatchWordRequests)
{
    if (powerPcCore is null || dynamicWatchWordRequests.Count == 0)
    {
        return;
    }

    Console.WriteLine("DynamicWatch32:");

    foreach (var request in dynamicWatchWordRequests.Distinct().OrderBy(entry => entry.RegisterIndex).ThenBy(entry => entry.Offset))
    {
        var registerValue = powerPcCore.Registers[request.RegisterIndex];
        var resolvedAddress = unchecked(registerValue + (uint)request.Offset);
        var watchName = FormatDynamicWatchWordRequest(request);
        Console.WriteLine(
            $"  [{watchName}] BASE=0x{registerValue:X8} ADDR=0x{resolvedAddress:X8} VALUE=0x{machine.ReadUInt32(resolvedAddress):X8}");
    }
}

static void PrintEffectiveWatch(
    EmulationMachine machine,
    PowerPc32CpuCore? powerPcCore,
    IReadOnlyList<uint> effectiveWatchWordAddresses)
{
    if (powerPcCore is null || effectiveWatchWordAddresses.Count == 0)
    {
        return;
    }

    Console.WriteLine("EffectiveWatch32:");

    foreach (var effectiveAddress in effectiveWatchWordAddresses.Distinct().OrderBy(address => address))
    {
        var translatedAddress = powerPcCore.TranslateDataAddressForDebug(effectiveAddress);
        var value = machine.ReadUInt32(translatedAddress);
        Console.WriteLine(
            $"  EA=0x{effectiveAddress:X8} PA=0x{translatedAddress:X8} VALUE=0x{value:X8}");
    }
}

static uint DecodeMpc8xxPageSizeForDebug(uint tableWalkControl, uint realPageNumber)
{
    var pageSizeCode = tableWalkControl & 0x0000_000C;

    if (pageSizeCode == 0x0000_000C)
    {
        return 8u * 1024u * 1024u;
    }

    if (pageSizeCode == 0x0000_0004)
    {
        return 512u * 1024u;
    }

    if (pageSizeCode == 0x0000_0000 &&
        (realPageNumber & 0x0000_0008) != 0)
    {
        return 16u * 1024u;
    }

    return 4u * 1024u;
}

static bool TryTranslateViaMpc8xxTlbEntry(
    PowerPc32TlbEntryState entry,
    uint effectiveAddress,
    out uint translatedAddress)
{
    translatedAddress = 0;

    if ((entry.EffectivePageNumber & 0x0000_0200) == 0)
    {
        return false;
    }

    var pageSize = DecodeMpc8xxPageSizeForDebug(entry.TableWalkControl, entry.RealPageNumber);
    var pageOffsetMask = pageSize - 1;
    var pageBaseMask = ~pageOffsetMask;

    if ((effectiveAddress & pageBaseMask) != (entry.EffectivePageNumber & pageBaseMask))
    {
        return false;
    }

    var translatedPageBase = entry.RealPageNumber & pageBaseMask;

    if ((translatedPageBase & 0x8000_0000u) == 0 &&
        (entry.EffectivePageNumber & 0x8000_0000u) != 0)
    {
        translatedPageBase |= 0x8000_0000u;
    }

    translatedAddress = translatedPageBase | (effectiveAddress & pageOffsetMask);
    return true;
}

static bool TryTranslateViaMpc8xxTlbEntries(
    IReadOnlyList<PowerPc32TlbEntryState> entries,
    uint effectiveAddress,
    out uint translatedAddress)
{
    for (var index = 0; index < entries.Count; index++)
    {
        if (!TryTranslateViaMpc8xxTlbEntry(entries[index], effectiveAddress, out translatedAddress))
        {
            continue;
        }

        return true;
    }

    translatedAddress = 0;
    return false;
}

static void MergeHotSpots(
    IDictionary<uint, long> counters,
    IReadOnlyList<ExecutionTraceEntry> hotSpots)
{
    foreach (var hotSpot in hotSpots)
    {
        counters.TryGetValue(hotSpot.ProgramCounter, out var current);
        counters[hotSpot.ProgramCounter] = current + hotSpot.Hits;
    }
}

static void MergeTrackedProgramCounterHits(
    IDictionary<uint, long> destination,
    IReadOnlyDictionary<uint, long> source)
{
    if (destination.Count == 0 || source.Count == 0)
    {
        return;
    }

    foreach (var (programCounter, hits) in source)
    {
        if (!destination.ContainsKey(programCounter))
        {
            continue;
        }

        destination[programCounter] = destination[programCounter] + hits;
    }
}

static void MergeProgramCounterTail(
    IList<uint>? destination,
    IReadOnlyList<uint> source,
    int maxLength)
{
    if (destination is null || maxLength <= 0 || source.Count == 0)
    {
        return;
    }

    foreach (var pc in source)
    {
        if (destination.Count == maxLength)
        {
            destination.RemoveAt(0);
        }

        destination.Add(pc);
    }
}

static void MergeMemoryWatchEvents(
    IList<MemoryWatchTraceEntry>? destination,
    IReadOnlyList<MemoryWatchTraceEntry> source,
    int maxLength)
{
    if (destination is null || maxLength <= 0 || source.Count == 0)
    {
        return;
    }

    foreach (var watchEvent in source)
    {
        if (destination.Count >= maxLength)
        {
            break;
        }

        destination.Add(watchEvent);
    }
}

static IReadOnlyDictionary<uint, long> SnapshotSupervisorCallCounters(
    PowerPc32CpuCore? powerPcCore)
{
    if (powerPcCore is null || powerPcCore.SupervisorCallCounters.Count == 0)
    {
        return new Dictionary<uint, long>(0);
    }

    return powerPcCore.SupervisorCallCounters
        .ToDictionary(entry => entry.Key, entry => entry.Value);
}

static IReadOnlyDictionary<uint, long> ComputeCounterDelta(
    IReadOnlyDictionary<uint, long> baseline,
    IReadOnlyDictionary<uint, long> current)
{
    if (current.Count == 0)
    {
        return new Dictionary<uint, long>(0);
    }

    var delta = new Dictionary<uint, long>();

    foreach (var (serviceCode, currentHits) in current)
    {
        baseline.TryGetValue(serviceCode, out var baselineHits);
        var runHits = currentHits - baselineHits;

        if (runHits > 0)
        {
            delta[serviceCode] = runHits;
        }
    }

    return delta;
}

static void PrintSupervisorCallCounters(
    string label,
    IReadOnlyDictionary<uint, long> counters)
{
    if (counters.Count == 0)
    {
        return;
    }

    Console.WriteLine($"{label}:");

    foreach (var (serviceCode, hits) in counters
                 .OrderByDescending(entry => entry.Value)
                 .ThenBy(entry => entry.Key)
                 .Take(12))
    {
        Console.WriteLine($"  Service=0x{serviceCode:X8} Hits={hits}");
    }
}

static ProbeRunReport CreateProbeReport(
    string imagePath,
    int imageSizeBytes,
    ImageInspectionResult inspection,
    long instructionBudget,
    int memoryMb,
    long baseExecutedInstructions,
    long executedInstructions,
    long executedInstructionsFromBoot,
    double runWallClockSeconds,
    double runInstructionsPerSecond,
    RuntimeSessionState state,
    TracedRunResult traceRun,
    bool stopOnSupervisorServiceReached,
    IReadOnlyList<KeyValuePair<uint, long>> topHotSpots,
    IReadOnlyDictionary<uint, long> trackedProgramCounterHits,
    IReadOnlyDictionary<string, uint> namedGlobals,
    IReadOnlyList<string> profileNames,
    long nullProgramCounterRedirectCount,
    IReadOnlyDictionary<uint, long> supervisorCallCountersTotal,
    IReadOnlyDictionary<uint, long> supervisorCallCountersDelta,
    IReadOnlyList<PowerPcSupervisorCallTraceEntry> supervisorCallTrace,
    string consoleOutput)
{
    var globalValues = namedGlobals
        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(
            entry => entry.Key,
            entry => $"0x{entry.Value:X8}",
            StringComparer.Ordinal);
    var trackedHits = trackedProgramCounterHits
        .OrderBy(entry => entry.Key)
        .ToDictionary(
            entry => $"0x{entry.Key:X8}",
            entry => entry.Value,
            StringComparer.Ordinal);
    var hotSpots = topHotSpots
        .Select(entry => new ProbeHotSpotReport($"0x{entry.Key:X8}", entry.Value))
        .ToArray();
    var tail = traceRun.ProgramCounterTail
        .Select(programCounter => $"0x{programCounter:X8}")
        .ToArray();
    var watchEvents = traceRun.MemoryWatchEvents
        .Select(entry => new ProbeMemoryWatchEventReport(
            $"0x{entry.ProgramCounter:X8}",
            $"0x{entry.Address:X8}",
            $"0x{entry.PreviousValue:X8}",
            $"0x{entry.CurrentValue:X8}"))
        .ToArray();
    var supervisorTrace = supervisorCallTrace
        .Take(128)
        .Select(entry => new ProbeSupervisorCallTraceReport(
            $"0x{entry.ProgramCounter:X8}",
            $"0x{entry.ServiceCode:X8}",
            $"0x{entry.LinkRegister:X8}",
            entry.CallerProgramCounter != 0 ? $"0x{entry.CallerProgramCounter:X8}" : null,
            $"0x{entry.Argument0:X8}",
            $"0x{entry.Argument1:X8}",
            $"0x{entry.Argument2:X8}",
            $"0x{entry.Argument3:X8}",
            $"0x{entry.ReturnValue:X8}",
            entry.Halt,
            entry.NextProgramCounter.HasValue ? $"0x{entry.NextProgramCounter.Value:X8}" : null))
        .ToArray();
    var supervisorCallsTotal = supervisorCallCountersTotal
        .OrderBy(entry => entry.Key)
        .ToDictionary(
            entry => $"0x{entry.Key:X8}",
            entry => entry.Value,
            StringComparer.Ordinal);
    var supervisorCallsDelta = supervisorCallCountersDelta
        .OrderBy(entry => entry.Key)
        .ToDictionary(
            entry => $"0x{entry.Key:X8}",
            entry => entry.Value,
            StringComparer.Ordinal);

    return new ProbeRunReport(
        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        imagePath,
        imageSizeBytes,
        inspection.Format.ToString(),
        inspection.Architecture.ToString(),
        inspection.Endianness.ToString(),
        inspection.Summary,
        inspection.CiscoFamily,
        inspection.CiscoImageTag,
        $"0x{inspection.EntryPoint:X8}",
        inspection.Sections.Count,
        instructionBudget,
        memoryMb,
        baseExecutedInstructions,
        executedInstructions,
        executedInstructionsFromBoot,
        runWallClockSeconds,
        runInstructionsPerSecond,
        state.Machine.Halted,
        $"0x{state.Machine.ProgramCounter:X8}",
        traceRun.StopReason.ToString(),
        traceRun.StopReason == ExecutionStopReason.StopAtProgramCounter,
        traceRun.StopReason == ExecutionStopReason.StopAtProgramCounterHit,
        traceRun.StopReason == ExecutionStopReason.StopOnWatchWordChange,
        stopOnSupervisorServiceReached,
        profileNames.ToArray(),
        nullProgramCounterRedirectCount,
        globalValues,
        trackedHits,
        hotSpots,
        tail,
        watchEvents,
        supervisorCallsTotal,
        supervisorCallsDelta,
        supervisorTrace,
        consoleOutput);
}

static void SaveProbeReport(string reportJsonPath, ProbeRunReport report)
{
    var directory = Path.GetDirectoryName(reportJsonPath);

    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var json = JsonSerializer.Serialize(report, ProbeReportJson.SerializerOptions);
    File.WriteAllText(reportJsonPath, json);
}

file sealed record ProbeCliOptions(
    string? CheckpointFilePath,
    string? SaveCheckpointFilePath,
    string? ReportJsonPath,
    long? CheckpointAtInstructions,
    bool CheckpointForceRebuild,
    bool ResumeHalted,
    int ChunkBudget,
    int MaxHotSpots,
    long ProgressEveryInstructions,
    uint? StopAtProgramCounter,
    IReadOnlyDictionary<uint, long> StopAtProgramCounterHits,
    uint? StopOnSupervisorService,
    int TailLength,
    IReadOnlyDictionary<uint, uint> SupervisorReturnOverrides,
    IReadOnlyDictionary<SupervisorCallsiteKey, uint> SupervisorReturnCallerOverrides,
    IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> SupervisorReturnCallerHitOverrides,
    IReadOnlyDictionary<uint, uint> MemoryWriteOverrides,
    IReadOnlyList<InstructionWindowRequest> AdditionalInstructionWindows,
    IReadOnlyList<uint> WatchWordAddresses,
    IReadOnlyList<DynamicWatchWordRequest> DynamicWatchWordRequests,
    IReadOnlyList<uint> WatchWordEffectiveAddresses,
    IReadOnlyList<uint> StopOnWatchWordChangeAddresses,
    IReadOnlyList<DynamicWatchWordRequest> DynamicStopOnWatchWordChangeRequests,
    IReadOnlyList<uint> StopOnWatchWordChangeEffectiveAddresses,
    IReadOnlyList<uint> TrackedProgramCounters,
    IReadOnlyList<NamedAddress> NamedGlobalAddresses,
    IReadOnlyList<NamedAddress> NamedGlobalEffectiveAddresses,
    IReadOnlyList<string> ProfileNames,
    bool DisableNullProgramCounterRedirect,
    string[] RegisterOverrideTokens);

file sealed record TracedRunResult(
    long ExecutedInstructions,
    IReadOnlyList<uint> ProgramCounterTail,
    ExecutionStopReason StopReason,
    IReadOnlyList<MemoryWatchTraceEntry> MemoryWatchEvents);

file sealed record TraceProgress(
    long ExecutedInstructions,
    long RemainingInstructions,
    uint ProgramCounter,
    double InstructionsPerSecond,
    ExecutionStopReason LastChunkStopReason);

file sealed record ProbeRunReport(
    string GeneratedAtUtc,
    string ImagePath,
    int ImageSizeBytes,
    string Format,
    string Architecture,
    string Endianness,
    string Summary,
    string? CiscoFamily,
    string? CiscoImageTag,
    string EntryPoint,
    int SegmentCount,
    long InstructionBudget,
    int MemoryMb,
    long BaseExecutedInstructions,
    long ExecutedInstructions,
    long ExecutedInstructionsFromBoot,
    double RunWallClockSeconds,
    double RunInstructionsPerSecond,
    bool Halted,
    string FinalProgramCounter,
    string StopReason,
    bool StopAtProgramCounterReached,
    bool StopAtProgramCounterHitReached,
    bool StopOnWatch32ChangeReached,
    bool StopOnSupervisorServiceReached,
    IReadOnlyList<string> Profiles,
    long NullProgramCounterRedirectCount,
    IReadOnlyDictionary<string, string> Globals32,
    IReadOnlyDictionary<string, long> TrackedProgramCounterHits,
    IReadOnlyList<ProbeHotSpotReport> HotSpots,
    IReadOnlyList<string> ProgramCounterTail,
    IReadOnlyList<ProbeMemoryWatchEventReport> MemoryWatchEvents,
    IReadOnlyDictionary<string, long> SupervisorCallsTotal,
    IReadOnlyDictionary<string, long> SupervisorCallsDelta,
    IReadOnlyList<ProbeSupervisorCallTraceReport> SupervisorTrace,
    string ConsoleOutput);

file sealed record ProbeHotSpotReport(
    string ProgramCounter,
    long Hits);

file sealed record ProbeMemoryWatchEventReport(
    string ProgramCounter,
    string Address,
    string PreviousValue,
    string CurrentValue);

file sealed record ProbeSupervisorCallTraceReport(
    string ProgramCounter,
    string ServiceCode,
    string LinkRegister,
    string? CallerProgramCounter,
    string Argument0,
    string Argument1,
    string Argument2,
    string Argument3,
    string ReturnValue,
    bool Halt,
    string? NextProgramCounter);

file static class ProbeReportJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
}

file sealed record InstructionWindowRequest(
    uint Address,
    int Before,
    int After);

file sealed record NamedAddress(
    string Name,
    uint Address);

file sealed record DynamicWatchWordRequest(
    int RegisterIndex,
    int Offset);

file readonly record struct SupervisorCallsiteKey(
    uint ServiceCode,
    uint CallerProgramCounter);

file readonly record struct SupervisorCallsiteHitKey(
    uint ServiceCode,
    uint CallerProgramCounter,
    int Hit);

file sealed class StopOnSupervisorServicePowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly uint _serviceCode;

    public StopOnSupervisorServicePowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        uint serviceCode)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _serviceCode = serviceCode;
    }

    public bool StopReached { get; private set; }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        var result = _inner.Handle(context);

        if (context.ServiceCode != _serviceCode)
        {
            return result;
        }

        StopReached = true;
        return result with { Halt = true };
    }
}

file sealed class OverrideReturnPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly IReadOnlyDictionary<uint, uint> _overrides;
    private readonly IReadOnlyDictionary<SupervisorCallsiteKey, uint> _callsiteOverrides;
    private readonly IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> _callsiteHitOverrides;
    private readonly Dictionary<SupervisorCallsiteKey, int> _callsiteHitCounters = new();

    public OverrideReturnPowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        IReadOnlyDictionary<uint, uint> overrides,
        IReadOnlyDictionary<SupervisorCallsiteKey, uint> callsiteOverrides,
        IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> callsiteHitOverrides)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _callsiteOverrides = callsiteOverrides ?? throw new ArgumentNullException(nameof(callsiteOverrides));
        _callsiteHitOverrides = callsiteHitOverrides ?? throw new ArgumentNullException(nameof(callsiteHitOverrides));
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        var callsiteKey = new SupervisorCallsiteKey(context.ServiceCode, context.CallerProgramCounter);
        _callsiteHitCounters.TryGetValue(callsiteKey, out var currentCallsiteHits);
        var nextHit = currentCallsiteHits + 1;
        _callsiteHitCounters[callsiteKey] = nextHit;
        var callsiteHitKey = new SupervisorCallsiteHitKey(callsiteKey.ServiceCode, callsiteKey.CallerProgramCounter, nextHit);

        if (_callsiteHitOverrides.TryGetValue(callsiteHitKey, out var callsiteHitReturnValue))
        {
            return new PowerPcSupervisorCallResult(callsiteHitReturnValue);
        }

        if (_callsiteOverrides.TryGetValue(callsiteKey, out var callsiteReturnValue))
        {
            return new PowerPcSupervisorCallResult(callsiteReturnValue);
        }

        if (_overrides.TryGetValue(context.ServiceCode, out var returnValue))
        {
            return new PowerPcSupervisorCallResult(returnValue);
        }

        return _inner.Handle(context);
    }
}

file sealed record ProbeCheckpoint(
    long ExecutedInstructionsFromBoot,
    int MemoryMb,
    PowerPc32CpuSnapshot CpuSnapshot,
    IReadOnlyList<SparseMemoryPageSnapshot> MemoryPages);
