using FrameStack.Emulation.Core;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.Mips32;
using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.Runtime;

public sealed class RuntimeImageBootstrapper
{
    private const uint OneMbInBytes = 1024u * 1024u;
    private const int CiscoC2600MaxReportedMemoryMb = 128;
    private const uint CiscoC2600BootMode = 2;
    private const uint CiscoC2600HardwarePlatformId = 0x2B;
    private const uint CiscoC2600BootInfoPointer = 0x8000_BD00;
    private const uint CiscoC2600InitialStackPointer = 0x8000_6000;
    private const uint CiscoC2600IoMemoryDescriptorAddress = 0x8336_67E0;
    private const uint CiscoC2600IoMemoryDescriptorSizeBytes = 0x10;
    private const int Mpc8xxInternalMemoryMapRegisterSpr = 638;
    private const uint CiscoC2600InternalMemoryMapRegister = 0xFFE0_0000;

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
        Action<FrameStack.Emulation.Abstractions.ICpuCore>? cpuInitializer = null,
        Action<byte>? consoleTransmitSink = null)
    {
        if (memoryMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMb), "Memory must be greater than zero.");
        }

        var inspection = _imageAnalyzer.Analyze(imageBytes);
        var loader = ResolveLoader(inspection);
        var loadedImage = loader.Load(imageBytes, inspection, ImageLoadOptions.Default);

        var ramBus = new SparseMemoryBus((ulong)memoryMb * 1024UL * 1024UL);

        foreach (var segment in loadedImage.Segments)
        {
            ramBus.LoadBytes(segment.VirtualAddress, segment.Data);
        }

        ApplyCiscoMemoryWriteProtection(ramBus, inspection);
        var machineMemoryBus = BuildMachineMemoryBus(
            ramBus,
            inspection,
            consoleTransmitSink,
            out var ciscoC2600ConsoleUartDevice);

        var lowVectorEntryStubInstalled =
            CiscoPowerPcLowVectorBootstrap.TryInstallEntryStub(machineMemoryBus, inspection, loadedImage.EntryPoint);
        var cpuCore = CreateCpuCore(loadedImage, inspection, memoryMb);
        var machine = new EmulationMachine(cpuCore, machineMemoryBus, loadedImage.EntryPoint);

        ApplyDefaultCpuInitialization(
            cpuCore,
            loadedImage,
            inspection,
            memoryMb,
            lowVectorEntryStubInstalled);
        cpuInitializer?.Invoke(cpuCore);

        var report = new RuntimeBootstrapReport(
            loadedImage.Format,
            loadedImage.Architecture,
            loadedImage.Endianness,
            loadedImage.EntryPoint,
            loadedImage.Segments.Count,
            inspection.Summary);

        return new RuntimeSessionState(
            runtimeHandle,
            machine,
            report,
            cpuCore,
            ciscoC2600ConsoleUartDevice);
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
            var reportedMemoryBytes = ResolvePowerPcReportedMemoryBytes(memoryMb, inspection);
            var nullProgramCounterRedirectPolicy =
                ResolvePowerPcNullProgramCounterRedirectPolicy(loadedImage, inspection);

            return new PowerPc32CpuCore(
                new DefaultPowerPcSupervisorCallHandler(
                    reportedMemoryBytes: reportedMemoryBytes,
                    hardwarePlatformId: ResolvePowerPcHardwarePlatformId(inspection)),
                nullProgramCounterRedirectPolicy);
        }

        throw new NotSupportedException(
            $"Unsupported architecture/endian pair: {loadedImage.Architecture}/{loadedImage.Endianness}.");
    }

    private static uint ResolvePowerPcReportedMemoryBytes(
        int memoryMb,
        ImageInspectionResult inspection)
    {
        var effectiveMemoryMb = memoryMb;

        if (string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase) &&
            effectiveMemoryMb > CiscoC2600MaxReportedMemoryMb)
        {
            // C2600 ROM TLB bootstrap expects at most 128MB and falls into a
            // non-recoverable guard loop when probing larger RAM profiles.
            effectiveMemoryMb = CiscoC2600MaxReportedMemoryMb;
        }

        return checked((uint)effectiveMemoryMb * OneMbInBytes);
    }

    private static CiscoPowerPcNullProgramCounterRedirectPolicy? ResolvePowerPcNullProgramCounterRedirectPolicy(
        LoadedImage loadedImage,
        ImageInspectionResult inspection)
    {
        return string.IsNullOrWhiteSpace(inspection.CiscoFamily)
            ? null
            : new CiscoPowerPcNullProgramCounterRedirectPolicy(loadedImage.EntryPoint);
    }

    private static uint ResolvePowerPcHardwarePlatformId(ImageInspectionResult inspection)
    {
        if (string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            return CiscoC2600HardwarePlatformId;
        }

        return 0;
    }

    private static void ApplyDefaultCpuInitialization(
        FrameStack.Emulation.Abstractions.ICpuCore cpuCore,
        LoadedImage loadedImage,
        ImageInspectionResult inspection,
        int memoryMb,
        bool lowVectorEntryStubInstalled)
    {
        if (loadedImage.Architecture != ImageArchitecture.PowerPc32 ||
            loadedImage.Endianness != ImageEndianness.BigEndian ||
            cpuCore is not PowerPc32CpuCore powerPcCore)
        {
            return;
        }

        powerPcCore.Registers[1] = ResolvePowerPcInitialStackPointer(memoryMb, inspection);
        ApplyCiscoPowerPcBootContext(powerPcCore, loadedImage, inspection, lowVectorEntryStubInstalled);

        if (lowVectorEntryStubInstalled)
        {
            powerPcCore.NullProgramCounterRedirectEnabled = false;
        }
    }

    private static uint ResolvePowerPcInitialStackPointer(
        int memoryMb,
        ImageInspectionResult inspection)
    {
        if (string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            return CiscoC2600InitialStackPointer;
        }

        const uint stackGuardBytes = 0x1000;
        var topOfRam = ResolvePowerPcReportedMemoryBytes(memoryMb, inspection);

        return topOfRam > stackGuardBytes
            ? topOfRam - stackGuardBytes
            : stackGuardBytes;
    }

    private static void ApplyCiscoPowerPcBootContext(
        PowerPc32CpuCore powerPcCore,
        LoadedImage loadedImage,
        ImageInspectionResult inspection,
        bool lowVectorEntryStubInstalled)
    {
        if (!string.IsNullOrWhiteSpace(inspection.CiscoFamily))
        {
            // When low-vector entry stub is present, IOS bootstrap returns through
            // LR and expects 0x00000000 -> low-vector trampoline, not entrypoint self-recursion.
            powerPcCore.Registers.Lr = lowVectorEntryStubInstalled
                ? 0u
                : loadedImage.EntryPoint;
        }

        if (!string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        powerPcCore.DefaultMpc8xxInternalMemoryMapRegister = CiscoC2600InternalMemoryMapRegister;
        powerPcCore.WriteSpecialPurposeRegister(
            Mpc8xxInternalMemoryMapRegisterSpr,
            CiscoC2600InternalMemoryMapRegister);
        powerPcCore.Registers[3] = CiscoC2600BootMode;
        powerPcCore.Registers[4] = CiscoC2600BootInfoPointer;
    }

    private static void ApplyCiscoMemoryWriteProtection(
        SparseMemoryBus memoryBus,
        ImageInspectionResult inspection)
    {
        if (!string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Do not protect C2600 NVRAM sizing words during bootstrap.
        // They live inside the ROM's decompression/checksum target region and
        // early write-protection corrupts the unpacked payload.
        memoryBus.ProtectWriteRange(
            CiscoC2600IoMemoryDescriptorAddress,
            CiscoC2600IoMemoryDescriptorSizeBytes);
    }

    private static IMemoryBus BuildMachineMemoryBus(
        SparseMemoryBus ramBus,
        ImageInspectionResult inspection,
        Action<byte>? consoleTransmitSink,
        out CiscoC2600ConsoleUartIoDevice? ciscoC2600ConsoleUartDevice)
    {
        if (!string.Equals(inspection.CiscoFamily, "C2600", StringComparison.OrdinalIgnoreCase))
        {
            ciscoC2600ConsoleUartDevice = null;
            return ramBus;
        }

        var mappedBus = new MemoryMappedBus(ramBus);
        mappedBus.RegisterDevice(new CiscoC2600Amdp2IoDevice());
        mappedBus.RegisterDevice(new CiscoC2600PortAdapterIoDevice());
        ciscoC2600ConsoleUartDevice = new CiscoC2600ConsoleUartIoDevice(
            transmitByteSink: consoleTransmitSink);
        mappedBus.RegisterDevice(ciscoC2600ConsoleUartDevice);
        return mappedBus;
    }
}
