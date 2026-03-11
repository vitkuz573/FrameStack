using System.Globalization;
using System.IO.Compression;
using System.Text;
using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

const int DefaultChunkBudget = 200_000_000;
const int DefaultTailLength = 64;
const int DefaultMaxMemoryWatchEvents = 1024;
const long DefaultCheckpointAtInstructions = 2_200_000_000;
const uint CheckpointMagic = 0x4653_5043; // "FSPC"
const int CheckpointVersion = 1;
const int CheckpointPageSize = 4096;

if (args.Length == 0)
{
    Console.WriteLine(
        "Usage: dotnet run --project tools/FrameStack.ImageProbe -- <image-path> [instruction-budget] [memory-mb] [timeline-steps] " +
        "[register=value ...] [--checkpoint-file <path>] [--checkpoint-at <instructions>] [--checkpoint-force-rebuild] [--chunk-budget <instructions>] " +
        "[--svc-return <service>=<value>] [--poke32 <address>=<value>] [--stop-at-pc <address>] [--tail-length <count>] [--save-checkpoint <path>] " +
        "[--window <address>:<before>:<after>] [--watch32 <address>] [--count-pc <address>]");
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

if (cliOptions.MemoryWriteOverrides.Count > 0)
{
    Console.WriteLine(
        $"MemoryOverrides: {string.Join(", ", cliOptions.MemoryWriteOverrides.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X8}=0x{pair.Value:X8}"))}");
}

if (cliOptions.StopAtProgramCounter.HasValue)
{
    Console.WriteLine($"StopAtProgramCounter: 0x{cliOptions.StopAtProgramCounter.Value:X8}");
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

if (cliOptions.TrackedProgramCounters.Count > 0)
{
    Console.WriteLine(
        $"CountPc: {string.Join(", ", cliOptions.TrackedProgramCounters.Select(address => $"0x{address:X8}"))}");
}

if (cliOptions.CheckpointFilePath is not null)
{
    Console.WriteLine($"CheckpointFile: {cliOptions.CheckpointFilePath}");
}

if (cliOptions.SaveCheckpointFilePath is not null)
{
    Console.WriteLine($"SaveCheckpointFile: {cliOptions.SaveCheckpointFilePath}");
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
    var powerPcCore = state.CpuCore as PowerPc32CpuCore;

    if (powerPcCore is not null)
    {
        if (cliOptions.SupervisorReturnOverrides.Count > 0)
        {
            powerPcCore.SupervisorCallHandler = new OverrideReturnPowerPcSupervisorCallHandler(
                powerPcCore.SupervisorCallHandler,
                cliOptions.SupervisorReturnOverrides);
        }

        supervisorTracer = new PowerPcTracingSupervisorCallHandler(powerPcCore.SupervisorCallHandler);
        powerPcCore.SupervisorCallHandler = supervisorTracer;
    }

    var timelineExecuted = 0L;

    if (timelineSteps > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Timeline (first {timelineSteps} step(s)):");

        for (var index = 0; index < timelineSteps; index++)
        {
            var pc = state.Machine.ProgramCounter;
            var mapped = TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, pc, out var instructionWordFromImage);
            var instructionWord = mapped
                ? instructionWordFromImage
                : state.Machine.ReadUInt32(pc);
            var opcode = (instructionWord >> 26).ToString("X2", CultureInfo.InvariantCulture);
            var instructionDisplay = mapped
                ? $"0x{instructionWord:X8}"
                : $"0x{instructionWord:X8} (memory)";

            Console.WriteLine($"  #{index + 1:D4} PC=0x{pc:X8} INSN={instructionDisplay} OPCODE=0x{opcode}");

            if (powerPcCore is not null)
            {
                Console.WriteLine(
                    $"       R0=0x{powerPcCore.Registers[0]:X8} R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} " +
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
    var traceRun = RunBudgetWithTrace(
        state.Machine,
        remainingBudget,
        cliOptions.ChunkBudget,
        hotSpotCounters,
        cliOptions.StopAtProgramCounter,
        cliOptions.TailLength,
        cliOptions.WatchWordAddresses);

    var executedThisRun = timelineExecuted + traceRun.ExecutedInstructions;
    var executedFromBoot = baseExecutedInstructions + executedThisRun;

    Console.WriteLine();
    Console.WriteLine("Preflight Run:");
    Console.WriteLine($"ExecutedInstructions: {executedThisRun}");
    if (baseExecutedInstructions > 0)
    {
        Console.WriteLine($"ExecutedInstructionsFromBoot: {executedFromBoot}");
    }

    if (cliOptions.StopAtProgramCounter.HasValue)
    {
        Console.WriteLine($"StopAtProgramCounterReached: {traceRun.StopPointReached}");
    }

    Console.WriteLine($"Halted: {state.Machine.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{state.Machine.ProgramCounter:X8}");

    if (traceRun.ProgramCounterTail.Count > 0)
    {
        Console.WriteLine("ProgramCounterTail:");

        foreach (var pc in traceRun.ProgramCounterTail)
        {
            var mapped = TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, pc, out var instructionWordFromImage);
            var instructionWord = mapped
                ? instructionWordFromImage
                : state.Machine.ReadUInt32(pc);
            var source = mapped ? "img" : "mem";

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
            $"FinalRegisters: R0=0x{powerPcCore.Registers[0]:X8} R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
            $"R8=0x{powerPcCore.Registers[8]:X8} R9=0x{powerPcCore.Registers[9]:X8} R10=0x{powerPcCore.Registers[10]:X8} " +
            $"R27=0x{powerPcCore.Registers[27]:X8} R29=0x{powerPcCore.Registers[29]:X8} R30=0x{powerPcCore.Registers[30]:X8} " +
            $"R31=0x{powerPcCore.Registers[31]:X8} LR=0x{powerPcCore.Registers.Lr:X8} CTR=0x{powerPcCore.Registers.Ctr:X8} " +
            $"CR=0x{powerPcCore.Registers.Cr:X8} XER=0x{powerPcCore.Registers.Xer:X8} MSR=0x{powerPcCore.MachineStateRegister:X8}");
        PrintSpecialPurposeRegisters(powerPcCore);
    }

    PrintFirmwareGlobals(state.Machine);
    PrintDynamicWatch(state.Machine, powerPcCore);
    Console.WriteLine("HotSpots:");

    var topHotSpots = hotSpotCounters
        .OrderByDescending(entry => entry.Value)
        .ThenBy(entry => entry.Key)
        .Take(12)
        .ToArray();

    foreach (var hotSpot in topHotSpots)
    {
        var mapped = TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, hotSpot.Key, out var instructionWordFromImage);
        var instructionWord = mapped
            ? instructionWordFromImage
            : state.Machine.ReadUInt32(hotSpot.Key);
        var majorOpcode = instructionWord >> 26;
        var source = mapped ? "image" : "memory";

        Console.WriteLine(
            $"  PC=0x{hotSpot.Key:X8} Hits={hotSpot.Value} INSN=0x{instructionWord:X8} OPCODE=0x{majorOpcode:X2} SRC={source}");
    }

    if (cliOptions.TrackedProgramCounters.Count > 0)
    {
        Console.WriteLine("TrackedProgramCounterHits:");

        foreach (var trackedPc in cliOptions.TrackedProgramCounters.Distinct().OrderBy(address => address))
        {
            hotSpotCounters.TryGetValue(trackedPc, out var hits);
            var mapped = TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, trackedPc, out var instructionWordFromImage);
            var instructionWord = mapped
                ? instructionWordFromImage
                : state.Machine.ReadUInt32(trackedPc);
            var source = mapped ? "image" : "memory";

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
        label: "FinalProgramCounter");

    if (topHotSpots.Length > 0 && topHotSpots[0].Key != state.Machine.ProgramCounter)
    {
        PrintInstructionWindow(
            state.Machine,
            inspection.Sections,
            imageBytes,
            topHotSpots[0].Key,
            before: 5,
            after: 5,
            label: "TopHotSpot");
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
                label: $"Window@0x{window.Address:X8}");
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
            label: "LinkRegister");

        if (linkRegister >= 4)
        {
            PrintInstructionWindow(
                state.Machine,
                inspection.Sections,
                imageBytes,
                linkRegister - 4,
                before: 24,
                after: 12,
                label: "LinkReturnSite");
        }
    }

    if (powerPcCore is not null &&
        powerPcCore.SupervisorCallCounters.Count > 0)
    {
        Console.WriteLine("SupervisorCalls:");

        foreach (var (serviceCode, hits) in powerPcCore.SupervisorCallCounters
                     .OrderByDescending(entry => entry.Value)
                     .ThenBy(entry => entry.Key)
                     .Take(12))
        {
            Console.WriteLine($"  Service=0x{serviceCode:X8} Hits={hits}");
        }
    }

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

            Console.WriteLine(
                $"  PC=0x{entry.ProgramCounter:X8} SVC=0x{entry.ServiceCode:X8} " +
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
    long? checkpointAtInstructions = null;
    var checkpointForceRebuild = false;
    var chunkBudget = DefaultChunkBudget;
    uint? stopAtProgramCounter = null;
    var tailLength = DefaultTailLength;
    var supervisorReturnOverrides = new Dictionary<uint, uint>();
    var memoryWriteOverrides = new Dictionary<uint, uint>();
    var additionalInstructionWindows = new List<InstructionWindowRequest>();
    var watchWordAddresses = new List<uint>();
    var trackedProgramCounters = new List<uint>();
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
            case "--chunk-budget":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                chunkBudget = ParsePositiveIntOption(optionName, optionValue);
                break;
            case "--svc-return":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                var supervisorOverride = ParseSupervisorReturnOverride(optionValue);
                supervisorReturnOverrides[supervisorOverride.ServiceCode] = supervisorOverride.ReturnValue;
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
            case "--count-pc":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                trackedProgramCounters.Add(ParseUInt32Flexible(optionValue));
                break;
            default:
                throw new ArgumentException($"Unsupported option '{optionName}'.");
        }
    }

    return new ProbeCliOptions(
        checkpointFilePath,
        saveCheckpointFilePath,
        checkpointAtInstructions,
        checkpointForceRebuild,
        chunkBudget,
        stopAtProgramCounter,
        tailLength,
        supervisorReturnOverrides,
        memoryWriteOverrides,
        additionalInstructionWindows,
        watchWordAddresses,
        trackedProgramCounters,
        registerOverrideTokens.ToArray());
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
    uint? stopAtProgramCounter,
    int tailLength,
    IReadOnlyList<uint> watchWordAddresses)
{
    var executed = 0L;
    var remaining = Math.Max(0, budget);
    var stopPointReached = false;
    var tail = tailLength > 0
        ? new List<uint>(tailLength)
        : null;
    var memoryWatchEvents = watchWordAddresses.Count > 0
        ? new List<MemoryWatchTraceEntry>()
        : null;

    while (remaining > 0 && !machine.Halted)
    {
        var currentChunk = (int)Math.Min(remaining, chunkBudget);
        ExecutionTraceSummary traceSummary;

        try
        {
            traceSummary = machine.RunWithTrace(
                currentChunk,
                maxHotSpots: int.MaxValue,
                tailLength: tailLength,
                stopAtProgramCounter: stopAtProgramCounter,
                watchWordAddresses: watchWordAddresses,
                maxMemoryWatchEvents: DefaultMaxMemoryWatchEvents);
        }
        catch (Exception exception)
        {
            var failurePc = machine.ProgramCounter;
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
                var start = Math.Max(0, tail.Count - Math.Min(tail.Count, 16));

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

            if (watchWordAddresses.Count > 0)
            {
                errorDetails.AppendLine("WatchedWordValuesAtFailure:");
                var count = Math.Min(watchWordAddresses.Count, 32);

                for (var index = 0; index < count; index++)
                {
                    var address = watchWordAddresses[index];
                    errorDetails.AppendLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "  0x{0:X8}=0x{1:X8}",
                            address,
                            machine.ReadUInt32(address)));
                }
            }

            throw new InvalidOperationException(errorDetails.ToString(), exception);
        }

        var chunkExecuted = traceSummary.Summary.ExecutedInstructions;

        executed += chunkExecuted;
        remaining -= chunkExecuted;
        MergeHotSpots(hotSpotCounters, traceSummary.HotSpots);
        MergeProgramCounterTail(tail, traceSummary.ProgramCounterTail, tailLength);
        MergeMemoryWatchEvents(memoryWatchEvents, traceSummary.MemoryWatchEvents, DefaultMaxMemoryWatchEvents);

        if (stopAtProgramCounter.HasValue &&
            machine.ProgramCounter == stopAtProgramCounter.Value)
        {
            stopPointReached = true;
            break;
        }

        if (chunkExecuted == 0)
        {
            break;
        }
    }

    return new TracedRunResult(
        executed,
        tail?.ToArray() ?? Array.Empty<uint>(),
        stopPointReached,
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
        extendedSpr,
        supervisorCounters);
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
    string label)
{
    Console.WriteLine($"  {label} center=0x{centerAddress:X8}:");

    for (var offset = -before; offset <= after; offset++)
    {
        var address = unchecked((uint)((long)centerAddress + (offset * 4L)));
        var mapped = TryReadWordAtVirtualAddress(sections, imageBytes, address, out var instructionFromImage);
        var instruction = mapped
            ? instructionFromImage
            : machine.ReadUInt32(address);
        var source = mapped ? "img" : "mem";
        var marker = offset == 0 ? "=>" : "  ";
        var description = DescribeInstruction(address, instruction);

        Console.WriteLine($"  {marker} 0x{address:X8}: 0x{instruction:X8} [{source}] {description}");
    }
}

static string DescribeInstruction(uint programCounter, uint instructionWord)
{
    var opcode = instructionWord >> 26;

    return opcode switch
    {
        16 => DescribeConditionalBranch(programCounter, instructionWord),
        18 => DescribeUnconditionalBranch(programCounter, instructionWord),
        19 => $"op=0x13 xo=0x{((instructionWord >> 1) & 0x3FF):X3}",
        31 => $"op=0x1F xo=0x{((instructionWord >> 1) & 0x3FF):X3}",
        _ => $"op=0x{opcode:X2}",
    };
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

static void PrintFirmwareGlobals(EmulationMachine machine)
{
    // Cisco ROM monitor global workspace in this image.
    var globals = new (string Name, uint Address)[]
    {
        ("g_0x8000BCEC", 0x8000BCEC),
        ("g_0x8000BCF0", 0x8000BCF0),
        ("g_0x8000BCF4", 0x8000BCF4),
        ("g_0x8000BCF8", 0x8000BCF8),
        ("g_0x8000BCFC", 0x8000BCFC),
        ("g_0x8000BD00", 0x8000BD00),
        ("g_0x8000BD04", 0x8000BD04),
        ("g_0x8000BD4C", 0x8000BD4C),
        ("g_0x8000BD50", 0x8000BD50),
        ("g_0x8000BD54", 0x8000BD54),
        ("g_0x80090780", 0x80090780),
        ("g_0x80090784", 0x80090784),
        ("g_0x82F40774", 0x82F40774),
        ("g_0x82F40778", 0x82F40778),
        ("g_0x82F4077C", 0x82F4077C),
        ("g_0x82F40780", 0x82F40780),
        ("g_0x82F40784", 0x82F40784),
        ("g_0x82F40788", 0x82F40788),
        ("g_0x82F4078C", 0x82F4078C),
        ("g_0x82F40790", 0x82F40790),
        ("g_0x82F40794", 0x82F40794),
        ("g_0x82F40798", 0x82F40798),
        ("g_0x82F4079C", 0x82F4079C),
        ("g_0x82F407A0", 0x82F407A0),
    };

    Console.WriteLine("FirmwareGlobals:");

    foreach (var global in globals)
    {
        Console.WriteLine($"  {global.Name}=0x{machine.ReadUInt32(global.Address):X8}");
    }
}

static void PrintDynamicWatch(
    EmulationMachine machine,
    PowerPc32CpuCore? powerPcCore)
{
    if (powerPcCore is null)
    {
        return;
    }

    var r9 = powerPcCore.Registers[9];
    var r10 = powerPcCore.Registers[10];
    var probeAddress774 = unchecked(r9 + 0x774u);
    var probeAddress778 = unchecked(r9 + 0x778u);
    var probeAddress77C = unchecked(r9 + 0x77Cu);
    var probeAddress780 = unchecked(r9 + 0x780u);
    var probeAddress784 = unchecked(r9 + 0x784u);
    var probeAddress788 = unchecked(r9 + 0x788u);
    var probeAddress78C = unchecked(r9 + 0x78Cu);
    var probeAddress790 = unchecked(r9 + 0x790u);
    var probeAddress794 = unchecked(r9 + 0x794u);
    var probeAddress798 = unchecked(r9 + 0x798u);
    var probeAddress79C = unchecked(r9 + 0x79Cu);
    var probeAddress7A0 = unchecked(r9 + 0x7A0u);
    var ioAddress77C = unchecked(r10 + 0x77Cu);
    var ioAddress780 = unchecked(r10 + 0x780u);

    Console.WriteLine("DynamicWatch:");
    Console.WriteLine($"  r9=0x{r9:X8}");
    Console.WriteLine($"  r10=0x{r10:X8}");
    Console.WriteLine($"  [r9+0x774]=0x{machine.ReadUInt32(probeAddress774):X8} @0x{probeAddress774:X8}");
    Console.WriteLine($"  [r9+0x778]=0x{machine.ReadUInt32(probeAddress778):X8} @0x{probeAddress778:X8}");
    Console.WriteLine($"  [r9+0x77C]=0x{machine.ReadUInt32(probeAddress77C):X8} @0x{probeAddress77C:X8}");
    Console.WriteLine($"  [r9+0x780]=0x{machine.ReadUInt32(probeAddress780):X8} @0x{probeAddress780:X8}");
    Console.WriteLine($"  [r9+0x784]=0x{machine.ReadUInt32(probeAddress784):X8} @0x{probeAddress784:X8}");
    Console.WriteLine($"  [r9+0x788]=0x{machine.ReadUInt32(probeAddress788):X8} @0x{probeAddress788:X8}");
    Console.WriteLine($"  [r9+0x78C]=0x{machine.ReadUInt32(probeAddress78C):X8} @0x{probeAddress78C:X8}");
    Console.WriteLine($"  [r9+0x790]=0x{machine.ReadUInt32(probeAddress790):X8} @0x{probeAddress790:X8}");
    Console.WriteLine($"  [r9+0x794]=0x{machine.ReadUInt32(probeAddress794):X8} @0x{probeAddress794:X8}");
    Console.WriteLine($"  [r9+0x798]=0x{machine.ReadUInt32(probeAddress798):X8} @0x{probeAddress798:X8}");
    Console.WriteLine($"  [r9+0x79C]=0x{machine.ReadUInt32(probeAddress79C):X8} @0x{probeAddress79C:X8}");
    Console.WriteLine($"  [r9+0x7A0]=0x{machine.ReadUInt32(probeAddress7A0):X8} @0x{probeAddress7A0:X8}");
    Console.WriteLine($"  [r10+0x77C]=0x{machine.ReadUInt32(ioAddress77C):X8} @0x{ioAddress77C:X8}");
    Console.WriteLine($"  [r10+0x780]=0x{machine.ReadUInt32(ioAddress780):X8} @0x{ioAddress780:X8}");
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

file sealed record ProbeCliOptions(
    string? CheckpointFilePath,
    string? SaveCheckpointFilePath,
    long? CheckpointAtInstructions,
    bool CheckpointForceRebuild,
    int ChunkBudget,
    uint? StopAtProgramCounter,
    int TailLength,
    IReadOnlyDictionary<uint, uint> SupervisorReturnOverrides,
    IReadOnlyDictionary<uint, uint> MemoryWriteOverrides,
    IReadOnlyList<InstructionWindowRequest> AdditionalInstructionWindows,
    IReadOnlyList<uint> WatchWordAddresses,
    IReadOnlyList<uint> TrackedProgramCounters,
    string[] RegisterOverrideTokens);

file sealed record TracedRunResult(
    long ExecutedInstructions,
    IReadOnlyList<uint> ProgramCounterTail,
    bool StopPointReached,
    IReadOnlyList<MemoryWatchTraceEntry> MemoryWatchEvents);

file sealed record InstructionWindowRequest(
    uint Address,
    int Before,
    int After);

file sealed class OverrideReturnPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly IReadOnlyDictionary<uint, uint> _overrides;

    public OverrideReturnPowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        IReadOnlyDictionary<uint, uint> overrides)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
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
