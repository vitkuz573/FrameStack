using System.Globalization;
using System.IO.Compression;
using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

const int DefaultChunkBudget = 200_000_000;
const long DefaultCheckpointAtInstructions = 2_200_000_000;
const uint CheckpointMagic = 0x4653_5043; // "FSPC"
const int CheckpointVersion = 1;
const int CheckpointPageSize = 4096;

if (args.Length == 0)
{
    Console.WriteLine(
        "Usage: dotnet run --project tools/FrameStack.ImageProbe -- <image-path> [instruction-budget] [memory-mb] [timeline-steps] " +
        "[register=value ...] [--checkpoint-file <path>] [--checkpoint-at <instructions>] [--checkpoint-force-rebuild] [--chunk-budget <instructions>]");
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

if (cliOptions.CheckpointFilePath is not null)
{
    Console.WriteLine($"CheckpointFile: {cliOptions.CheckpointFilePath}");
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

    ApplyRegisterOverrides(state.CpuCore, registerOverrides);

    PowerPcTracingSupervisorCallHandler? supervisorTracer = null;
    var powerPcCore = state.CpuCore as PowerPc32CpuCore;

    if (powerPcCore is not null)
    {
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
                    $"       R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
                    $"R10=0x{powerPcCore.Registers[10]:X8} R27=0x{powerPcCore.Registers[27]:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8}");
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
    var tracedExecuted = RunBudgetWithTrace(
        state.Machine,
        remainingBudget,
        cliOptions.ChunkBudget,
        hotSpotCounters);

    var executedThisRun = timelineExecuted + tracedExecuted;
    var executedFromBoot = baseExecutedInstructions + executedThisRun;

    Console.WriteLine();
    Console.WriteLine("Preflight Run:");
    Console.WriteLine($"ExecutedInstructions: {executedThisRun}");
    if (baseExecutedInstructions > 0)
    {
        Console.WriteLine($"ExecutedInstructionsFromBoot: {executedFromBoot}");
    }
    Console.WriteLine($"Halted: {state.Machine.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{state.Machine.ProgramCounter:X8}");

    if (powerPcCore is not null)
    {
        Console.WriteLine(
            $"FinalRegisters: R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
            $"R8=0x{powerPcCore.Registers[8]:X8} R9=0x{powerPcCore.Registers[9]:X8} R10=0x{powerPcCore.Registers[10]:X8} " +
            $"R27=0x{powerPcCore.Registers[27]:X8} R29=0x{powerPcCore.Registers[29]:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8}");
    }

    PrintFirmwareGlobals(state.Machine);
    Console.WriteLine("HotSpots:");

    foreach (var hotSpot in hotSpotCounters
                 .OrderByDescending(entry => entry.Value)
                 .ThenBy(entry => entry.Key)
                 .Take(12))
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
        supervisorTracer.ConsoleOutput.Length > 0)
    {
        Console.WriteLine("ConsoleOutput:");
        Console.WriteLine(supervisorTracer.ConsoleOutput);
    }

    return 0;
}
catch (Exception exception)
{
    Console.WriteLine();
    Console.WriteLine("Preflight Run Failed:");
    Console.WriteLine(exception.Message);
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
    long? checkpointAtInstructions = null;
    var checkpointForceRebuild = false;
    var chunkBudget = DefaultChunkBudget;
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
            case "--checkpoint-force-rebuild":
                checkpointForceRebuild = true;
                break;
            case "--chunk-budget":
                optionValue ??= ReadRequiredOptionValue(tokens, ref index, optionName);
                chunkBudget = ParsePositiveIntOption(optionName, optionValue);
                break;
            default:
                throw new ArgumentException($"Unsupported option '{optionName}'.");
        }
    }

    return new ProbeCliOptions(
        checkpointFilePath,
        checkpointAtInstructions,
        checkpointForceRebuild,
        chunkBudget,
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

        if (name.Equals("pc", StringComparison.OrdinalIgnoreCase))
        {
            powerPc.Registers.Pc = value;
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

static long RunBudgetWithTrace(
    EmulationMachine machine,
    long budget,
    int chunkBudget,
    IDictionary<uint, long> hotSpotCounters)
{
    var executed = 0L;
    var remaining = Math.Max(0, budget);

    while (remaining > 0 && !machine.Halted)
    {
        var currentChunk = (int)Math.Min(remaining, chunkBudget);
        var traceSummary = machine.RunWithTrace(currentChunk, maxHotSpots: int.MaxValue);
        var chunkExecuted = traceSummary.Summary.ExecutedInstructions;

        executed += chunkExecuted;
        remaining -= chunkExecuted;
        MergeHotSpots(hotSpotCounters, traceSummary.HotSpots);

        if (chunkExecuted == 0)
        {
            break;
        }
    }

    return executed;
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
    };

    Console.WriteLine("FirmwareGlobals:");

    foreach (var global in globals)
    {
        Console.WriteLine($"  {global.Name}=0x{machine.ReadUInt32(global.Address):X8}");
    }
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

file sealed record ProbeCliOptions(
    string? CheckpointFilePath,
    long? CheckpointAtInstructions,
    bool CheckpointForceRebuild,
    int ChunkBudget,
    string[] RegisterOverrideTokens);

file sealed record ProbeCheckpoint(
    long ExecutedInstructionsFromBoot,
    int MemoryMb,
    PowerPc32CpuSnapshot CpuSnapshot,
    IReadOnlyList<SparseMemoryPageSnapshot> MemoryPages);
