using System.Text;
using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Images;
using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;
using FrameStack.Emulation.Runtime;

namespace FrameStack.Emulation.UnitTests;

public sealed class ImagePipelineTests
{
    [Fact]
    public void AnalyzeRawBinaryShouldDefaultToMips32BigEndian()
    {
        var analyzer = new BinaryImageAnalyzer();
        var image = new byte[] { 0x20, 0x08, 0x00, 0x05, 0x00, 0x00, 0x00, 0x0D };

        var inspection = analyzer.Analyze(image);

        Assert.Equal(ImageContainerFormat.RawBinary, inspection.Format);
        Assert.Equal(ImageArchitecture.Mips32, inspection.Architecture);
        Assert.Equal(ImageEndianness.BigEndian, inspection.Endianness);
    }

    [Fact]
    public void AnalyzeElf32ShouldExtractEntryPointAndSegments()
    {
        var analyzer = new BinaryImageAnalyzer();
        var elfImage = CreateMinimalBigEndianMipsElf();

        var inspection = analyzer.Analyze(elfImage);

        Assert.Equal(ImageContainerFormat.Elf32, inspection.Format);
        Assert.Equal(ImageArchitecture.Mips32, inspection.Architecture);
        Assert.Equal(ImageEndianness.BigEndian, inspection.Endianness);
        Assert.Equal(0x80001000u, inspection.EntryPoint);
        Assert.Single(inspection.Sections);
        Assert.Equal(0x80001000u, inspection.Sections[0].VirtualAddress);
    }

    [Fact]
    public void BootstrapShouldCreateRunnableMachineForMinimalElf()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-test",
            imageBytes: CreateMinimalBigEndianMipsElf(),
            memoryMb: 64);

        var summary = state.Machine.Run(instructionBudget: 10);

        Assert.True(summary.Halted);
        Assert.Equal(2, summary.ExecutedInstructions);
        Assert.Equal(ImageContainerFormat.Elf32, state.BootstrapReport.Format);
    }

    [Fact]
    public void AnalyzeSparcTaggedElfWithPowerPcEntryShouldDetectPowerPc32()
    {
        var analyzer = new BinaryImageAnalyzer();
        var elfImage = CreateSparcTaggedPowerPcElf();

        var inspection = analyzer.Analyze(elfImage);

        Assert.Equal(ImageContainerFormat.Elf32, inspection.Format);
        Assert.Equal(ImageArchitecture.PowerPc32, inspection.Architecture);
        Assert.Contains("PowerPC32", inspection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeElf32WithCiscoTagsShouldExtractCiscoMetadata()
    {
        var analyzer = new BinaryImageAnalyzer();
        var elfImage = CreateSparcTaggedPowerPcElf(
            ciscoFamily: "C2600",
            ciscoImageTag: "C2600-ADVENTERPRISEK9-MZ");

        var inspection = analyzer.Analyze(elfImage);

        Assert.Equal("C2600", inspection.CiscoFamily);
        Assert.Equal("C2600-ADVENTERPRISEK9-MZ", inspection.CiscoImageTag);
        Assert.Contains("Cisco family tag: C2600.", inspection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BootstrapShouldExecutePowerPcPrologueSnippet()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-test",
            imageBytes: CreateSparcTaggedPowerPcElf(),
            memoryMb: 64);

        var summary = state.Machine.Run(instructionBudget: 8);

        Assert.False(summary.Halted);
        Assert.Equal(8, summary.ExecutedInstructions);
        Assert.Equal(ImageArchitecture.PowerPc32, state.BootstrapReport.Architecture);
    }

    [Fact]
    public void BootstrapShouldInitializePowerPcStackPointerFromAllocatedMemory()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-stack-init",
            imageBytes: CreateSparcTaggedPowerPcElf(),
            memoryMb: 192);

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(0x0BFFF000u, powerPc.Registers[1]);
    }

    [Fact]
    public void BootstrapShouldInitializeCiscoC2600PowerPcBootRegisters()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-c2600-boot-context",
            imageBytes: CreateSparcTaggedPowerPcElf(ciscoFamily: "C2600"),
            memoryMb: 128);

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(0x80006000u, powerPc.Registers[1]);
        Assert.Equal(1u, powerPc.Registers[3]);
        Assert.Equal(0x8000BD00u, powerPc.Registers[4]);
        Assert.Equal(0u, powerPc.Registers.Lr);
        Assert.False(powerPc.NullProgramCounterRedirectEnabled);
    }

    [Fact]
    public void BootstrapShouldInstallCiscoC2600PortAdapterMappedIoDevice()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-c2600-mmio-device",
            imageBytes: CreateSparcTaggedPowerPcElf(ciscoFamily: "C2600"),
            memoryMb: 128);

        var mappedBus = Assert.IsType<MemoryMappedBus>(state.Machine.MemoryBus);

        // Assert select and perform two read clocks.
        mappedBus.WriteByte(0x6740_001C, 0x10);
        mappedBus.WriteByte(0x6740_001C, 0x14);
        var firstBitSample = mappedBus.ReadByte(0x6740_001C);
        mappedBus.WriteByte(0x6740_001C, 0x10);
        mappedBus.WriteByte(0x6740_001C, 0x14);
        var secondBitSample = mappedBus.ReadByte(0x6740_001C);
        mappedBus.WriteByte(0x6740_001C, 0x10);

        Assert.Equal((byte)0x80, mappedBus.ReadByte(0x6740_0014));
        Assert.True((firstBitSample & 0x80) != 0);
        Assert.True((secondBitSample & 0x80) == 0);
    }

    [Fact]
    public void BootstrapShouldLeaveNonC2600PowerPcBootRegistersUntouched()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-nonc2600-boot-context",
            imageBytes: CreateSparcTaggedPowerPcElf(ciscoFamily: "C2800"),
            memoryMb: 128);

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(0x07FFF000u, powerPc.Registers[1]);
        Assert.Equal(0u, powerPc.Registers[3]);
        Assert.Equal(0u, powerPc.Registers[4]);
        Assert.Equal(0u, powerPc.Registers.Lr);
        Assert.False(powerPc.NullProgramCounterRedirectEnabled);
    }

    [Fact]
    public void BootstrapShouldNotSeedLinkRegisterForNonCiscoPowerPcImages()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-no-cisco-boot-context",
            imageBytes: CreateSparcTaggedPowerPcElf(),
            memoryMb: 128);

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(0u, powerPc.Registers.Lr);
        Assert.True(powerPc.NullProgramCounterRedirectEnabled);
        Assert.Equal(0u, state.Machine.ReadUInt32(0x00000000));
    }

    [Fact]
    public void BootstrapShouldApplyCpuInitializerAfterReset()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-init-test",
            imageBytes: CreateSparcTaggedPowerPcElf(),
            memoryMb: 64,
            cpuInitializer: cpu =>
            {
                var powerPc = Assert.IsType<PowerPc32CpuCore>(cpu);
                powerPc.Registers.Pc = 0x80008004;
            });

        Assert.Equal(0x80008004u, state.Machine.ProgramCounter);
    }

    [Fact]
    public void BootstrapShouldAllowCpuInitializerToOverridePowerPcStackPointer()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-stack-override",
            imageBytes: CreateSparcTaggedPowerPcElf(),
            memoryMb: 192,
            cpuInitializer: cpu =>
            {
                var powerPc = Assert.IsType<PowerPc32CpuCore>(cpu);
                powerPc.Registers[1] = 0x12345000;
            });

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(0x12345000u, powerPc.Registers[1]);
    }

    [Fact]
    public void BootstrapShouldInstallCiscoLowVectorEntryStubAndRunFromNullVector()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-low-vector-stub-cisco",
            imageBytes: CreateSparcTaggedPowerPcElf(ciscoFamily: "C2600"),
            memoryMb: 64);

        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);
        powerPc.Registers.Pc = 0;

        Assert.False(powerPc.NullProgramCounterRedirectEnabled);
        Assert.Equal(0x3C008000u, state.Machine.ReadUInt32(0x00000000));
        Assert.Equal(0x60008000u, state.Machine.ReadUInt32(0x00000004));
        Assert.Equal(0x7C0803A6u, state.Machine.ReadUInt32(0x00000008));
        Assert.Equal(0x4E800020u, state.Machine.ReadUInt32(0x0000000C));

        var summary = state.Machine.Run(instructionBudget: 4);

        Assert.Equal(4, summary.ExecutedInstructions);
        Assert.False(summary.Halted);
        Assert.Equal(0x80008000u, summary.FinalProgramCounter);
        Assert.Equal(0x80008000u, powerPc.ProgramCounter);
    }

    [Fact]
    public void BootstrapShouldClampPowerPcSupervisorMemoryForCiscoC2600Profile()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-ram-c2600",
            imageBytes: CreateSparcTaggedPowerPcSupervisorProbeElf("C2600"),
            memoryMb: 256);

        var summary = state.Machine.Run(instructionBudget: 2);
        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(2, summary.ExecutedInstructions);
        Assert.Equal(128u * 1024u * 1024u, powerPc.Registers[3]);
    }

    [Fact]
    public void BootstrapShouldReportAllocatedPowerPcSupervisorMemoryForCiscoImagesAtLowerBound()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-ram-c2600-lower-bound",
            imageBytes: CreateSparcTaggedPowerPcSupervisorProbeElf("C2600"),
            memoryMb: 64);

        var summary = state.Machine.Run(instructionBudget: 2);
        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(2, summary.ExecutedInstructions);
        Assert.Equal(64u * 1024u * 1024u, powerPc.Registers[3]);
    }

    [Fact]
    public void BootstrapShouldProtectCiscoC2600IoMemoryDescriptorWrites()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-c2600-iomem-protect",
            imageBytes: CreateSparcTaggedPowerPcElf(ciscoFamily: "C2600"),
            memoryMb: 128);

        var memoryBus = ResolveSparseMemoryBus(state.Machine.MemoryBus);

        using (memoryBus.BeginPrivilegedWriteScope())
        {
            memoryBus.WriteUInt32(0x8336_67E0, 0x1122_3344);
        }

        memoryBus.WriteUInt32(0x8336_67E0, 0xAABB_CCDD);

        Assert.Equal(0x1122_3344u, memoryBus.ReadUInt32(0x8336_67E0));
    }

    [Fact]
    public void BootstrapShouldFallbackToAllocatedMemoryWhenCiscoFamilyHasNoProfile()
    {
        var bootstrapper = new RuntimeImageBootstrapper(
            new BinaryImageAnalyzer(),
            [new Elf32ImageLoader(), new RawBinaryImageLoader()]);

        var state = bootstrapper.Bootstrap(
            runtimeHandle: "native-ppc-ram-fallback",
            imageBytes: CreateSparcTaggedPowerPcSupervisorProbeElf("C2800"),
            memoryMb: 192);

        var summary = state.Machine.Run(instructionBudget: 2);
        var powerPc = Assert.IsType<PowerPc32CpuCore>(state.CpuCore);

        Assert.Equal(2, summary.ExecutedInstructions);
        Assert.Equal(192u * 1024u * 1024u, powerPc.Registers[3]);
    }

    private static byte[] CreateMinimalBigEndianMipsElf()
    {
        var image = new byte[0x88];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 0x01; // ELFCLASS32
        image[5] = 0x02; // ELFDATA2MSB
        image[6] = 0x01; // EV_CURRENT

        WriteUInt16BigEndian(image, 16, 0x0002); // ET_EXEC
        WriteUInt16BigEndian(image, 18, 0x0008); // EM_MIPS
        WriteUInt32BigEndian(image, 20, 0x00000001); // EV_CURRENT
        WriteUInt32BigEndian(image, 24, 0x80001000); // entry
        WriteUInt32BigEndian(image, 28, 0x00000034); // phoff
        WriteUInt16BigEndian(image, 40, 0x0034); // ehsize
        WriteUInt16BigEndian(image, 42, 0x0020); // phentsize
        WriteUInt16BigEndian(image, 44, 0x0001); // phnum

        const int ph = 0x34;
        WriteUInt32BigEndian(image, ph + 0, 0x00000001); // PT_LOAD
        WriteUInt32BigEndian(image, ph + 4, 0x00000080); // p_offset
        WriteUInt32BigEndian(image, ph + 8, 0x80001000); // p_vaddr
        WriteUInt32BigEndian(image, ph + 12, 0x80001000); // p_paddr
        WriteUInt32BigEndian(image, ph + 16, 0x00000008); // p_filesz
        WriteUInt32BigEndian(image, ph + 20, 0x00000008); // p_memsz
        WriteUInt32BigEndian(image, ph + 24, 0x00000005); // PF_R | PF_X
        WriteUInt32BigEndian(image, ph + 28, 0x00001000); // p_align

        image[0x80] = 0x20;
        image[0x81] = 0x08;
        image[0x82] = 0x00;
        image[0x83] = 0x05; // addi t0, zero, 5
        image[0x84] = 0x00;
        image[0x85] = 0x00;
        image[0x86] = 0x00;
        image[0x87] = 0x0D; // break

        return image;
    }

    private static SparseMemoryBus ResolveSparseMemoryBus(IMemoryBus memoryBus)
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
            $"Expected SparseMemoryBus-backed runtime memory, got '{memoryBus.GetType().Name}'.");
    }

    private static byte[] CreateSparcTaggedPowerPcElf(
        string? ciscoFamily = null,
        string? ciscoImageTag = null)
    {
        var trailer = BuildCiscoTagTrailer(ciscoFamily, ciscoImageTag);
        var image = new byte[0x90 + trailer.Length];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 0x01; // ELFCLASS32
        image[5] = 0x02; // ELFDATA2MSB
        image[6] = 0x01; // EV_CURRENT

        WriteUInt16BigEndian(image, 16, 0x0002); // ET_EXEC
        WriteUInt16BigEndian(image, 18, 0x002B); // EM_SPARCV9
        WriteUInt32BigEndian(image, 20, 0x00000001); // EV_CURRENT
        WriteUInt32BigEndian(image, 24, 0x80008000); // entry
        WriteUInt32BigEndian(image, 28, 0x00000034); // phoff
        WriteUInt16BigEndian(image, 40, 0x0034); // ehsize
        WriteUInt16BigEndian(image, 42, 0x0020); // phentsize
        WriteUInt16BigEndian(image, 44, 0x0001); // phnum

        const int ph = 0x34;
        WriteUInt32BigEndian(image, ph + 0, 0x00000001); // PT_LOAD
        WriteUInt32BigEndian(image, ph + 4, 0x00000080); // p_offset
        WriteUInt32BigEndian(image, ph + 8, 0x80008000); // p_vaddr
        WriteUInt32BigEndian(image, ph + 12, 0x80008000); // p_paddr
        WriteUInt32BigEndian(image, ph + 16, 0x00000010); // p_filesz
        WriteUInt32BigEndian(image, ph + 20, 0x00000010); // p_memsz
        WriteUInt32BigEndian(image, ph + 24, 0x00000007); // PF_R | PF_W | PF_X
        WriteUInt32BigEndian(image, ph + 28, 0x00000100); // p_align

        WriteUInt32BigEndian(image, 0x80, 0x9421FFF8); // stwu r1, -8(r1)
        WriteUInt32BigEndian(image, 0x84, 0x7C0802A6); // mflr r0
        WriteUInt32BigEndian(image, 0x88, 0x9001000C); // stw r0, 12(r1)
        WriteUInt32BigEndian(image, 0x8C, 0x48000000); // b .

        if (trailer.Length > 0)
        {
            Buffer.BlockCopy(trailer, 0, image, 0x90, trailer.Length);
        }

        return image;
    }

    private static byte[] CreateSparcTaggedPowerPcSupervisorProbeElf(string ciscoFamily)
    {
        var trailer = BuildCiscoTagTrailer(ciscoFamily, ciscoImageTag: null);
        var image = new byte[0x90 + trailer.Length];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 0x01; // ELFCLASS32
        image[5] = 0x02; // ELFDATA2MSB
        image[6] = 0x01; // EV_CURRENT

        WriteUInt16BigEndian(image, 16, 0x0002); // ET_EXEC
        WriteUInt16BigEndian(image, 18, 0x0014); // EM_PPC
        WriteUInt32BigEndian(image, 20, 0x00000001); // EV_CURRENT
        WriteUInt32BigEndian(image, 24, 0x80008000); // entry
        WriteUInt32BigEndian(image, 28, 0x00000034); // phoff
        WriteUInt16BigEndian(image, 40, 0x0034); // ehsize
        WriteUInt16BigEndian(image, 42, 0x0020); // phentsize
        WriteUInt16BigEndian(image, 44, 0x0001); // phnum

        const int ph = 0x34;
        WriteUInt32BigEndian(image, ph + 0, 0x00000001); // PT_LOAD
        WriteUInt32BigEndian(image, ph + 4, 0x00000080); // p_offset
        WriteUInt32BigEndian(image, ph + 8, 0x80008000); // p_vaddr
        WriteUInt32BigEndian(image, ph + 12, 0x80008000); // p_paddr
        WriteUInt32BigEndian(image, ph + 16, 0x00000010); // p_filesz
        WriteUInt32BigEndian(image, ph + 20, 0x00000010); // p_memsz
        WriteUInt32BigEndian(image, ph + 24, 0x00000007); // PF_R | PF_W | PF_X
        WriteUInt32BigEndian(image, ph + 28, 0x00000100); // p_align

        WriteUInt32BigEndian(image, 0x80, 0x38600004); // li r3, 4
        WriteUInt32BigEndian(image, 0x84, 0x44000002); // sc
        WriteUInt32BigEndian(image, 0x88, 0x48000000); // b .
        WriteUInt32BigEndian(image, 0x8C, 0x60000000); // nop

        if (trailer.Length > 0)
        {
            Buffer.BlockCopy(trailer, 0, image, 0x90, trailer.Length);
        }

        return image;
    }

    private static byte[] BuildCiscoTagTrailer(string? ciscoFamily, string? ciscoImageTag)
    {
        if (string.IsNullOrWhiteSpace(ciscoFamily) && string.IsNullOrWhiteSpace(ciscoImageTag))
        {
            return [];
        }

        var trailer = new List<byte>();
        AppendCiscoTag(trailer, "CW_FAMILY", ciscoFamily);
        AppendCiscoTag(trailer, "CW_IMAGE", ciscoImageTag);
        return trailer.ToArray();
    }

    private static void AppendCiscoTag(List<byte> target, string tagName, string? tagValue)
    {
        if (string.IsNullOrWhiteSpace(tagValue))
        {
            return;
        }

        target.AddRange(Encoding.ASCII.GetBytes($"{tagName}${tagValue}$"));
        target.Add(0);
    }

    private static void WriteUInt16BigEndian(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)value;
    }

    private static void WriteUInt32BigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
