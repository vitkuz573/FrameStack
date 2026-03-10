using System.Globalization;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run --project tools/FrameStack.ImageProbe -- <image-path> [instruction-budget] [memory-mb] [timeline-steps]");
    return 1;
}

var imagePath = Path.GetFullPath(args[0]);
var instructionBudget = ParseInstructionBudget(args, 1, 2048);
var memoryMb = ParseOrDefault(args, 2, 256);
var timelineSteps = ParseOrDefault(args, 3, 0);
var registerOverrides = ParseRegisterOverrides(args.Skip(4).ToArray());

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
        memoryMb,
        cpuInitializer: cpuCore => ApplyRegisterOverrides(cpuCore, registerOverrides));
    PowerPcTracingSupervisorCallHandler? supervisorTracer = null;
    var powerPcCore = state.CpuCore as PowerPc32CpuCore;

    if (powerPcCore is not null)
    {
        supervisorTracer = new PowerPcTracingSupervisorCallHandler(powerPcCore.SupervisorCallHandler);
        powerPcCore.SupervisorCallHandler = supervisorTracer;
    }

    if (timelineSteps > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Timeline (first {timelineSteps} step(s)):");

        for (var index = 0; index < timelineSteps; index++)
        {
            var pc = state.Machine.ProgramCounter;
            var mapped = TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, pc, out var instructionWord);
            var opcode = mapped ? (instructionWord >> 26).ToString("X2", CultureInfo.InvariantCulture) : "??";
            var instructionDisplay = mapped
                ? $"0x{instructionWord:X8}"
                : "<unmapped>";

            Console.WriteLine($"  #{index + 1:D4} PC=0x{pc:X8} INSN={instructionDisplay} OPCODE=0x{opcode}");

            if (powerPcCore is not null)
            {
                Console.WriteLine(
                    $"       R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
                    $"R10=0x{powerPcCore.Registers[10]:X8} R27=0x{powerPcCore.Registers[27]:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8}");
            }

            var stepSummary = state.Machine.Run(1);

            if (stepSummary.Halted)
            {
                Console.WriteLine($"  Halted after step {index + 1} at PC=0x{stepSummary.FinalProgramCounter:X8}");
                break;
            }
        }
    }

    var remainingBudget = Math.Max(1, instructionBudget - timelineSteps);
    var traceSummary = state.Machine.RunWithTrace(remainingBudget, maxHotSpots: 12);
    var runSummary = traceSummary.Summary;

    Console.WriteLine();
    Console.WriteLine("Preflight Run:");
    Console.WriteLine($"ExecutedInstructions: {runSummary.ExecutedInstructions}");
    Console.WriteLine($"Halted: {runSummary.Halted}");
    Console.WriteLine($"FinalProgramCounter: 0x{runSummary.FinalProgramCounter:X8}");
    if (powerPcCore is not null)
    {
        Console.WriteLine(
            $"FinalRegisters: R3=0x{powerPcCore.Registers[3]:X8} R4=0x{powerPcCore.Registers[4]:X8} R5=0x{powerPcCore.Registers[5]:X8} " +
            $"R8=0x{powerPcCore.Registers[8]:X8} R9=0x{powerPcCore.Registers[9]:X8} R10=0x{powerPcCore.Registers[10]:X8} " +
            $"R27=0x{powerPcCore.Registers[27]:X8} R29=0x{powerPcCore.Registers[29]:X8} R30=0x{powerPcCore.Registers[30]:X8} R31=0x{powerPcCore.Registers[31]:X8}");
    }
    PrintFirmwareGlobals(state.Machine);
    Console.WriteLine("HotSpots:");

    foreach (var hotSpot in traceSummary.HotSpots)
    {
        if (TryReadWordAtVirtualAddress(inspection.Sections, imageBytes, hotSpot.ProgramCounter, out var instructionWord))
        {
            var majorOpcode = instructionWord >> 26;
            Console.WriteLine(
                $"  PC=0x{hotSpot.ProgramCounter:X8} Hits={hotSpot.Hits} INSN=0x{instructionWord:X8} OPCODE=0x{majorOpcode:X2}");
        }
        else
        {
            Console.WriteLine($"  PC=0x{hotSpot.ProgramCounter:X8} Hits={hotSpot.Hits} INSN=<unmapped>");
        }
    }

    if (state.CpuCore is PowerPc32CpuCore powerPc &&
        powerPc.SupervisorCallCounters.Count > 0)
    {
        Console.WriteLine("SupervisorCalls:");

        foreach (var (serviceCode, hits) in powerPc.SupervisorCallCounters
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

static int ParseInstructionBudget(string[] input, int index, int fallback)
{
    if (input.Length <= index)
    {
        return fallback;
    }

    var token = input[index];

    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    {
        return parsed > 0
            ? parsed
            : fallback;
    }

    if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
    {
        return fallback;
    }

    if (parsedLong <= 0)
    {
        return fallback;
    }

    if (parsedLong > int.MaxValue)
    {
        Console.WriteLine(
            $"Instruction budget {parsedLong} exceeds {int.MaxValue}; clamping to {int.MaxValue}.");
        return int.MaxValue;
    }

    return (int)parsedLong;
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

    if (cpuCore is not FrameStack.Emulation.PowerPc32.PowerPc32CpuCore powerPc)
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

static void PrintFirmwareGlobals(FrameStack.Emulation.Core.EmulationMachine machine)
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
