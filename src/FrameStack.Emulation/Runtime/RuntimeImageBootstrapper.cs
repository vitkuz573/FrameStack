using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.Mips32;
using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.Runtime;

public sealed class RuntimeImageBootstrapper
{
    private const uint CiscoC2600BootMode = 1;
    private const uint CiscoC2600BootInfoPointer = 0x8000_BD00;

    private readonly IImageAnalyzer _imageAnalyzer;
    private readonly IReadOnlyList<IImageLoader> _imageLoaders;

    public RuntimeImageBootstrapper(
        IImageAnalyzer imageAnalyzer,
        IEnumerable<IImageLoader> imageLoaders)
    {
        _imageAnalyzer = imageAnalyzer;
        _imageLoaders = imageLoaders.ToArray();
    }

    public RuntimeSessionState Bootstrap(
        string runtimeHandle,
        byte[] imageBytes,
        int memoryMb,
        Action<FrameStack.Emulation.Abstractions.ICpuCore>? cpuInitializer = null)
    {
        if (memoryMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMb), "Memory must be greater than zero.");
        }

        var inspection = _imageAnalyzer.Analyze(imageBytes);
        var loader = ResolveLoader(inspection);
        var loadedImage = loader.Load(imageBytes, inspection, ImageLoadOptions.Default);

        var memoryBus = new SparseMemoryBus((ulong)memoryMb * 1024UL * 1024UL);

        foreach (var segment in loadedImage.Segments)
        {
            memoryBus.LoadBytes(segment.VirtualAddress, segment.Data);
        }

        var cpuCore = CreateCpuCore(loadedImage, inspection, memoryMb);
        var machine = new EmulationMachine(cpuCore, memoryBus, loadedImage.EntryPoint);

        ApplyDefaultCpuInitialization(cpuCore, loadedImage, inspection, memoryMb);
        cpuInitializer?.Invoke(cpuCore);

        var report = new RuntimeBootstrapReport(
            loadedImage.Format,
            loadedImage.Architecture,
            loadedImage.Endianness,
            loadedImage.EntryPoint,
            loadedImage.Segments.Count,
            inspection.Summary);

        return new RuntimeSessionState(runtimeHandle, machine, report, cpuCore);
    }

    private IImageLoader ResolveLoader(ImageInspectionResult inspection)
    {
        var loader = _imageLoaders.FirstOrDefault(candidate => candidate.CanLoad(inspection));

        if (loader is not null)
        {
            return loader;
        }

        throw new NotSupportedException($"No loader registered for image format '{inspection.Format}'.");
    }

    private static FrameStack.Emulation.Abstractions.ICpuCore CreateCpuCore(
        LoadedImage loadedImage,
        ImageInspectionResult inspection,
        int memoryMb)
    {
        if (loadedImage.Architecture == ImageArchitecture.Mips32 &&
            loadedImage.Endianness == ImageEndianness.BigEndian)
        {
            return new Mips32CpuCore();
        }

        if (loadedImage.Architecture == ImageArchitecture.Mips32 &&
            loadedImage.Endianness == ImageEndianness.LittleEndian)
        {
            throw new NotSupportedException("Little-endian MIPS32 core is not implemented yet.");
        }

        if (loadedImage.Architecture == ImageArchitecture.PowerPc32 &&
            loadedImage.Endianness == ImageEndianness.BigEndian)
        {
            var reportedMemoryBytes = ResolvePowerPcReportedMemoryBytes(memoryMb);
            var nullProgramCounterRedirectPolicy =
                ResolvePowerPcNullProgramCounterRedirectPolicy(loadedImage, inspection);

            return new PowerPc32CpuCore(
                new DefaultPowerPcSupervisorCallHandler(reportedMemoryBytes),
                nullProgramCounterRedirectPolicy);
        }

        throw new NotSupportedException(
            $"Unsupported architecture/endian pair: {loadedImage.Architecture}/{loadedImage.Endianness}.");
    }

    private static uint ResolvePowerPcReportedMemoryBytes(int memoryMb)
    {
        const uint oneMb = 1024u * 1024u;
        return checked((uint)memoryMb * oneMb);
    }

    private static CiscoPowerPcNullProgramCounterRedirectPolicy? ResolvePowerPcNullProgramCounterRedirectPolicy(
        LoadedImage loadedImage,
        ImageInspectionResult inspection)
    {
        return string.IsNullOrWhiteSpace(inspection.CiscoFamily)
            ? null
            : new CiscoPowerPcNullProgramCounterRedirectPolicy(loadedImage.EntryPoint);
    }

    private static void ApplyDefaultCpuInitialization(
        FrameStack.Emulation.Abstractions.ICpuCore cpuCore,
        LoadedImage loadedImage,
        ImageInspectionResult inspection,
        int memoryMb)
    {
        if (loadedImage.Architecture != ImageArchitecture.PowerPc32 ||
            loadedImage.Endianness != ImageEndianness.BigEndian ||
            cpuCore is not PowerPc32CpuCore powerPcCore)
        {
            return;
        }

        powerPcCore.Registers[1] = ResolvePowerPcInitialStackPointer(memoryMb);
        ApplyCiscoPowerPcBootContext(powerPcCore, inspection);
    }

    private static uint ResolvePowerPcInitialStackPointer(int memoryMb)
    {
        const uint stackGuardBytes = 0x1000;
        var topOfRam = ResolvePowerPcReportedMemoryBytes(memoryMb);

        return topOfRam > stackGuardBytes
            ? topOfRam - stackGuardBytes
            : stackGuardBytes;
    }

    private static void ApplyCiscoPowerPcBootContext(
        PowerPc32CpuCore powerPcCore,
        ImageInspectionResult inspection)
    {
        if (!string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        powerPcCore.Registers[3] = CiscoC2600BootMode;
        powerPcCore.Registers[4] = CiscoC2600BootInfoPointer;
    }
}
