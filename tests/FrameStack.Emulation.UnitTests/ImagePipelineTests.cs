using FrameStack.Emulation.Images;
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

    private static byte[] CreateSparcTaggedPowerPcElf()
    {
        var image = new byte[0x90];

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

        return image;
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
