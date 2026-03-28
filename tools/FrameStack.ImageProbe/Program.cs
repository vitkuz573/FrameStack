using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

const int ConsoleRepeatStopMaxChunkBudget = 20_000_000;
const int DefaultTailLength = 64;
const int DefaultMaxMemoryWatchEvents = 1024;
const long DefaultCheckpointAtInstructions = 2_200_000_000;
const uint CheckpointMagic = 0x4653_504E; // "FSPN"
const int CheckpointVersion = 1;
const int CheckpointPageSize = 4096;

if (!ProbeCommandHandler.TryParse(args, out var cliInvocation, out var cliExitCode))
{
    return cliExitCode;
}

var imagePath = Path.GetFullPath(cliInvocation!.ImagePath);
var instructionBudget = cliInvocation.InstructionBudget;
var memoryMb = cliInvocation.MemoryMb;
var timelineSteps = cliInvocation.TimelineSteps;
var cliOptions = cliInvocation.CliOptions;
var registerOverrides = cliInvocation.RegisterOverrides;

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

if (cliOptions.SupervisorReturnSignatureOverrides.Count > 0)
{
    Console.WriteLine(
        $"SupervisorSignatureOverrides: {string.Join(", ", cliOptions.SupervisorReturnSignatureOverrides.OrderBy(pair => pair.Key.ServiceCode).ThenBy(pair => pair.Key.CallerProgramCounter).ThenBy(pair => pair.Key.Argument0).ThenBy(pair => pair.Key.Argument1).ThenBy(pair => pair.Key.Argument2).ThenBy(pair => pair.Key.Argument3).Select(pair => $"0x{pair.Key.ServiceCode:X8}@0x{pair.Key.CallerProgramCounter:X8}/0x{pair.Key.Argument0:X8}/0x{pair.Key.Argument1:X8}/0x{pair.Key.Argument2:X8}/0x{pair.Key.Argument3:X8}=0x{pair.Value:X8}"))}");
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

if (cliOptions.SupervisorTraceMaxEvents != 4096)
{
    Console.WriteLine($"SupervisorTraceMaxEvents: {cliOptions.SupervisorTraceMaxEvents}");
}

if (cliOptions.SupervisorTraceIncludePutCharacter)
{
    Console.WriteLine("SupervisorTraceIncludePutCharacter: True");
}

if (cliOptions.StopOnSupervisorSignatures.Count > 0)
{
    Console.WriteLine(
        $"StopOnSupervisorSignatures: {string.Join(", ", cliOptions.StopOnSupervisorSignatures.OrderBy(key => key.ServiceCode).ThenBy(key => key.CallerProgramCounter).ThenBy(key => key.Argument0).ThenBy(key => key.Argument1).ThenBy(key => key.Argument2).ThenBy(key => key.Argument3).Select(key => $"0x{key.ServiceCode:X8}@0x{key.CallerProgramCounter:X8}/0x{key.Argument0:X8}/0x{key.Argument1:X8}/0x{key.Argument2:X8}/0x{key.Argument3:X8}"))}");
}

if (cliOptions.StopOnSupervisorSignatureHits.Count > 0)
{
    Console.WriteLine(
        $"StopOnSupervisorSignatureHits: {string.Join(", ", cliOptions.StopOnSupervisorSignatureHits.OrderBy(pair => pair.Key.ServiceCode).ThenBy(pair => pair.Key.CallerProgramCounter).ThenBy(pair => pair.Key.Argument0).ThenBy(pair => pair.Key.Argument1).ThenBy(pair => pair.Key.Argument2).ThenBy(pair => pair.Key.Argument3).Select(pair => $"0x{pair.Key.ServiceCode:X8}@0x{pair.Key.CallerProgramCounter:X8}/0x{pair.Key.Argument0:X8}/0x{pair.Key.Argument1:X8}/0x{pair.Key.Argument2:X8}/0x{pair.Key.Argument3:X8}#{pair.Value.ToString(CultureInfo.InvariantCulture)}"))}");
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

if (cliOptions.TraceWatch32Accesses)
{
    Console.WriteLine("TraceWatch32Accesses: True");
    if (cliOptions.TraceWatch32AllAddresses)
    {
        Console.WriteLine("TraceWatch32AllAddresses: True");
    }

    Console.WriteLine($"TraceWatch32AccessesMax: {cliOptions.TraceWatch32AccessesMaxEvents}");
}

if (cliOptions.TraceWatch32ProgramCounterRanges.Count > 0)
{
    Console.WriteLine(
        $"TraceWatch32PcRange: {string.Join(", ", cliOptions.TraceWatch32ProgramCounterRanges.Select(range => $"0x{range.Start:X8}:0x{range.End:X8}"))}");
}

if (cliOptions.TraceWatch32AddressRanges.Count > 0)
{
    Console.WriteLine(
        $"TraceWatch32EaRange: {string.Join(", ", cliOptions.TraceWatch32AddressRanges.Select(range => $"0x{range.Start:X8}:0x{range.End:X8}"))}");
}

if (cliOptions.TraceInstructionProgramCounterRanges.Count > 0)
{
    Console.WriteLine(
        $"TraceInstructionPcRange: {string.Join(", ", cliOptions.TraceInstructionProgramCounterRanges.Select(range => $"0x{range.Start:X8}:0x{range.End:X8}"))}");
    Console.WriteLine($"TraceInstructionMax: {cliOptions.TraceInstructionMaxEvents}");
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

if (cliOptions.CStringDumpRequests.Count > 0)
{
    Console.WriteLine(
        $"DumpCString: {string.Join(", ", cliOptions.CStringDumpRequests.Select(request => $"0x{request.Address:X8}:{request.MaxBytes}"))}");
}

if (cliOptions.FindAsciiPatterns.Count > 0)
{
    Console.WriteLine(
        $"FindAscii: {string.Join(", ", cliOptions.FindAsciiPatterns.Select(pattern => $"\"{pattern}\""))}");
    if (cliOptions.FindAsciiRanges.Count > 0)
    {
        Console.WriteLine(
            $"FindAsciiRange: {string.Join(", ", cliOptions.FindAsciiRanges.Select(range => $"0x{range.Start:X8}:0x{range.End:X8}"))}");
    }

    Console.WriteLine($"FindAsciiMax: {cliOptions.FindAsciiMaxResults}");
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

if (cliOptions.StopOnConsoleRepeatRules.Count > 0)
{
    Console.WriteLine(
        $"StopOnConsoleRepeat: {string.Join(", ", cliOptions.StopOnConsoleRepeatRules.Select(rule => $"'{rule.Text}'={rule.RequiredHits}"))}");
}

if (cliOptions.AutoConsoleScript)
{
    Console.WriteLine("AutoConsoleScript: True");
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

if (cliOptions.Disable8MbHighBitAlias)
{
    Console.WriteLine("Disable8MbHighBitAlias: True");
}

if (cliOptions.DisableDynarec)
{
    Console.WriteLine("DisableDynarec: True");
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
var uartConsoleOutput = new StringBuilder();

try
{
    var state = bootstrapper.Bootstrap(
        runtimeHandle: "probe",
        imageBytes,
        memoryMb,
        consoleTransmitSink: value =>
        {
            if (TryDecodeConsoleCharacter(value, out var character))
            {
                uartConsoleOutput.Append(character);
            }
        });

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

    if (EnsureCiscoPowerPcLowVectorEntryStub(state, inspection))
    {
        Console.WriteLine("CiscoLowVectorEntryStub: installed.");
    }

    ApplyMemoryOverrides(state.Machine.MemoryBus, cliOptions.MemoryWriteOverrides);
    ApplyRegisterOverrides(state.CpuCore, registerOverrides);

    PowerPcTracingSupervisorCallHandler? supervisorTracer = null;
    StopOnSupervisorServicePowerPcSupervisorCallHandler? stopOnSupervisorServiceHandler = null;
    StopOnSupervisorSignaturePowerPcSupervisorCallHandler? stopOnSupervisorSignatureHandler = null;
    StopOnSupervisorSignatureHitPowerPcSupervisorCallHandler? stopOnSupervisorSignatureHitHandler = null;
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

        if (cliOptions.Disable8MbHighBitAlias)
        {
            powerPcCore.PreserveHighBitOn8MbTranslation = false;
        }

        if (cliOptions.DisableDynarec)
        {
            powerPcCore.DynarecEnabled = false;
        }

        if (cliOptions.SupervisorReturnOverrides.Count > 0 ||
            cliOptions.SupervisorReturnCallerOverrides.Count > 0 ||
            cliOptions.SupervisorReturnSignatureOverrides.Count > 0 ||
            cliOptions.SupervisorReturnCallerHitOverrides.Count > 0)
        {
            powerPcCore.SupervisorCallHandler = new OverrideReturnPowerPcSupervisorCallHandler(
                powerPcCore.SupervisorCallHandler,
                cliOptions.SupervisorReturnOverrides,
                cliOptions.SupervisorReturnCallerOverrides,
                cliOptions.SupervisorReturnSignatureOverrides,
                cliOptions.SupervisorReturnCallerHitOverrides);
        }

        supervisorTracer = new PowerPcTracingSupervisorCallHandler(
            powerPcCore.SupervisorCallHandler,
            cliOptions.SupervisorTraceMaxEvents,
            includePutCharacterInTrace: cliOptions.SupervisorTraceIncludePutCharacter);
        IPowerPcSupervisorCallHandler activeSupervisorHandler = supervisorTracer;

        if (cliOptions.StopOnSupervisorService.HasValue)
        {
            stopOnSupervisorServiceHandler = new StopOnSupervisorServicePowerPcSupervisorCallHandler(
                activeSupervisorHandler,
                cliOptions.StopOnSupervisorService.Value);
            activeSupervisorHandler = stopOnSupervisorServiceHandler;
        }

        if (cliOptions.StopOnSupervisorSignatures.Count > 0)
        {
            stopOnSupervisorSignatureHandler = new StopOnSupervisorSignaturePowerPcSupervisorCallHandler(
                activeSupervisorHandler,
                cliOptions.StopOnSupervisorSignatures);
            activeSupervisorHandler = stopOnSupervisorSignatureHandler;
        }

        if (cliOptions.StopOnSupervisorSignatureHits.Count > 0)
        {
            stopOnSupervisorSignatureHitHandler = new StopOnSupervisorSignatureHitPowerPcSupervisorCallHandler(
                activeSupervisorHandler,
                cliOptions.StopOnSupervisorSignatureHits);
            activeSupervisorHandler = stopOnSupervisorSignatureHitHandler;
        }

        powerPcCore.SupervisorCallHandler = activeSupervisorHandler;
    }

    var supervisorCallCountersBaseline = SnapshotSupervisorCallCounters(powerPcCore);
    var nullProgramCounterRedirectCountBaseline = powerPcCore?.NullProgramCounterRedirectCount ?? 0;
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
    var traceChunkBudget = cliOptions.ChunkBudget;

    if (cliOptions.StopOnConsoleRepeatRules.Count > 0 &&
        traceChunkBudget > ConsoleRepeatStopMaxChunkBudget)
    {
        traceChunkBudget = ConsoleRepeatStopMaxChunkBudget;
        Console.WriteLine($"ChunkBudgetAdjustedForConsoleRepeat: {traceChunkBudget}");
    }

    var hotSpotCounters = new Dictionary<uint, long>();
    var trackedProgramCounterHits = cliOptions.TrackedProgramCounters
        .Distinct()
        .ToDictionary(address => address, _ => 0L);
    var runStopwatch = Stopwatch.StartNew();
    var ciscoAutoPromptReturnResponses = 0L;
    var ciscoAutoPromptConfigResponses = 0L;
    var ciscoAutoIdleResponses = 0L;
    var ciscoAutoLastConsoleLength = 0;
    var ciscoAutoLastProgramCounter = state.Machine.ProgramCounter;
    var ciscoAutoIdleConsoleChunks = 0;
    var ciscoAutoStagnantPcChunks = 0;
    var enableAutoConsoleIdleAssist = cliOptions.AutoConsoleScript &&
                                      cliOptions.TraceInstructionProgramCounterRanges.Count == 0;
    var traceRun = RunBudgetWithTrace(
        state.Machine,
        remainingBudget,
        traceChunkBudget,
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
        cliOptions.TraceWatch32Accesses,
        cliOptions.TraceWatch32AllAddresses,
        cliOptions.TraceWatch32AccessesMaxEvents,
        cliOptions.TraceWatch32ProgramCounterRanges,
        cliOptions.TraceWatch32AddressRanges,
        cliOptions.TraceInstructionProgramCounterRanges,
        cliOptions.TraceInstructionMaxEvents,
        powerPcCore,
        cliOptions.ProgressEveryInstructions,
        () =>
        {
            if (!cliOptions.AutoConsoleScript &&
                cliOptions.StopOnConsoleRepeatRules.Count == 0)
            {
                return null;
            }

            var consoleOutput = BuildCombinedConsoleOutput(
                supervisorTracer?.ConsoleOutput ?? string.Empty,
                uartConsoleOutput.ToString());

            if (cliOptions.AutoConsoleScript)
            {
                ApplyCiscoAutoConsoleScript(
                    state,
                    consoleOutput,
                    enableAutoConsoleIdleAssist,
                    ref ciscoAutoPromptReturnResponses,
                    ref ciscoAutoPromptConfigResponses,
                    ref ciscoAutoIdleResponses,
                    ref ciscoAutoLastConsoleLength,
                    ref ciscoAutoLastProgramCounter,
                    ref ciscoAutoIdleConsoleChunks,
                    ref ciscoAutoStagnantPcChunks);
            }

            if (cliOptions.StopOnConsoleRepeatRules.Count == 0)
            {
                return null;
            }

            foreach (var rule in cliOptions.StopOnConsoleRepeatRules)
            {
                if (CountSubstringOccurrences(consoleOutput, rule.Text) >= rule.RequiredHits)
                {
                    return ExecutionStopReason.StopOnConsoleRepeat;
                }
            }

            return null;
        },
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
    var nullProgramCounterRedirectCountTotal = powerPcCore?.NullProgramCounterRedirectCount ?? 0;
    var nullProgramCounterRedirectCount = Math.Max(
        0,
        nullProgramCounterRedirectCountTotal - nullProgramCounterRedirectCountBaseline);
    var nullProgramCounterRedirectEvents = powerPcCore?.NullProgramCounterRedirectEvents.ToArray()
                                             ?? Array.Empty<PowerPcNullProgramCounterRedirectEvent>();
    var nullProgramCounterRedirectSourceCounts = nullProgramCounterRedirectEvents
        .GroupBy(entry => entry.Source)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key)
        .ToDictionary(
            group => group.Key,
            group => (long)group.LongCount());
    var supervisorCallCountersTotal = SnapshotSupervisorCallCounters(powerPcCore);
    var supervisorCallCountersDelta = ComputeCounterDelta(
        supervisorCallCountersBaseline,
        supervisorCallCountersTotal);
    var combinedConsoleOutput = BuildCombinedConsoleOutput(
        supervisorTracer?.ConsoleOutput ?? string.Empty,
        uartConsoleOutput.ToString());

    if (cliOptions.AutoConsoleScript)
    {
        Console.WriteLine(
            $"AutoConsoleResponses: press-return={ciscoAutoPromptReturnResponses} config-no={ciscoAutoPromptConfigResponses} idle-cr={ciscoAutoIdleResponses}");
    }

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
    var stopOnSupervisorSignatureReached = stopOnSupervisorSignatureHandler?.StopReached ?? false;
    var stopOnSupervisorSignatureHitReached = stopOnSupervisorSignatureHitHandler?.StopReached ?? false;
    var stopOnSupervisorSignatureHitMatched = stopOnSupervisorSignatureHitHandler?.MatchedSignature;
    var stopOnSupervisorSignatureHitMatchedCount = stopOnSupervisorSignatureHitHandler?.MatchedHitCount;

    if (cliOptions.StopOnSupervisorService.HasValue)
    {
        Console.WriteLine($"StopOnSupervisorServiceReached: {stopOnSupervisorServiceReached}");
    }

    if (cliOptions.StopOnSupervisorSignatures.Count > 0)
    {
        Console.WriteLine($"StopOnSupervisorSignatureReached: {stopOnSupervisorSignatureReached}");
    }

    if (cliOptions.StopOnSupervisorSignatureHits.Count > 0)
    {
        Console.WriteLine($"StopOnSupervisorSignatureHitReached: {stopOnSupervisorSignatureHitReached}");

        if (stopOnSupervisorSignatureHitReached &&
            stopOnSupervisorSignatureHitMatched.HasValue &&
            stopOnSupervisorSignatureHitMatchedCount.HasValue)
        {
            var key = stopOnSupervisorSignatureHitMatched.Value;
            Console.WriteLine(
                $"StopOnSupervisorSignatureHitMatched: 0x{key.ServiceCode:X8}@0x{key.CallerProgramCounter:X8}/0x{key.Argument0:X8}/0x{key.Argument1:X8}/0x{key.Argument2:X8}/0x{key.Argument3:X8}#{stopOnSupervisorSignatureHitMatchedCount.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    if (cliOptions.StopOnWatchWordChangeAddresses.Count > 0 ||
        cliOptions.DynamicStopOnWatchWordChangeRequests.Count > 0)
    {
        Console.WriteLine(
            $"StopOnWatch32ChangeReached: {traceRun.StopReason == ExecutionStopReason.StopOnWatchWordChange}");
    }

    if (cliOptions.StopOnConsoleRepeatRules.Count > 0)
    {
        Console.WriteLine(
            $"StopOnConsoleRepeatReached: {traceRun.StopReason == ExecutionStopReason.StopOnConsoleRepeat}");
    }

    Console.WriteLine($"RunWallClockSeconds: {runStopwatch.Elapsed.TotalSeconds:F3}");
    Console.WriteLine($"RunInstructionsPerSecond: {runInstructionsPerSecond:F2}");

    Console.WriteLine($"Halted: {state.Machine.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{state.Machine.ProgramCounter:X8}");
    Console.WriteLine($"NullProgramCounterRedirects: {nullProgramCounterRedirectCount}");

    if (nullProgramCounterRedirectCountBaseline > 0)
    {
        Console.WriteLine($"NullProgramCounterRedirectsTotal: {nullProgramCounterRedirectCountTotal}");
    }

    if (nullProgramCounterRedirectSourceCounts.Count > 0)
    {
        Console.WriteLine("NullProgramCounterRedirectSources:");

        foreach (var (source, hits) in nullProgramCounterRedirectSourceCounts)
        {
            Console.WriteLine($"  Source={source} Hits={hits}");
        }
    }

    if (nullProgramCounterRedirectEvents.Length > 0)
    {
        Console.WriteLine("NullProgramCounterRedirectTrace:");

        foreach (var redirectEvent in nullProgramCounterRedirectEvents.TakeLast(12))
        {
            var stackAddress = redirectEvent.StackAddress.HasValue
                ? $"0x{redirectEvent.StackAddress.Value:X8}"
                : "-";
            Console.WriteLine(
                $"  SRC={redirectEvent.Source} TARGET=0x{redirectEvent.RedirectTarget:X8} CAND=0x{redirectEvent.CandidateValue:X8} " +
                $"STACK={stackAddress} SP=0x{redirectEvent.StackPointer:X8} LR=0x{redirectEvent.LinkRegister:X8} " +
                $"R30=0x{redirectEvent.Register30:X8} R31=0x{redirectEvent.Register31:X8} " +
                $"S-18=0x{redirectEvent.StackWordMinus24:X8} S-14=0x{redirectEvent.StackWordMinus20:X8} " +
                $"S-10=0x{redirectEvent.StackWordMinus16:X8} S+0=0x{redirectEvent.StackWordAtPointer:X8} " +
                $"S+4=0x{redirectEvent.StackWordPlus4:X8} S+8=0x{redirectEvent.StackWordPlus8:X8}");
        }
    }

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

if (traceRun.MemoryAccessEvents.Count > 0)
{
    Console.WriteLine("MemoryAccessEvents:");

        foreach (var accessEvent in traceRun.MemoryAccessEvents)
        {
            Console.WriteLine(
                $"  PC=0x{accessEvent.ProgramCounter:X8} TYPE={accessEvent.AccessType} SIZE={accessEvent.SizeBytes} " +
                $"EA=0x{accessEvent.EffectiveAddress:X8} PA=0x{accessEvent.PhysicalAddress:X8} VALUE=0x{accessEvent.Value:X8}");
    }
}

if (traceRun.InstructionTraceEvents.Count > 0)
{
    Console.WriteLine("InstructionTraceEvents:");

    foreach (var traceEvent in traceRun.InstructionTraceEvents)
    {
        var deltas = traceEvent.RegisterDeltas.Count == 0
            ? "<no-register-delta>"
            : string.Join(
                ", ",
                traceEvent.RegisterDeltas.Select(delta =>
                    $"{delta.RegisterName}:0x{delta.BeforeValue:X8}->0x{delta.AfterValue:X8}"));
        Console.WriteLine(
            $"  PC=0x{traceEvent.ProgramCounterBefore:X8} NEXT=0x{traceEvent.ProgramCounterAfter:X8} " +
            $"INSN=0x{traceEvent.InstructionWord:X8} {traceEvent.Description} DELTA={deltas}");
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
        PrintPowerPcTlbEntries(powerPcCore);
        PrintPowerPcStopSnapshot(state.Machine, powerPcCore);
    }

    var namedGlobals = ReadNamedGlobals(
        state.Machine,
        cliOptions.NamedGlobalAddresses,
        powerPcCore,
        cliOptions.NamedGlobalEffectiveAddresses);
    var cStringDumps = ReadCStringDumps(
        state.Machine,
        cliOptions.CStringDumpRequests);
    var findAsciiRanges = ResolveFindAsciiRanges(cliOptions, memoryMb);
    var asciiMatches = FindAsciiMatches(
        state.Machine,
        cliOptions.FindAsciiPatterns,
        findAsciiRanges,
        cliOptions.FindAsciiMaxResults);
    PrintNamedGlobals(namedGlobals);
    PrintCStringDumps(cStringDumps);
    PrintAsciiMatches(asciiMatches);
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
        Console.WriteLine($"SupervisorTraceCaptured: {supervisorTracer.CallTrace.Count}");

        var supervisorCallsites = BuildSupervisorCallsiteCounts(supervisorTracer.CallTrace);

        if (supervisorCallsites.Count > 0)
        {
            Console.WriteLine("SupervisorCallsites:");

            foreach (var callsite in supervisorCallsites.Take(16))
            {
                Console.WriteLine(
                    $"  Service={callsite.ServiceCode} Caller={callsite.CallerProgramCounter} Hits={callsite.Hits}");
            }
        }

        var supervisorSignatures = BuildSupervisorSignatureCounts(supervisorTracer.CallTrace);

        if (supervisorSignatures.Count > 0)
        {
            Console.WriteLine("SupervisorSignatures:");

            foreach (var signature in supervisorSignatures.Take(20))
            {
                Console.WriteLine(
                    $"  Service={signature.ServiceCode} Caller={signature.CallerProgramCounter} " +
                    $"A0={signature.Argument0} A1={signature.Argument1} A2={signature.Argument2} A3={signature.Argument3} " +
                    $"RET={signature.ReturnValue} Hits={signature.Hits}");
            }
        }

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
                $"RET=0x{entry.ReturnValue:X8} HALT={entry.Halt} NEXT={nextPc} " +
                $"SP=0x{entry.StackPointer:X8} R30=0x{entry.Register30:X8} R31=0x{entry.Register31:X8} " +
                $"S-10=0x{entry.StackWordMinus16:X8} S+0=0x{entry.StackWordAtPointer:X8} " +
                $"S+4=0x{entry.StackWordPlus4:X8} S+8=0x{entry.StackWordPlus8:X8}");
        }

        if (supervisorTracer.CallTrace.Count > 40)
        {
            Console.WriteLine($"  ... trimmed ({supervisorTracer.CallTrace.Count - 40} more event(s))");
        }
    }

    if (!string.IsNullOrEmpty(combinedConsoleOutput))
    {
        Console.WriteLine("ConsoleOutput:");
        Console.WriteLine(combinedConsoleOutput);
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
            stopOnSupervisorSignatureReached,
            stopOnSupervisorSignatureHitReached,
            stopOnSupervisorSignatureHitMatched,
            stopOnSupervisorSignatureHitMatchedCount,
            traceRun.StopReason == ExecutionStopReason.StopOnConsoleRepeat,
            topHotSpots,
            trackedProgramCounterHits,
            namedGlobals,
            cStringDumps,
            asciiMatches,
            cliOptions.ProfileNames,
            nullProgramCounterRedirectCount,
            nullProgramCounterRedirectEvents,
            supervisorCallCountersTotal,
            supervisorCallCountersDelta,
            supervisorTracer?.CallTrace ?? Array.Empty<PowerPcSupervisorCallTraceEntry>(),
            combinedConsoleOutput);
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

    var inner = exception.InnerException;

    while (inner is not null)
    {
        Console.WriteLine("InnerException:");
        Console.WriteLine(inner.Message);
        inner = inner.InnerException;
    }

    return 3;
}

static void AddDistinct<T>(ICollection<T> destination, T value)
{
    if (destination.Contains(value))
    {
        return;
    }

    destination.Add(value);
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

static bool EnsureCiscoPowerPcLowVectorEntryStub(
    RuntimeSessionState state,
    ImageInspectionResult inspection)
{
    if (state.CpuCore is not PowerPc32CpuCore powerPcCore)
    {
        return false;
    }

    var installed = CiscoPowerPcLowVectorBootstrap.TryInstallEntryStub(
        state.Machine.MemoryBus,
        inspection,
        state.BootstrapReport.EntryPoint);

    if (installed)
    {
        powerPcCore.NullProgramCounterRedirectEnabled = false;
    }

    return installed;
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

static long CountSubstringOccurrences(string source, string needle)
{
    if (string.IsNullOrEmpty(source) ||
        string.IsNullOrEmpty(needle))
    {
        return 0;
    }

    var hits = 0L;
    var searchIndex = 0;

    while (searchIndex < source.Length)
    {
        var foundIndex = source.IndexOf(needle, searchIndex, StringComparison.Ordinal);

        if (foundIndex < 0)
        {
            break;
        }

        hits++;
        searchIndex = foundIndex + needle.Length;
    }

    return hits;
}

static void ApplyCiscoAutoConsoleScript(
    RuntimeSessionState state,
    string consoleOutput,
    bool enableIdleAssist,
    ref long pressReturnResponses,
    ref long configDialogResponses,
    ref long idleResponses,
    ref int lastConsoleLength,
    ref uint lastProgramCounter,
    ref int idleConsoleChunks,
    ref int stagnantProgramCounterChunks)
{
    if (state.CiscoC2600ConsoleUartDevice is null)
    {
        return;
    }

    const string PressReturnPrompt = "Press RETURN to get started!";
    const string ConfigDialogPrompt = "initial configuration dialog? [yes/no]:";
    var pressReturnHits = CountSubstringOccurrences(consoleOutput, PressReturnPrompt);

    while (pressReturnResponses < pressReturnHits)
    {
        state.CiscoC2600ConsoleUartDevice.EnqueueReceiveByte((byte)'\r');
        pressReturnResponses++;
    }

    var configDialogHits = CountSubstringOccurrences(consoleOutput, ConfigDialogPrompt);

    while (configDialogResponses < configDialogHits)
    {
        state.CiscoC2600ConsoleUartDevice.EnqueueReceiveByte((byte)'n');
        state.CiscoC2600ConsoleUartDevice.EnqueueReceiveByte((byte)'o');
        state.CiscoC2600ConsoleUartDevice.EnqueueReceiveByte((byte)'\r');
        configDialogResponses++;
    }

    if (!enableIdleAssist)
    {
        return;
    }

    if (consoleOutput.Length == lastConsoleLength)
    {
        idleConsoleChunks++;
    }
    else
    {
        idleConsoleChunks = 0;
    }

    var currentProgramCounter = state.Machine.ProgramCounter;

    if (currentProgramCounter == lastProgramCounter)
    {
        stagnantProgramCounterChunks++;
    }
    else
    {
        stagnantProgramCounterChunks = 0;
    }

    lastConsoleLength = consoleOutput.Length;
    lastProgramCounter = currentProgramCounter;

    if (idleConsoleChunks < 4 ||
        stagnantProgramCounterChunks < 4)
    {
        return;
    }

    state.CiscoC2600ConsoleUartDevice.EnqueueReceiveByte((byte)'\r');
    idleResponses++;
    idleConsoleChunks = 0;
    stagnantProgramCounterChunks = 0;
}

static string BuildCombinedConsoleOutput(string monitorOutput, string uartOutput)
{
    var hasMonitorOutput = !string.IsNullOrEmpty(monitorOutput);
    var hasUartOutput = !string.IsNullOrEmpty(uartOutput);

    if (!hasMonitorOutput &&
        !hasUartOutput)
    {
        return string.Empty;
    }

    if (!hasMonitorOutput)
    {
        return uartOutput;
    }

    if (!hasUartOutput)
    {
        return monitorOutput;
    }

    return string.Concat(monitorOutput, uartOutput);
}

static bool TryDecodeConsoleCharacter(byte value, out char character)
{
    switch (value)
    {
        case (byte)'\r':
        case (byte)'\n':
        case (byte)'\t':
            character = (char)value;
            return true;
    }

    if (value is >= 0x20 and <= 0x7E)
    {
        character = (char)value;
        return true;
    }

    character = default;
    return false;
}

static bool IsProgramCounterInRanges(uint programCounter, IReadOnlyList<AddressRange> ranges)
{
    if (ranges.Count == 0)
    {
        return true;
    }

    foreach (var range in ranges)
    {
        if (programCounter >= range.Start &&
            programCounter <= range.End)
        {
            return true;
        }
    }

    return false;
}

static IReadOnlyList<AddressRange> BuildWordWatchRanges(IReadOnlyList<uint> addresses)
{
    if (addresses.Count == 0)
    {
        return Array.Empty<AddressRange>();
    }

    var ranges = new AddressRange[addresses.Count];

    for (var index = 0; index < addresses.Count; index++)
    {
        var start = addresses[index];
        var end = unchecked(start + 3u);
        ranges[index] = new AddressRange(start, end);
    }

    return ranges;
}

static bool IsMemoryAccessWithinRanges(
    uint address,
    int sizeBytes,
    IReadOnlyList<AddressRange> ranges)
{
    if (ranges.Count == 0)
    {
        return false;
    }

    if (sizeBytes <= 0)
    {
        return false;
    }

    var accessStart = address;
    var accessEnd = unchecked(address + (uint)(sizeBytes - 1));

    foreach (var range in ranges)
    {
        if (accessEnd < range.Start ||
            accessStart > range.End)
        {
            continue;
        }

        return true;
    }

    return false;
}

static PowerPcTraceSnapshot CapturePowerPcTraceSnapshot(PowerPc32CpuCore core)
{
    var generalPurposeRegisters = new uint[32];

    for (var index = 0; index < generalPurposeRegisters.Length; index++)
    {
        generalPurposeRegisters[index] = core.Registers[index];
    }

    return new PowerPcTraceSnapshot(
        generalPurposeRegisters,
        core.Registers.Lr,
        core.Registers.Ctr,
        core.Registers.Cr,
        core.Registers.Xer,
        core.MachineStateRegister);
}

static IReadOnlyList<PowerPcRegisterDelta> BuildPowerPcRegisterDeltas(
    PowerPcTraceSnapshot before,
    PowerPcTraceSnapshot after)
{
    var deltas = new List<PowerPcRegisterDelta>(40);

    for (var index = 0; index < before.GeneralPurposeRegisters.Length; index++)
    {
        var beforeValue = before.GeneralPurposeRegisters[index];
        var afterValue = after.GeneralPurposeRegisters[index];

        if (beforeValue == afterValue)
        {
            continue;
        }

        deltas.Add(new PowerPcRegisterDelta($"r{index}", beforeValue, afterValue));
    }

    if (before.LinkRegister != after.LinkRegister)
    {
        deltas.Add(new PowerPcRegisterDelta("lr", before.LinkRegister, after.LinkRegister));
    }

    if (before.CounterRegister != after.CounterRegister)
    {
        deltas.Add(new PowerPcRegisterDelta("ctr", before.CounterRegister, after.CounterRegister));
    }

    if (before.ConditionRegister != after.ConditionRegister)
    {
        deltas.Add(new PowerPcRegisterDelta("cr", before.ConditionRegister, after.ConditionRegister));
    }

    if (before.FixedPointExceptionRegister != after.FixedPointExceptionRegister)
    {
        deltas.Add(new PowerPcRegisterDelta("xer", before.FixedPointExceptionRegister, after.FixedPointExceptionRegister));
    }

    if (before.MachineStateRegister != after.MachineStateRegister)
    {
        deltas.Add(new PowerPcRegisterDelta("msr", before.MachineStateRegister, after.MachineStateRegister));
    }

    return deltas;
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
    bool traceWatchWordAccesses,
    bool traceWatchWordAllAddresses,
    int traceWatchWordAccessesMaxEvents,
    IReadOnlyList<AddressRange> traceWatchWordProgramCounterRanges,
    IReadOnlyList<AddressRange> traceWatchWordAddressRanges,
    IReadOnlyList<AddressRange> traceInstructionProgramCounterRanges,
    int traceInstructionMaxEvents,
    PowerPc32CpuCore? powerPcCore,
    long progressEveryInstructions,
    Func<ExecutionStopReason?>? stopEvaluator,
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
    var memoryAccessEvents = traceWatchWordAccesses &&
                             traceWatchWordAccessesMaxEvents > 0 &&
                             powerPcCore is not null
        ? new List<PowerPcMemoryAccessTraceEntry>()
        : null;
    var instructionTraceEvents = powerPcCore is not null &&
                                 traceInstructionProgramCounterRanges.Count > 0 &&
                                 traceInstructionMaxEvents > 0
        ? new List<PowerPcInstructionTraceEvent>()
        : null;
    var instructionTraceEnabled = instructionTraceEvents is not null;
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

        if (instructionTraceEnabled &&
            instructionTraceEvents!.Count < traceInstructionMaxEvents &&
            currentChunk > 1)
        {
            // Step one instruction at a time when instruction tracing is enabled.
            currentChunk = 1;
        }

        var traceProgramCounterBefore = 0u;
        var traceInstructionWord = 0u;
        PowerPcTraceSnapshot? traceSnapshotBefore = null;

        if (instructionTraceEnabled &&
            instructionTraceEvents!.Count < traceInstructionMaxEvents &&
            IsProgramCounterInRanges(machine.ProgramCounter, traceInstructionProgramCounterRanges))
        {
            traceProgramCounterBefore = machine.ProgramCounter;
            traceInstructionWord = machine.ReadUInt32(traceProgramCounterBefore);
            traceSnapshotBefore = CapturePowerPcTraceSnapshot(powerPcCore!);
        }

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
        IReadOnlyList<AddressRange>? traceWatchAddressRanges = null;
        IReadOnlyList<AddressRange>? traceWatchAddressRangeFilters = null;
        Action<PowerPcMemoryAccessTraceEntry>? priorMemoryTraceSink = null;
        var traceAllAddresses = traceWatchWordAllAddresses;
        var canTraceMemoryAccesses = powerPcCore is not null &&
                                     memoryAccessEvents is not null &&
                                     memoryAccessEvents.Count < traceWatchWordAccessesMaxEvents &&
                                     (traceAllAddresses ||
                                      currentTraceWatchWordAddresses.Length > 0 ||
                                      traceWatchWordAddressRanges.Count > 0);

        if (canTraceMemoryAccesses)
        {
            traceWatchAddressRanges = traceAllAddresses
                ? null
                : BuildWordWatchRanges(currentTraceWatchWordAddresses);
            traceWatchAddressRangeFilters = traceWatchWordAddressRanges.Count > 0
                ? traceWatchWordAddressRanges
                : null;
            priorMemoryTraceSink = powerPcCore!.MemoryAccessTraceSink;
            powerPcCore.MemoryAccessTraceSink = accessEntry =>
            {
                if (memoryAccessEvents!.Count >= traceWatchWordAccessesMaxEvents)
                {
                    return;
                }

                if (traceWatchAddressRanges is not null &&
                    !IsMemoryAccessWithinRanges(
                        accessEntry.PhysicalAddress,
                        accessEntry.SizeBytes,
                        traceWatchAddressRanges))
                {
                    return;
                }

                if (traceWatchAddressRangeFilters is not null &&
                    !IsMemoryAccessWithinRanges(
                        accessEntry.PhysicalAddress,
                        accessEntry.SizeBytes,
                        traceWatchAddressRangeFilters))
                {
                    return;
                }

                if (!IsProgramCounterInRanges(
                        accessEntry.ProgramCounter,
                        traceWatchWordProgramCounterRanges))
                {
                    return;
                }

                memoryAccessEvents.Add(accessEntry);
            };
        }

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

            if (memoryAccessEvents is { Count: > 0 })
            {
                errorDetails.AppendLine("MemoryAccessWatchBeforeFailure:");
                var start = Math.Max(0, memoryAccessEvents.Count - Math.Min(memoryAccessEvents.Count, 32));

                for (var index = start; index < memoryAccessEvents.Count; index++)
                {
                    var accessEvent = memoryAccessEvents[index];
                    errorDetails.AppendLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "  PC=0x{0:X8} TYPE={1} SIZE={2} EA=0x{3:X8} PA=0x{4:X8} VALUE=0x{5:X8}",
                            accessEvent.ProgramCounter,
                            accessEvent.AccessType,
                            accessEvent.SizeBytes,
                            accessEvent.EffectiveAddress,
                            accessEvent.PhysicalAddress,
                            accessEvent.Value));
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
        finally
        {
            if (canTraceMemoryAccesses)
            {
                powerPcCore!.MemoryAccessTraceSink = priorMemoryTraceSink;
            }
        }

        var chunkExecuted = traceSummary.Summary.ExecutedInstructions;

        if (traceSnapshotBefore.HasValue &&
            chunkExecuted > 0 &&
            instructionTraceEvents is not null)
        {
            var traceSnapshotAfter = CapturePowerPcTraceSnapshot(powerPcCore!);
            var registerDeltas = BuildPowerPcRegisterDeltas(traceSnapshotBefore.Value, traceSnapshotAfter);
            var description = DescribeInstruction(traceProgramCounterBefore, traceInstructionWord);

            instructionTraceEvents.Add(new PowerPcInstructionTraceEvent(
                traceProgramCounterBefore,
                machine.ProgramCounter,
                traceInstructionWord,
                description,
                registerDeltas));
        }

        executed += chunkExecuted;
        remaining -= chunkExecuted;
        MergeHotSpots(hotSpotCounters, traceSummary.HotSpots);
        MergeTrackedProgramCounterHits(trackedProgramCounterHits, traceSummary.TrackedProgramCounterHits);
        MergeProgramCounterTail(tail, traceSummary.ProgramCounterTail, tailLength);
        MergeMemoryWatchEvents(memoryWatchEvents, traceSummary.MemoryWatchEvents, DefaultMaxMemoryWatchEvents);

        if (stopEvaluator is not null)
        {
            var evaluatedStopReason = stopEvaluator();

            if (evaluatedStopReason.HasValue &&
                evaluatedStopReason.Value != ExecutionStopReason.None)
            {
                stopReason = evaluatedStopReason.Value;
                break;
            }
        }

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

        if (traceSummary.StopReason != ExecutionStopReason.InstructionBudgetReached &&
            traceSummary.StopReason != ExecutionStopReason.None)
        {
            stopReason = traceSummary.StopReason;
            break;
        }

        if (chunkExecuted == 0)
        {
            break;
        }
    }

    if (stopReason == ExecutionStopReason.None)
    {
        if (machine.Halted)
        {
            stopReason = ExecutionStopReason.Halted;
        }
        else if (stopAtProgramCounter.HasValue &&
                 machine.ProgramCounter == stopAtProgramCounter.Value)
        {
            stopReason = ExecutionStopReason.StopAtProgramCounter;
        }
        else if (stopAtProgramCounterHits.TryGetValue(machine.ProgramCounter, out var requiredHits) &&
                 trackedProgramCounterHits.TryGetValue(machine.ProgramCounter, out var hitCount) &&
                 hitCount >= requiredHits)
        {
            stopReason = ExecutionStopReason.StopAtProgramCounterHit;
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
        memoryWatchEvents?.ToArray() ?? Array.Empty<MemoryWatchTraceEntry>(),
        memoryAccessEvents?.ToArray() ?? Array.Empty<PowerPcMemoryAccessTraceEntry>(),
        instructionTraceEvents?.ToArray() ?? Array.Empty<PowerPcInstructionTraceEvent>());
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

    var memory = ResolveCheckpointSparseMemoryBus(state.Machine.MemoryBus);

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

    var memory = ResolveCheckpointSparseMemoryBus(state.Machine.MemoryBus);

    if (checkpoint.MemoryMb > configuredMemoryMb)
    {
        throw new InvalidOperationException(
            $"Checkpoint requires {checkpoint.MemoryMb} MB, but runtime was configured with {configuredMemoryMb} MB.");
    }

    memory.RestoreSnapshot(checkpoint.MemoryPages);
    cpu.RestoreSnapshot(checkpoint.CpuSnapshot);
}

static SparseMemoryBus ResolveCheckpointSparseMemoryBus(IMemoryBus memoryBus)
{
    if (memoryBus is SparseMemoryBus sparseMemoryBus)
    {
        return sparseMemoryBus;
    }

    if (memoryBus is MemoryMappedBus mappedBus &&
        mappedBus.BackingBus is SparseMemoryBus mappedSparseMemoryBus)
    {
        return mappedSparseMemoryBus;
    }

    throw new NotSupportedException(
        $"Checkpointing currently supports SparseMemoryBus-backed runtimes, got '{memoryBus.GetType().Name}'.");
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
        17 => DescribeSystemCall(instructionWord),
        18 => DescribeUnconditionalBranch(programCounter, instructionWord),
        19 => DescribeOpcode19(instructionWord),
        20 => DescribeRotateLeftWordImmediateThenMaskInsert(instructionWord),
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

static string DescribeRotateLeftWordImmediateThenMaskInsert(uint instructionWord)
{
    var rs = ExtractRs(instructionWord);
    var ra = ExtractRa(instructionWord);
    var shift = (int)((instructionWord >> 11) & 0x1F);
    var mb = (int)((instructionWord >> 6) & 0x1F);
    var me = (int)((instructionWord >> 1) & 0x1F);
    var record = (instructionWord & 0x1) != 0 ? "." : string.Empty;
    return $"rlwimi{record} ra=r{ra} rs=r{rs} sh={shift} mb={mb} me={me}";
}

static string DescribeSystemCall(uint instructionWord)
{
    var level = (instructionWord >> 5) & 0x7F;
    return level == 0
        ? "sc"
        : $"sc lev={level}";
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
        138 => $"adde{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        86 => "dcbf",
        144 => $"mtcrf mask=0x{((instructionWord >> 12) & 0xFF):X2} rs=r{rs}",
        146 => $"mtmsr rs=r{rs}",
        151 => $"stwx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        183 => $"stwux rs=r{rs} ra=r{ra} rb=r{rb}",
        200 => $"subfze{overflow}{record} rt=r{rt} ra=r{ra}",
        202 => $"addze{overflow}{record} rt=r{rt} ra=r{ra}",
        215 => $"stbx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        234 => $"addme{overflow}{record} rt=r{rt} ra=r{ra}",
        235 => $"mullw{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        246 => "dcbtst",
        247 => $"stbux rs=r{rs} ra=r{ra} rb=r{rb}",
        266 => $"add{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        278 => "dcbt",
        279 => $"lhzx rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        306 => $"tlbie rb=r{rb}",
        311 => $"lhzux rt=r{rt} ra=r{ra} rb=r{rb}",
        316 => $"xor{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        339 => $"mfspr rt=r{rt} spr={DescribeSpr(DecodeSpr(instructionWord))}",
        343 => $"lhax rt=r{rt} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        370 => "tlbia",
        371 => $"mftb rt=r{rt} spr={DescribeSpr(DecodeSpr(instructionWord))}",
        375 => $"lhaux rt=r{rt} ra=r{ra} rb=r{rb}",
        407 => $"sthx rs=r{rs} ra={DescribeBaseRegister(ra)} rb=r{rb}",
        439 => $"sthux rs=r{rs} ra=r{ra} rb=r{rb}",
        444 => $"or{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        459 => $"divwu{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        467 => $"mtspr spr={DescribeSpr(DecodeSpr(instructionWord))} rs=r{rs}",
        470 => "dcbi",
        476 => $"nand{record} ra=r{ra} rs=r{rs} rb=r{rb}",
        491 => $"divw{overflow}{record} rt=r{rt} ra=r{ra} rb=r{rb}",
        536 => $"srw ra=r{ra} rs=r{rs} rb=r{rb}{record}",
        597 => $"lswi rt=r{rt} ra={DescribeBaseRegister(ra)} nb={DescribeStringWordImmediateByteCount(rb)}",
        598 => "sync",
        725 => $"stswi rs=r{rs} ra={DescribeBaseRegister(ra)} nb={DescribeStringWordImmediateByteCount(rb)}",
        824 =>
            $"srawi{record} ra=r{ra} rs=r{rs} sh={(instructionWord >> 11) & 0x1F}",
        854 => "eieio",
        922 => $"extsh{record} ra=r{ra} rs=r{rs}",
        954 => $"extsb{record} ra=r{ra} rs=r{rs}",
        982 => "icbi",
        1014 => "dcbz",
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

static int DescribeStringWordImmediateByteCount(int encodedByteCount)
{
    return (encodedByteCount & 0x1F) switch
    {
        0 => 32,
        var byteCount => byteCount,
    };
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

static void PrintPowerPcTlbEntries(PowerPc32CpuCore powerPcCore)
{
    var instructionTlb = powerPcCore.GetInstructionTlbEntries();
    var dataTlb = powerPcCore.GetDataTlbEntries();

    if (instructionTlb.Count == 0 &&
        dataTlb.Count == 0)
    {
        return;
    }

    Console.WriteLine("InstructionTlb:");
    PrintPowerPcTlbEntrySet(instructionTlb);

    Console.WriteLine("DataTlb:");
    PrintPowerPcTlbEntrySet(dataTlb);
}

static void PrintPowerPcTlbEntrySet(IReadOnlyList<PowerPc32TlbEntryState> entries)
{
    if (entries.Count == 0)
    {
        Console.WriteLine("  <empty>");
        return;
    }

    foreach (var entry in entries.OrderBy(entry => entry.Index).Take(24))
    {
        var pageSizeBytes = DecodeMpc8xxPageSizeForDebug(entry.TableWalkControl, entry.RealPageNumber);
        Console.WriteLine(
            $"  IDX={entry.Index} EPN=0x{entry.EffectivePageNumber:X8} RPN=0x{entry.RealPageNumber:X8} " +
            $"TWC=0x{entry.TableWalkControl:X8} SIZE={pageSizeBytes}");
    }

    if (entries.Count > 24)
    {
        Console.WriteLine($"  ... {entries.Count - 24} more entries");
    }
}

static void PrintPowerPcStopSnapshot(
    EmulationMachine machine,
    PowerPc32CpuCore powerPcCore)
{
    var stackPointer = powerPcCore.Registers[1];
    var programCounter = powerPcCore.ProgramCounter;
    var linkRegister = powerPcCore.Registers.Lr;
    var dataTlbEntries = powerPcCore.GetDataTlbEntries();
    var offsetsMatchingProgramCounter = new List<int>();
    var offsetsMatchingLinkRegister = new List<int>();
    Console.WriteLine(
        $"StopStackSnapshot: PC=0x{programCounter:X8} SP=0x{stackPointer:X8} " +
        $"LR=0x{linkRegister:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8}");
    Console.WriteLine("StopStackWords:");

    for (var offset = -32; offset <= 96; offset += 4)
    {
        var effectiveAddress = unchecked((uint)((int)stackPointer + offset));
        var directValue = machine.ReadUInt32(effectiveAddress);
        var marker = offset == 0 ? "*" : " ";

        if (directValue == programCounter)
        {
            offsetsMatchingProgramCounter.Add(offset);
        }

        if (directValue == linkRegister)
        {
            offsetsMatchingLinkRegister.Add(offset);
        }

        if (TryTranslateViaMpc8xxTlbEntries(dataTlbEntries, effectiveAddress, out var translatedAddress))
        {
            var translatedValue = machine.ReadUInt32(translatedAddress);
            Console.WriteLine(
                $"{marker} OFF={offset,4:+#;-#;0} EA=0x{effectiveAddress:X8} DIRECT=0x{directValue:X8} " +
                $"PA=0x{translatedAddress:X8} VALUE=0x{translatedValue:X8}");
            continue;
        }

        Console.WriteLine(
            $"{marker} OFF={offset,4:+#;-#;0} EA=0x{effectiveAddress:X8} DIRECT=0x{directValue:X8}");
    }

    Console.WriteLine(
        $"StopStackSignals: LR_EQ_PC={linkRegister == programCounter} " +
        $"PC_OFFSETS={FormatStackOffsets(offsetsMatchingProgramCounter)} " +
        $"LR_OFFSETS={FormatStackOffsets(offsetsMatchingLinkRegister)}");
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

static IReadOnlyDictionary<string, string> ReadCStringDumps(
    EmulationMachine machine,
    IReadOnlyList<CStringDumpRequest> requests)
{
    if (requests.Count == 0)
    {
        return new Dictionary<string, string>(0, StringComparer.Ordinal);
    }

    var values = new Dictionary<string, string>(requests.Count, StringComparer.Ordinal);

    foreach (var request in requests)
    {
        var bytes = new List<byte>(request.MaxBytes);

        for (var index = 0; index < request.MaxBytes; index++)
        {
            var address = unchecked(request.Address + (uint)index);
            var value = machine.ReadByte(address);

            if (value == 0)
            {
                break;
            }

            bytes.Add(value);
        }

        values[$"0x{request.Address:X8}"] = FormatCStringBytes(bytes);
    }

    return values;
}

static void PrintCStringDumps(IReadOnlyDictionary<string, string> cStringDumps)
{
    if (cStringDumps.Count == 0)
    {
        return;
    }

    Console.WriteLine("CStringDumps:");

    foreach (var dump in cStringDumps.OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {dump.Key}=\"{dump.Value}\"");
    }
}

static IReadOnlyList<AddressRange> ResolveFindAsciiRanges(
    ProbeCliOptions cliOptions,
    int memoryMb)
{
    if (cliOptions.FindAsciiRanges.Count > 0)
    {
        return cliOptions.FindAsciiRanges;
    }

    if (memoryMb <= 0)
    {
        return Array.Empty<AddressRange>();
    }

    var start = 0x8000_0000u;
    var spanBytes = (ulong)memoryMb * 1024UL * 1024UL;
    var end = spanBytes == 0
        ? start
        : start + (uint)Math.Min(spanBytes - 1, uint.MaxValue - start);

    return [new AddressRange(start, end)];
}

static IReadOnlyDictionary<string, IReadOnlyList<uint>> FindAsciiMatches(
    EmulationMachine machine,
    IReadOnlyList<string> patterns,
    IReadOnlyList<AddressRange> ranges,
    int maxMatchesPerPattern)
{
    if (patterns.Count == 0 || ranges.Count == 0 || maxMatchesPerPattern <= 0)
    {
        return new Dictionary<string, IReadOnlyList<uint>>(0, StringComparer.Ordinal);
    }

    var encodedPatterns = patterns
        .Distinct(StringComparer.Ordinal)
        .Select(pattern => (Pattern: pattern, Bytes: Encoding.ASCII.GetBytes(pattern)))
        .Where(entry => entry.Bytes.Length > 0)
        .ToArray();

    if (encodedPatterns.Length == 0)
    {
        return new Dictionary<string, IReadOnlyList<uint>>(0, StringComparer.Ordinal);
    }

    var matches = encodedPatterns.ToDictionary(
        entry => entry.Pattern,
        _ => new List<uint>(),
        StringComparer.Ordinal);

    foreach (var range in ranges)
    {
        foreach (var entry in encodedPatterns)
        {
            var patternBytes = entry.Bytes;
            var currentMatches = matches[entry.Pattern];

            if (currentMatches.Count >= maxMatchesPerPattern ||
                range.End < range.Start ||
                range.End - range.Start + 1 < patternBytes.Length)
            {
                continue;
            }

            var lastStartAddress = range.End - (uint)patternBytes.Length + 1;

            for (var address = range.Start; address <= lastStartAddress; address++)
            {
                if (MatchesAsciiPattern(machine, address, patternBytes))
                {
                    currentMatches.Add(address);

                    if (currentMatches.Count >= maxMatchesPerPattern)
                    {
                        break;
                    }
                }

                if (address == uint.MaxValue)
                {
                    break;
                }
            }
        }
    }

    return matches
        .Where(entry => entry.Value.Count > 0)
        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<uint>)entry.Value.ToArray(),
            StringComparer.Ordinal);
}

static bool MatchesAsciiPattern(
    EmulationMachine machine,
    uint startAddress,
    ReadOnlySpan<byte> patternBytes)
{
    for (var index = 0; index < patternBytes.Length; index++)
    {
        var address = unchecked(startAddress + (uint)index);
        var value = machine.ReadByte(address);

        if (value != patternBytes[index])
        {
            return false;
        }
    }

    return true;
}

static void PrintAsciiMatches(IReadOnlyDictionary<string, IReadOnlyList<uint>> asciiMatches)
{
    if (asciiMatches.Count == 0)
    {
        return;
    }

    Console.WriteLine("AsciiMatches:");

    foreach (var match in asciiMatches.OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"  \"{match.Key}\": {string.Join(", ", match.Value.Select(address => $"0x{address:X8}"))}");
    }
}

static string FormatCStringBytes(IReadOnlyList<byte> bytes)
{
    if (bytes.Count == 0)
    {
        return string.Empty;
    }

    var builder = new StringBuilder(bytes.Count);

    foreach (var value in bytes)
    {
        if (TryDecodeConsoleCharacter(value, out var character))
        {
            switch (character)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    builder.Append(character);
                    break;
            }

            continue;
        }

        builder.Append("\\x");
        builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
    }

    return builder.ToString();
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

static IReadOnlyList<ProbeSupervisorCallsiteReport> BuildSupervisorCallsiteCounts(
    IReadOnlyList<PowerPcSupervisorCallTraceEntry> supervisorCallTrace)
{
    if (supervisorCallTrace.Count == 0)
    {
        return Array.Empty<ProbeSupervisorCallsiteReport>();
    }

    return supervisorCallTrace
        .GroupBy(entry => (entry.ServiceCode, entry.CallerProgramCounter))
        .OrderByDescending(group => group.LongCount())
        .ThenBy(group => group.Key.ServiceCode)
        .ThenBy(group => group.Key.CallerProgramCounter)
        .Select(group => new ProbeSupervisorCallsiteReport(
            ServiceCode: $"0x{group.Key.ServiceCode:X8}",
            CallerProgramCounter: $"0x{group.Key.CallerProgramCounter:X8}",
            Hits: group.LongCount()))
        .ToArray();
}

static IReadOnlyList<ProbeSupervisorSignatureReport> BuildSupervisorSignatureCounts(
    IReadOnlyList<PowerPcSupervisorCallTraceEntry> supervisorCallTrace)
{
    if (supervisorCallTrace.Count == 0)
    {
        return Array.Empty<ProbeSupervisorSignatureReport>();
    }

    return supervisorCallTrace
        .GroupBy(entry => new
        {
            entry.ServiceCode,
            entry.CallerProgramCounter,
            entry.Argument0,
            entry.Argument1,
            entry.Argument2,
            entry.Argument3,
            entry.ReturnValue,
        })
        .OrderByDescending(group => group.LongCount())
        .ThenBy(group => group.Key.ServiceCode)
        .ThenBy(group => group.Key.CallerProgramCounter)
        .ThenBy(group => group.Key.Argument0)
        .ThenBy(group => group.Key.Argument1)
        .ThenBy(group => group.Key.Argument2)
        .ThenBy(group => group.Key.Argument3)
        .ThenBy(group => group.Key.ReturnValue)
        .Select(group => new ProbeSupervisorSignatureReport(
            ServiceCode: $"0x{group.Key.ServiceCode:X8}",
            CallerProgramCounter: $"0x{group.Key.CallerProgramCounter:X8}",
            Argument0: $"0x{group.Key.Argument0:X8}",
            Argument1: $"0x{group.Key.Argument1:X8}",
            Argument2: $"0x{group.Key.Argument2:X8}",
            Argument3: $"0x{group.Key.Argument3:X8}",
            ReturnValue: $"0x{group.Key.ReturnValue:X8}",
            Hits: group.LongCount()))
        .ToArray();
}

static ProbePowerPcCpuStateReport CreatePowerPcCpuStateReport(PowerPc32CpuCore powerPcCore)
{
    var gpr = new Dictionary<string, string>(32, StringComparer.Ordinal);

    for (var index = 0; index < 32; index++)
    {
        gpr[$"r{index}"] = $"0x{powerPcCore.Registers[index]:X8}";
    }

    var specialPurposeRegisters = powerPcCore.ExtendedSpecialPurposeRegisters
        .OrderBy(entry => entry.Key)
        .ToDictionary(
            entry => $"SPR[{entry.Key}]",
            entry => $"0x{entry.Value:X8}",
            StringComparer.Ordinal);

    return new ProbePowerPcCpuStateReport(
        ProgramCounter: $"0x{powerPcCore.ProgramCounter:X8}",
        LinkRegister: $"0x{powerPcCore.Registers.Lr:X8}",
        CounterRegister: $"0x{powerPcCore.Registers.Ctr:X8}",
        ConditionRegister: $"0x{powerPcCore.Registers.Cr:X8}",
        FixedPointExceptionRegister: $"0x{powerPcCore.Registers.Xer:X8}",
        MachineStateRegister: $"0x{powerPcCore.MachineStateRegister:X8}",
        GeneralPurposeRegisters: gpr,
        SpecialPurposeRegisters: specialPurposeRegisters);
}

static ProbePowerPcStopSnapshotReport CreatePowerPcStopSnapshotReport(
    EmulationMachine machine,
    PowerPc32CpuCore powerPcCore)
{
    var stackPointer = powerPcCore.Registers[1];
    var programCounter = powerPcCore.ProgramCounter;
    var linkRegister = powerPcCore.Registers.Lr;
    var dataTlbEntries = powerPcCore.GetDataTlbEntries();
    var stackWords = new List<ProbePowerPcStackWordReport>();
    var stackOffsetsMatchingProgramCounter = new List<int>();
    var stackOffsetsMatchingLinkRegister = new List<int>();

    for (var offset = -32; offset <= 96; offset += 4)
    {
        var effectiveAddress = unchecked((uint)((int)stackPointer + offset));
        var directValue = machine.ReadUInt32(effectiveAddress);
        string? translatedAddress = null;
        string? translatedValue = null;

        if (TryTranslateViaMpc8xxTlbEntries(dataTlbEntries, effectiveAddress, out var physicalAddress))
        {
            translatedAddress = $"0x{physicalAddress:X8}";
            translatedValue = $"0x{machine.ReadUInt32(physicalAddress):X8}";
        }

        if (directValue == programCounter)
        {
            stackOffsetsMatchingProgramCounter.Add(offset);
        }

        if (directValue == linkRegister)
        {
            stackOffsetsMatchingLinkRegister.Add(offset);
        }

        stackWords.Add(new ProbePowerPcStackWordReport(
            Offset: offset,
            EffectiveAddress: $"0x{effectiveAddress:X8}",
            DirectValue: $"0x{directValue:X8}",
            PhysicalAddress: translatedAddress,
            PhysicalValue: translatedValue));
    }

    return new ProbePowerPcStopSnapshotReport(
        ProgramCounter: $"0x{programCounter:X8}",
        StackPointer: $"0x{stackPointer:X8}",
        LinkRegister: $"0x{linkRegister:X8}",
        Register30: $"0x{powerPcCore.Registers[30]:X8}",
        Register31: $"0x{powerPcCore.Registers[31]:X8}",
        CounterRegister: $"0x{powerPcCore.Registers.Ctr:X8}",
        ConditionRegister: $"0x{powerPcCore.Registers.Cr:X8}",
        MachineStateRegister: $"0x{powerPcCore.MachineStateRegister:X8}",
        LinkRegisterEqualsProgramCounter: linkRegister == programCounter,
        StackOffsetsMatchingProgramCounter: stackOffsetsMatchingProgramCounter,
        StackOffsetsMatchingLinkRegister: stackOffsetsMatchingLinkRegister,
        StackWords: stackWords);
}

static IReadOnlyList<ProbeTlbEntryReport> CreateTlbEntryReports(
    IReadOnlyList<PowerPc32TlbEntryState> entries)
{
    if (entries.Count == 0)
    {
        return Array.Empty<ProbeTlbEntryReport>();
    }

    return entries
        .OrderBy(entry => entry.Index)
        .Select(entry => new ProbeTlbEntryReport(
            Index: entry.Index,
            EffectivePageNumber: $"0x{entry.EffectivePageNumber:X8}",
            RealPageNumber: $"0x{entry.RealPageNumber:X8}",
            TableWalkControl: $"0x{entry.TableWalkControl:X8}",
            PageSizeBytes: DecodeMpc8xxPageSizeForDebug(entry.TableWalkControl, entry.RealPageNumber)))
        .ToArray();
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
    bool stopOnSupervisorSignatureReached,
    bool stopOnSupervisorSignatureHitReached,
    SupervisorCallSignatureKey? stopOnSupervisorSignatureHitMatched,
    int? stopOnSupervisorSignatureHitMatchedCount,
    bool stopOnConsoleRepeatReached,
    IReadOnlyList<KeyValuePair<uint, long>> topHotSpots,
    IReadOnlyDictionary<uint, long> trackedProgramCounterHits,
    IReadOnlyDictionary<string, uint> namedGlobals,
    IReadOnlyDictionary<string, string> cStringDumps,
    IReadOnlyDictionary<string, IReadOnlyList<uint>> asciiMatches,
    IReadOnlyList<string> profileNames,
    long nullProgramCounterRedirectCount,
    IReadOnlyList<PowerPcNullProgramCounterRedirectEvent> nullProgramCounterRedirectEvents,
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
    var cStringDumpValues = cStringDumps
        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
    var asciiMatchValues = asciiMatches
        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
        .ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value
                .Select(address => $"0x{address:X8}")
                .ToArray(),
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
    var memoryAccessEvents = traceRun.MemoryAccessEvents
        .Select(entry => new ProbeMemoryAccessEventReport(
            $"0x{entry.ProgramCounter:X8}",
            entry.AccessType.ToString(),
            entry.SizeBytes,
            $"0x{entry.EffectiveAddress:X8}",
            $"0x{entry.PhysicalAddress:X8}",
            $"0x{entry.Value:X8}"))
        .ToArray();
    var instructionTraceEvents = traceRun.InstructionTraceEvents
        .Select(entry => new ProbeInstructionTraceEventReport(
            $"0x{entry.ProgramCounterBefore:X8}",
            $"0x{entry.ProgramCounterAfter:X8}",
            $"0x{entry.InstructionWord:X8}",
            entry.Description,
            entry.RegisterDeltas
                .Select(delta => new ProbeRegisterDeltaReport(
                    delta.RegisterName,
                    $"0x{delta.BeforeValue:X8}",
                    $"0x{delta.AfterValue:X8}"))
                .ToArray()))
        .ToArray();
    var nullProgramCounterRedirectTrace = nullProgramCounterRedirectEvents
        .TakeLast(64)
        .Select(entry => new ProbeNullProgramCounterRedirectReport(
            entry.Source.ToString(),
            $"0x{entry.RedirectTarget:X8}",
            $"0x{entry.CandidateValue:X8}",
            entry.StackAddress.HasValue ? $"0x{entry.StackAddress.Value:X8}" : null,
            $"0x{entry.StackPointer:X8}",
            $"0x{entry.LinkRegister:X8}",
            $"0x{entry.Register30:X8}",
            $"0x{entry.Register31:X8}",
            $"0x{entry.StackWordMinus24:X8}",
            $"0x{entry.StackWordMinus20:X8}",
            $"0x{entry.StackWordMinus16:X8}",
            $"0x{entry.StackWordAtPointer:X8}",
            $"0x{entry.StackWordPlus4:X8}",
            $"0x{entry.StackWordPlus8:X8}"))
        .ToArray();
    var nullProgramCounterRedirectSourceCounts = nullProgramCounterRedirectEvents
        .GroupBy(entry => entry.Source)
        .OrderBy(entry => entry.Key)
        .ToDictionary(
            entry => entry.Key.ToString(),
            entry => (long)entry.LongCount(),
            StringComparer.Ordinal);
    var supervisorTrace = supervisorCallTrace
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
            entry.NextProgramCounter.HasValue ? $"0x{entry.NextProgramCounter.Value:X8}" : null,
            $"0x{entry.StackPointer:X8}",
            $"0x{entry.Register30:X8}",
            $"0x{entry.Register31:X8}",
            $"0x{entry.StackWordMinus16:X8}",
            $"0x{entry.StackWordAtPointer:X8}",
            $"0x{entry.StackWordPlus4:X8}",
            $"0x{entry.StackWordPlus8:X8}"))
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
    var supervisorCallsites = BuildSupervisorCallsiteCounts(supervisorCallTrace)
        .ToDictionary(
            entry => $"{entry.ServiceCode}@{entry.CallerProgramCounter}",
            entry => entry.Hits,
            StringComparer.Ordinal);
    var supervisorSignatures = BuildSupervisorSignatureCounts(supervisorCallTrace)
        .ToDictionary(
            entry => $"{entry.ServiceCode}@{entry.CallerProgramCounter} " +
                     $"a0={entry.Argument0} a1={entry.Argument1} a2={entry.Argument2} a3={entry.Argument3} " +
                     $"ret={entry.ReturnValue}",
            entry => entry.Hits,
            StringComparer.Ordinal);
    var powerPcCore = state.CpuCore as PowerPc32CpuCore;
    var powerPcCpuState = powerPcCore is null
        ? null
        : CreatePowerPcCpuStateReport(powerPcCore);
    var powerPcStopSnapshot = powerPcCore is null
        ? null
        : CreatePowerPcStopSnapshotReport(state.Machine, powerPcCore);
    var instructionTlb = powerPcCore is null
        ? Array.Empty<ProbeTlbEntryReport>()
        : CreateTlbEntryReports(powerPcCore.GetInstructionTlbEntries());
    var dataTlb = powerPcCore is null
        ? Array.Empty<ProbeTlbEntryReport>()
        : CreateTlbEntryReports(powerPcCore.GetDataTlbEntries());

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
        stopOnSupervisorSignatureReached,
        stopOnSupervisorSignatureHitReached,
        stopOnSupervisorSignatureHitMatched.HasValue
            ? $"0x{stopOnSupervisorSignatureHitMatched.Value.ServiceCode:X8}@0x{stopOnSupervisorSignatureHitMatched.Value.CallerProgramCounter:X8}/0x{stopOnSupervisorSignatureHitMatched.Value.Argument0:X8}/0x{stopOnSupervisorSignatureHitMatched.Value.Argument1:X8}/0x{stopOnSupervisorSignatureHitMatched.Value.Argument2:X8}/0x{stopOnSupervisorSignatureHitMatched.Value.Argument3:X8}"
            : null,
        stopOnSupervisorSignatureHitMatchedCount,
        stopOnConsoleRepeatReached,
        profileNames.ToArray(),
        nullProgramCounterRedirectCount,
        nullProgramCounterRedirectSourceCounts,
        nullProgramCounterRedirectTrace,
        globalValues,
        cStringDumpValues,
        asciiMatchValues,
        trackedHits,
        hotSpots,
        tail,
        watchEvents,
        memoryAccessEvents,
        instructionTraceEvents,
        supervisorCallsTotal,
        supervisorCallsDelta,
        supervisorCallsites,
        supervisorSignatures,
        supervisorTrace,
        powerPcCpuState,
        powerPcStopSnapshot,
        instructionTlb,
        dataTlb,
        consoleOutput);
}

static string FormatStackOffsets(IReadOnlyList<int> offsets)
{
    if (offsets.Count == 0)
    {
        return "-";
    }

    return string.Join(
        ",",
        offsets.Select(offset => offset.ToString("+#;-#;0", CultureInfo.InvariantCulture)));
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

sealed record ConsoleRepeatStopRule(
    string Text,
    long RequiredHits);

sealed record ProbeCliOptions(
    string? CheckpointFilePath,
    string? SaveCheckpointFilePath,
    string? ReportJsonPath,
    long? CheckpointAtInstructions,
    bool CheckpointForceRebuild,
    bool ResumeHalted,
    int ChunkBudget,
    int MaxHotSpots,
    long ProgressEveryInstructions,
    IReadOnlyList<ConsoleRepeatStopRule> StopOnConsoleRepeatRules,
    bool AutoConsoleScript,
    uint? StopAtProgramCounter,
    IReadOnlyDictionary<uint, long> StopAtProgramCounterHits,
    uint? StopOnSupervisorService,
    int SupervisorTraceMaxEvents,
    bool SupervisorTraceIncludePutCharacter,
    IReadOnlySet<SupervisorCallSignatureKey> StopOnSupervisorSignatures,
    IReadOnlyDictionary<SupervisorCallSignatureKey, int> StopOnSupervisorSignatureHits,
    int TailLength,
    IReadOnlyDictionary<uint, uint> SupervisorReturnOverrides,
    IReadOnlyDictionary<SupervisorCallsiteKey, uint> SupervisorReturnCallerOverrides,
    IReadOnlyDictionary<SupervisorCallSignatureKey, uint> SupervisorReturnSignatureOverrides,
    IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> SupervisorReturnCallerHitOverrides,
    IReadOnlyDictionary<uint, uint> MemoryWriteOverrides,
    IReadOnlyList<InstructionWindowRequest> AdditionalInstructionWindows,
    IReadOnlyList<uint> WatchWordAddresses,
    IReadOnlyList<DynamicWatchWordRequest> DynamicWatchWordRequests,
    IReadOnlyList<uint> WatchWordEffectiveAddresses,
    IReadOnlyList<uint> StopOnWatchWordChangeAddresses,
    IReadOnlyList<DynamicWatchWordRequest> DynamicStopOnWatchWordChangeRequests,
    IReadOnlyList<uint> StopOnWatchWordChangeEffectiveAddresses,
    bool TraceWatch32Accesses,
    bool TraceWatch32AllAddresses,
    int TraceWatch32AccessesMaxEvents,
    IReadOnlyList<AddressRange> TraceWatch32ProgramCounterRanges,
    IReadOnlyList<AddressRange> TraceWatch32AddressRanges,
    IReadOnlyList<AddressRange> TraceInstructionProgramCounterRanges,
    int TraceInstructionMaxEvents,
    IReadOnlyList<uint> TrackedProgramCounters,
    IReadOnlyList<NamedAddress> NamedGlobalAddresses,
    IReadOnlyList<NamedAddress> NamedGlobalEffectiveAddresses,
    IReadOnlyList<CStringDumpRequest> CStringDumpRequests,
    IReadOnlyList<string> FindAsciiPatterns,
    IReadOnlyList<AddressRange> FindAsciiRanges,
    int FindAsciiMaxResults,
    IReadOnlyList<string> ProfileNames,
    bool DisableNullProgramCounterRedirect,
    bool Disable8MbHighBitAlias,
    bool DisableDynarec);

sealed record TracedRunResult(
    long ExecutedInstructions,
    IReadOnlyList<uint> ProgramCounterTail,
    ExecutionStopReason StopReason,
    IReadOnlyList<MemoryWatchTraceEntry> MemoryWatchEvents,
    IReadOnlyList<PowerPcMemoryAccessTraceEntry> MemoryAccessEvents,
    IReadOnlyList<PowerPcInstructionTraceEvent> InstructionTraceEvents);

readonly record struct PowerPcTraceSnapshot(
    uint[] GeneralPurposeRegisters,
    uint LinkRegister,
    uint CounterRegister,
    uint ConditionRegister,
    uint FixedPointExceptionRegister,
    uint MachineStateRegister);

readonly record struct PowerPcRegisterDelta(
    string RegisterName,
    uint BeforeValue,
    uint AfterValue);

sealed record PowerPcInstructionTraceEvent(
    uint ProgramCounterBefore,
    uint ProgramCounterAfter,
    uint InstructionWord,
    string Description,
    IReadOnlyList<PowerPcRegisterDelta> RegisterDeltas);

sealed record TraceProgress(
    long ExecutedInstructions,
    long RemainingInstructions,
    uint ProgramCounter,
    double InstructionsPerSecond,
    ExecutionStopReason LastChunkStopReason);

sealed record ProbeRunReport(
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
    bool StopOnSupervisorSignatureReached,
    bool StopOnSupervisorSignatureHitReached,
    string? StopOnSupervisorSignatureHitMatched,
    int? StopOnSupervisorSignatureHitMatchedCount,
    bool StopOnConsoleRepeatReached,
    IReadOnlyList<string> Profiles,
    long NullProgramCounterRedirectCount,
    IReadOnlyDictionary<string, long> NullProgramCounterRedirectSources,
    IReadOnlyList<ProbeNullProgramCounterRedirectReport> NullProgramCounterRedirectTrace,
    IReadOnlyDictionary<string, string> Globals32,
    IReadOnlyDictionary<string, string> CStringDumps,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AsciiMatches,
    IReadOnlyDictionary<string, long> TrackedProgramCounterHits,
    IReadOnlyList<ProbeHotSpotReport> HotSpots,
    IReadOnlyList<string> ProgramCounterTail,
    IReadOnlyList<ProbeMemoryWatchEventReport> MemoryWatchEvents,
    IReadOnlyList<ProbeMemoryAccessEventReport> MemoryAccessEvents,
    IReadOnlyList<ProbeInstructionTraceEventReport> InstructionTraceEvents,
    IReadOnlyDictionary<string, long> SupervisorCallsTotal,
    IReadOnlyDictionary<string, long> SupervisorCallsDelta,
    IReadOnlyDictionary<string, long> SupervisorCallsites,
    IReadOnlyDictionary<string, long> SupervisorSignatures,
    IReadOnlyList<ProbeSupervisorCallTraceReport> SupervisorTrace,
    ProbePowerPcCpuStateReport? PowerPcCpuState,
    ProbePowerPcStopSnapshotReport? PowerPcStopSnapshot,
    IReadOnlyList<ProbeTlbEntryReport> InstructionTlb,
    IReadOnlyList<ProbeTlbEntryReport> DataTlb,
    string ConsoleOutput);

sealed record ProbeHotSpotReport(
    string ProgramCounter,
    long Hits);

sealed record ProbeMemoryWatchEventReport(
    string ProgramCounter,
    string Address,
    string PreviousValue,
    string CurrentValue);

sealed record ProbeMemoryAccessEventReport(
    string ProgramCounter,
    string AccessType,
    int SizeBytes,
    string EffectiveAddress,
    string PhysicalAddress,
    string Value);

sealed record ProbeRegisterDeltaReport(
    string Register,
    string Before,
    string After);

sealed record ProbeInstructionTraceEventReport(
    string ProgramCounterBefore,
    string ProgramCounterAfter,
    string InstructionWord,
    string Description,
    IReadOnlyList<ProbeRegisterDeltaReport> RegisterDeltas);

sealed record ProbeNullProgramCounterRedirectReport(
    string Source,
    string RedirectTarget,
    string CandidateValue,
    string? StackAddress,
    string StackPointer,
    string LinkRegister,
    string Register30,
    string Register31,
    string StackWordMinus24,
    string StackWordMinus20,
    string StackWordMinus16,
    string StackWordAtPointer,
    string StackWordPlus4,
    string StackWordPlus8);

sealed record ProbeSupervisorCallTraceReport(
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
    string? NextProgramCounter,
    string StackPointer,
    string Register30,
    string Register31,
    string StackWordMinus16,
    string StackWordAtPointer,
    string StackWordPlus4,
    string StackWordPlus8);

sealed record ProbeSupervisorCallsiteReport(
    string ServiceCode,
    string CallerProgramCounter,
    long Hits);

sealed record ProbeSupervisorSignatureReport(
    string ServiceCode,
    string CallerProgramCounter,
    string Argument0,
    string Argument1,
    string Argument2,
    string Argument3,
    string ReturnValue,
    long Hits);

sealed record ProbePowerPcCpuStateReport(
    string ProgramCounter,
    string LinkRegister,
    string CounterRegister,
    string ConditionRegister,
    string FixedPointExceptionRegister,
    string MachineStateRegister,
    IReadOnlyDictionary<string, string> GeneralPurposeRegisters,
    IReadOnlyDictionary<string, string> SpecialPurposeRegisters);

sealed record ProbePowerPcStopSnapshotReport(
    string ProgramCounter,
    string StackPointer,
    string LinkRegister,
    string Register30,
    string Register31,
    string CounterRegister,
    string ConditionRegister,
    string MachineStateRegister,
    bool LinkRegisterEqualsProgramCounter,
    IReadOnlyList<int> StackOffsetsMatchingProgramCounter,
    IReadOnlyList<int> StackOffsetsMatchingLinkRegister,
    IReadOnlyList<ProbePowerPcStackWordReport> StackWords);

sealed record ProbePowerPcStackWordReport(
    int Offset,
    string EffectiveAddress,
    string DirectValue,
    string? PhysicalAddress,
    string? PhysicalValue);

sealed record ProbeTlbEntryReport(
    int Index,
    string EffectivePageNumber,
    string RealPageNumber,
    string TableWalkControl,
    uint PageSizeBytes);

static class ProbeReportJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
}

sealed record InstructionWindowRequest(
    uint Address,
    int Before,
    int After);

sealed record NamedAddress(
    string Name,
    uint Address);

sealed record CStringDumpRequest(
    uint Address,
    int MaxBytes);

sealed record DynamicWatchWordRequest(
    int RegisterIndex,
    int Offset);

readonly record struct AddressRange(
    uint Start,
    uint End);

readonly record struct SupervisorCallsiteKey(
    uint ServiceCode,
    uint CallerProgramCounter);

readonly record struct SupervisorCallSignatureKey(
    uint ServiceCode,
    uint CallerProgramCounter,
    uint Argument0,
    uint Argument1,
    uint Argument2,
    uint Argument3);

readonly record struct SupervisorCallsiteHitKey(
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

file sealed class StopOnSupervisorSignaturePowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly IReadOnlySet<SupervisorCallSignatureKey> _signatures;

    public StopOnSupervisorSignaturePowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        IReadOnlySet<SupervisorCallSignatureKey> signatures)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
    }

    public bool StopReached { get; private set; }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        var result = _inner.Handle(context);

        if (StopReached || _signatures.Count == 0)
        {
            return result;
        }

        var signature = new SupervisorCallSignatureKey(
            context.ServiceCode,
            context.CallerProgramCounter,
            context.Argument0,
            context.Argument1,
            context.Argument2,
            context.Argument3);

        if (!_signatures.Contains(signature))
        {
            return result;
        }

        StopReached = true;
        return result with { Halt = true };
    }
}

file sealed class StopOnSupervisorSignatureHitPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly IReadOnlyDictionary<SupervisorCallSignatureKey, int> _signatureHits;
    private readonly Dictionary<SupervisorCallSignatureKey, int> _counters = new();

    public StopOnSupervisorSignatureHitPowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        IReadOnlyDictionary<SupervisorCallSignatureKey, int> signatureHits)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _signatureHits = signatureHits ?? throw new ArgumentNullException(nameof(signatureHits));
    }

    public bool StopReached { get; private set; }

    public SupervisorCallSignatureKey? MatchedSignature { get; private set; }

    public int? MatchedHitCount { get; private set; }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        var result = _inner.Handle(context);

        if (StopReached || _signatureHits.Count == 0)
        {
            return result;
        }

        var signature = new SupervisorCallSignatureKey(
            context.ServiceCode,
            context.CallerProgramCounter,
            context.Argument0,
            context.Argument1,
            context.Argument2,
            context.Argument3);

        if (!_signatureHits.TryGetValue(signature, out var requiredHits))
        {
            return result;
        }

        _counters.TryGetValue(signature, out var currentHits);
        var nextHits = currentHits + 1;
        _counters[signature] = nextHits;

        if (nextHits < requiredHits)
        {
            return result;
        }

        StopReached = true;
        MatchedSignature = signature;
        MatchedHitCount = nextHits;
        return result with { Halt = true };
    }
}

file sealed class OverrideReturnPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private readonly IPowerPcSupervisorCallHandler _inner;
    private readonly IReadOnlyDictionary<uint, uint> _overrides;
    private readonly IReadOnlyDictionary<SupervisorCallsiteKey, uint> _callsiteOverrides;
    private readonly IReadOnlyDictionary<SupervisorCallSignatureKey, uint> _signatureOverrides;
    private readonly IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> _callsiteHitOverrides;
    private readonly Dictionary<SupervisorCallsiteKey, int> _callsiteHitCounters = new();

    public OverrideReturnPowerPcSupervisorCallHandler(
        IPowerPcSupervisorCallHandler inner,
        IReadOnlyDictionary<uint, uint> overrides,
        IReadOnlyDictionary<SupervisorCallsiteKey, uint> callsiteOverrides,
        IReadOnlyDictionary<SupervisorCallSignatureKey, uint> signatureOverrides,
        IReadOnlyDictionary<SupervisorCallsiteHitKey, uint> callsiteHitOverrides)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _callsiteOverrides = callsiteOverrides ?? throw new ArgumentNullException(nameof(callsiteOverrides));
        _signatureOverrides = signatureOverrides ?? throw new ArgumentNullException(nameof(signatureOverrides));
        _callsiteHitOverrides = callsiteHitOverrides ?? throw new ArgumentNullException(nameof(callsiteHitOverrides));
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        var innerResult = _inner.Handle(context);
        var callsiteKey = new SupervisorCallsiteKey(context.ServiceCode, context.CallerProgramCounter);
        _callsiteHitCounters.TryGetValue(callsiteKey, out var currentCallsiteHits);
        var nextHit = currentCallsiteHits + 1;
        _callsiteHitCounters[callsiteKey] = nextHit;
        var callsiteHitKey = new SupervisorCallsiteHitKey(callsiteKey.ServiceCode, callsiteKey.CallerProgramCounter, nextHit);

        if (_callsiteHitOverrides.TryGetValue(callsiteHitKey, out var callsiteHitReturnValue))
        {
            return innerResult with { ReturnValue = callsiteHitReturnValue };
        }

        var signatureKey = new SupervisorCallSignatureKey(
            context.ServiceCode,
            context.CallerProgramCounter,
            context.Argument0,
            context.Argument1,
            context.Argument2,
            context.Argument3);

        if (_signatureOverrides.TryGetValue(signatureKey, out var signatureReturnValue))
        {
            return innerResult with { ReturnValue = signatureReturnValue };
        }

        if (_callsiteOverrides.TryGetValue(callsiteKey, out var callsiteReturnValue))
        {
            return innerResult with { ReturnValue = callsiteReturnValue };
        }

        if (_overrides.TryGetValue(context.ServiceCode, out var returnValue))
        {
            return innerResult with { ReturnValue = returnValue };
        }

        return innerResult;
    }
}

file sealed record ProbeCheckpoint(
    long ExecutedInstructionsFromBoot,
    int MemoryMb,
    PowerPc32CpuSnapshot CpuSnapshot,
    IReadOnlyList<SparseMemoryPageSnapshot> MemoryPages);
