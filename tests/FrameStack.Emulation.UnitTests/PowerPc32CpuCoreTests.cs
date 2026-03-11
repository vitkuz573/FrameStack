using FrameStack.Emulation.Memory;
using FrameStack.Emulation.PowerPc32;

namespace FrameStack.Emulation.UnitTests;

public sealed class PowerPc32CpuCoreTests
{
    [Fact]
    public void SetHaltedShouldAllowResumingExecution()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x6063_0001); // ori r3, r3, 1

        cpu.Reset(0x1000);
        cpu.SetHalted(true);

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1000u, cpu.ProgramCounter);
        Assert.Equal(0u, cpu.Registers[3]);

        cpu.SetHalted(false);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1004u, cpu.ProgramCounter);
        Assert.Equal(1u, cpu.Registers[3]);
    }

    [Fact]
    public void BlrlShouldBranchToPreviousLrValueAndUpdateLinkRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x4E80_0021); // blrl

        cpu.Reset(0x1000);
        cpu.Registers.Lr = 0x1100;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1100u, cpu.ProgramCounter);
        Assert.Equal(0x1004u, cpu.Registers.Lr);
    }

    [Fact]
    public void BctrlShouldBranchToCtrAndUpdateLinkRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x4E80_0421); // bctrl

        cpu.Reset(0x1000);
        cpu.Registers.Ctr = 0x1200;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1200u, cpu.ProgramCounter);
        Assert.Equal(0x1004u, cpu.Registers.Lr);
    }

    [Fact]
    public void OriShouldReadRsAndWriteRa()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x6064_0001); // ori r4, r3, 1

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x10;
        cpu.Registers[4] = 0xDEAD_BEEF;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x11u, cpu.Registers[4]);
        Assert.Equal(0x10u, cpu.Registers[3]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void AddicDotShouldUpdateCr0AndCarryFlag()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x34A0_FFFF); // addic. r5, r0, -1

        cpu.Reset(0x1000);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFFu, cpu.Registers[5]);

        // CR0 should be LT (1000b) for negative signed result.
        var cr0 = (cpu.Registers.Cr >> 28) & 0xF;
        Assert.Equal(0b1000u, cr0);

        // CA should be clear for 0 + 0xFFFFFFFF.
        Assert.Equal(0u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void AddicShouldTreatR0AsRegularBaseRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x30A0_0001); // addic r5, r0, 1

        cpu.Reset(0x1000);
        cpu.Registers[0] = 0xFFFF_FFFF;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[5]);
        Assert.Equal(0x2000_0000u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void AddzeShouldUseCarryInAndUpdateCarryFlag()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x30E0_0001); // addic r7, r0, 1
        memory.WriteUInt32(0x1004, 0x7CA3_0194); // addze r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[0] = 0xFFFF_FFFF;
        cpu.Registers[3] = 0;

        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(1u, cpu.Registers[5]);
        Assert.Equal(0u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void AddmeShouldAddMinusOneAndCarryIn()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x30E0_0001); // addic r7, r0, 1
        memory.WriteUInt32(0x1004, 0x7CA3_01D4); // addme r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[0] = 0xFFFF_FFFF;
        cpu.Registers[3] = 0;

        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[5]);
        Assert.Equal(0x2000_0000u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void SubficShouldTreatR0AsRegularSourceRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x2120_0000); // subfic r9, r0, 0

        cpu.Reset(0x1000);
        cpu.Registers[0] = 5;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFBu, cpu.Registers[9]);
        Assert.Equal(0u, cpu.Registers.Xer & 0x2000_0000u);
    }

    [Fact]
    public void MulliShouldTreatR0AsRegularSourceRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x1CC0_0003); // mulli r6, r0, 3

        cpu.Reset(0x1000);
        cpu.Registers[0] = 4;

        cpu.ExecuteCycle(memory);

        Assert.Equal(12u, cpu.Registers[6]);
    }

    [Fact]
    public void MtmsrThenMfmsrShouldRoundTripMachineStateRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C60_0124); // mtmsr r3
        memory.WriteUInt32(0x1004, 0x7C80_00A6); // mfmsr r4

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x1234_5678;

        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1234_5678u, cpu.Registers[4]);
        Assert.Equal(0x1008u, cpu.ProgramCounter);
    }

    [Fact]
    public void MfsprMtwbShouldReturnLevelOneDescriptorPointerForCurrentEpn()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C7C_C3A6); // mtspr 796, r3 (M_TWB)
        memory.WriteUInt32(0x1004, 0x7C9B_C3A6); // mtspr 795, r4 (MD_EPN)
        memory.WriteUInt32(0x1008, 0x7CBC_C2A6); // mfspr r5, 796

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x0345_0000;
        cpu.Registers[4] = 0x8F00_0200;

        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0x0345_08F0u, cpu.Registers[5]);
    }

    [Fact]
    public void ReadSpecialPurposeRegisterMtwbShouldUseEffectivePageNumberIndex()
    {
        var cpu = new PowerPc32CpuCore();

        cpu.WriteSpecialPurposeRegister(796, 0x0345_0000); // M_TWB base
        cpu.WriteSpecialPurposeRegister(795, 0x8F00_0200); // MD_EPN
        cpu.WriteSpecialPurposeRegister(792, 0x1600_1F00); // MD_CTR with slot index 31

        var descriptorPointer = cpu.ReadSpecialPurposeRegister(796);

        Assert.Equal(0x0345_08F0u, descriptorPointer);
    }

    [Fact]
    public void ReadSpecialPurposeRegisterMtwbShouldUseDataEpnEvenWhenInstructionControlIsActive()
    {
        var cpu = new PowerPc32CpuCore();

        cpu.WriteSpecialPurposeRegister(796, 0x0345_0000); // M_TWB base
        cpu.WriteSpecialPurposeRegister(795, 0x8F00_0200); // MD_EPN
        cpu.WriteSpecialPurposeRegister(787, 0x8100_1200); // MI_EPN

        cpu.WriteSpecialPurposeRegister(792, 0x1600_0000); // MD_CTR
        var dataDescriptorPointer = cpu.ReadSpecialPurposeRegister(796);

        cpu.WriteSpecialPurposeRegister(784, 0x0200_0000); // MI_CTR
        var instructionDescriptorPointer = cpu.ReadSpecialPurposeRegister(796);

        Assert.Equal(0x0345_08F0u, dataDescriptorPointer);
        Assert.Equal(0x0345_08F0u, instructionDescriptorPointer);
    }

    [Fact]
    public void MfsprMdTwcShouldReturnLevelTwoDescriptorPointerForCurrentEpn()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C9B_C3A6); // mtspr 795, r4 (MD_EPN)
        memory.WriteUInt32(0x1004, 0x7CFD_C3A6); // mtspr 797, r7 (MD_TWC)
        memory.WriteUInt32(0x1008, 0x7CDD_C2A6); // mfspr r6, 797

        cpu.Reset(0x1000);
        cpu.Registers[4] = 0x8F12_3000;
        cpu.Registers[7] = 0x0F40_0203;

        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(0x0F63_C400u, cpu.Registers[6]);
    }

    [Fact]
    public void ReadSpecialPurposeRegisterMdTwcShouldNotFallbackToMtwbBaseWhenLevelTwoBaseIsZero()
    {
        var cpu = new PowerPc32CpuCore();

        cpu.WriteSpecialPurposeRegister(796, 0x0345_0000); // M_TWB base
        cpu.WriteSpecialPurposeRegister(795, 0x8F12_3000); // MD_EPN
        cpu.WriteSpecialPurposeRegister(797, 0x0000_0000); // MD_TWC with zero level-two base

        var descriptorPointer = cpu.ReadSpecialPurposeRegister(797);

        Assert.Equal(0x0023_C400u, descriptorPointer);
    }

    [Fact]
    public void DcbfShouldAdvanceProgramCounterWithoutMutatingMemory()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C00_20AC); // dcbf 0, r4
        memory.WriteUInt32(0x1100, 0xAABB_CCDD);

        cpu.Reset(0x1000);
        cpu.Registers[4] = 0x1100;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1004u, cpu.ProgramCounter);
        Assert.Equal(0xAABB_CCDDu, memory.ReadUInt32(0x1100));
    }

    [Fact]
    public void DcbzShouldZeroAlignedCacheLine()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x4000);

        memory.WriteUInt32(0x1100, 0x7C00_1FEC); // dcbz 0, r3

        for (var address = 0x1000u; address < 0x1040u; address++)
        {
            memory.WriteByte(address, 0xAA);
        }

        cpu.Reset(0x1100);
        cpu.Registers[3] = 0x1013;

        cpu.ExecuteCycle(memory);

        for (var address = 0x1010u; address < 0x1020u; address++)
        {
            Assert.Equal(0u, memory.ReadByte(address));
        }

        for (var address = 0x1000u; address < 0x1010u; address++)
        {
            Assert.Equal(0xAAu, memory.ReadByte(address));
        }

        for (var address = 0x1020u; address < 0x1040u; address++)
        {
            Assert.Equal(0xAAu, memory.ReadByte(address));
        }

        Assert.Equal(0x1104u, cpu.ProgramCounter);
    }

    [Fact]
    public void MulhwuShouldWriteUpperWordOfUnsignedProduct()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C63_0016); // mulhwu r3, r3, r0

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0xFFFF_FFFF;
        cpu.Registers[0] = 2;

        cpu.ExecuteCycle(memory);

        Assert.Equal(1u, cpu.Registers[3]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void MulhwShouldWriteUpperWordOfSignedProduct()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C63_0096); // mulhw r3, r3, r0

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0xFFFF_FFFE; // -2
        cpu.Registers[0] = 3;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFFu, cpu.Registers[3]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void MullwShouldWriteLowerWordOfSignedProduct()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA3_21D6); // mullw r5, r3, r4

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0xFFFF_FFFD; // -3
        cpu.Registers[4] = 0x0000_0005; // 5

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFF1u, cpu.Registers[5]); // -15
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void MftbAndMftbuShouldReadMonotonicTimeBaseCounter()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C6C_42E6); // mftb r3, 0x10c (TBL)
        memory.WriteUInt32(0x1004, 0x7C8D_42E6); // mftbu r4 (TBU)

        cpu.Reset(0x1000);
        cpu.ExecuteCycle(memory);
        cpu.ExecuteCycle(memory);

        Assert.Equal(1u, cpu.Registers[3]);
        Assert.Equal(0u, cpu.Registers[4]);
        Assert.Equal(0x1008u, cpu.ProgramCounter);
    }

    [Fact]
    public void DivwuShouldDivideUnsignedWords()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA3_2396); // divwu r5, r3, r4

        cpu.Reset(0x1000);
        cpu.Registers[3] = 10;
        cpu.Registers[4] = 3;

        cpu.ExecuteCycle(memory);

        Assert.Equal(3u, cpu.Registers[5]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void DivwShouldDivideSignedWords()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA3_23D6); // divw r5, r3, r4

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0xFFFF_FFF7; // -9
        cpu.Registers[4] = 2;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xFFFF_FFFCu, cpu.Registers[5]); // -4
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void TlbieShouldInvalidateMatchingTlbEntry()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new SparseMemoryBus(maxMappedBytes: 64UL * 1024UL * 1024UL);

        memory.WriteUInt32(0x1000, 0x7D18_C3A6); // mtspr 792, r8 (MD_CTR, index field)
        memory.WriteUInt32(0x1004, 0x7C9B_C3A6); // mtspr 795, r4 (MD_EPN)
        memory.WriteUInt32(0x1008, 0x7CFD_C3A6); // mtspr 797, r7 (MD_TWC)
        memory.WriteUInt32(0x100C, 0x7CDE_C3A6); // mtspr 798, r6 (MD_RPN installs DTLB entry)
        memory.WriteUInt32(0x1010, 0x7C00_1A64); // tlbie r3
        memory.WriteUInt32(0x1014, 0x80A3_0000); // lwz r5, 0(r3)

        memory.WriteUInt32(0x0000_2000, 0xDEAD_BEEF);
        memory.WriteUInt32(0x8000_0000, 0xAABB_CCDD);

        cpu.Reset(0x1000);
        cpu.WriteMachineStateRegister(0x0000_0010);
        cpu.Registers[8] = 0; // Index 0
        cpu.Registers[4] = 0x8000_0200; // EPN + valid
        cpu.Registers[7] = 0x0000_0000;
        cpu.Registers[6] = 0x0000_2000; // RPN
        cpu.Registers[3] = 0x8000_0000; // Effective address mapped by entry above

        for (var step = 0; step < 6; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0xAABB_CCDDu, cpu.Registers[5]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void TlbiaShouldInvalidateAllTlbEntries()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new SparseMemoryBus(maxMappedBytes: 64UL * 1024UL * 1024UL);

        memory.WriteUInt32(0x1000, 0x7D18_C3A6); // mtspr 792, r8 (MD_CTR, index field)
        memory.WriteUInt32(0x1004, 0x7C9B_C3A6); // mtspr 795, r4 (MD_EPN)
        memory.WriteUInt32(0x1008, 0x7CFD_C3A6); // mtspr 797, r7 (MD_TWC)
        memory.WriteUInt32(0x100C, 0x7CDE_C3A6); // mtspr 798, r6 (MD_RPN installs DTLB entry)
        memory.WriteUInt32(0x1010, 0x7C00_02E4); // tlbia
        memory.WriteUInt32(0x1014, 0x80A3_0000); // lwz r5, 0(r3)

        memory.WriteUInt32(0x0000_2000, 0xDEAD_BEEF);
        memory.WriteUInt32(0x8000_0000, 0xAABB_CCDD);

        cpu.Reset(0x1000);
        cpu.WriteMachineStateRegister(0x0000_0010);
        cpu.Registers[8] = 0; // Index 0
        cpu.Registers[4] = 0x8000_0200; // EPN + valid
        cpu.Registers[7] = 0x0000_0000;
        cpu.Registers[6] = 0x0000_2000; // RPN
        cpu.Registers[3] = 0x8000_0000; // Effective address mapped by entry above

        for (var step = 0; step < 6; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0xAABB_CCDDu, cpu.Registers[5]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void DataLoadShouldUseInstalledMpc8xxTlbEntry()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new SparseMemoryBus(maxMappedBytes: 64UL * 1024UL * 1024UL);

        memory.WriteUInt32(0x1000, 0x7D18_C3A6); // mtspr 792, r8 (MD_CTR, index field)
        memory.WriteUInt32(0x1004, 0x7C9B_C3A6); // mtspr 795, r4 (MD_EPN)
        memory.WriteUInt32(0x1008, 0x7CFD_C3A6); // mtspr 797, r7 (MD_TWC)
        memory.WriteUInt32(0x100C, 0x7CDE_C3A6); // mtspr 798, r6 (MD_RPN installs DTLB entry)
        memory.WriteUInt32(0x1010, 0x80A3_0000); // lwz r5, 0(r3)

        memory.WriteUInt32(0x0000_2000, 0xDEAD_BEEF);

        cpu.Reset(0x1000);
        cpu.WriteMachineStateRegister(0x0000_0010);
        cpu.Registers[8] = 0; // Index 0
        cpu.Registers[4] = 0x8000_0200; // EPN + valid
        cpu.Registers[7] = 0x0000_0000;
        cpu.Registers[6] = 0x0000_2000; // RPN
        cpu.Registers[3] = 0x8000_0000; // Effective address mapped by entry above

        for (var step = 0; step < 5; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0xDEAD_BEEFu, cpu.Registers[5]);
    }

    [Fact]
    public void DataLoadShouldDerivePageSizeFromMpc8xxTableWalkControl()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new SparseMemoryBus(maxMappedBytes: 64UL * 1024UL * 1024UL);

        memory.WriteUInt32(0x1000, 0x80A3_0000); // lwz r5, 0(r3)
        memory.WriteUInt32(0x0014_0000, 0xCAFE_BABE); // 0x0010_0000 base + 0x0004_0000 offset within 512KB page

        cpu.Reset(0x1000);
        cpu.WriteMachineStateRegister(0x0000_0010); // Data relocation enabled.
        cpu.WriteSpecialPurposeRegister(792, 0x0000_0000); // MD_CTR index 0
        cpu.WriteSpecialPurposeRegister(795, 0x8000_0200); // MD_EPN + valid
        cpu.WriteSpecialPurposeRegister(797, 0x0000_0004); // MD_TWC page size = 512KB
        cpu.WriteSpecialPurposeRegister(798, 0x0010_0000); // MD_RPN
        cpu.Registers[3] = 0x8004_0000; // Effective address in same 512KB page

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xCAFE_BABEu, cpu.Registers[5]);
    }

    [Fact]
    public void InstructionFetchShouldUseInstalledMpc8xxInstructionTlbEntryWhenRelocationEnabled()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new SparseMemoryBus(maxMappedBytes: 64UL * 1024UL * 1024UL);

        memory.WriteUInt32(0x0000_1000, 0x3860_0011); // li r3, 0x11 (effective address stream)
        memory.WriteUInt32(0x0000_3000, 0x3860_0055); // li r3, 0x55 (translated physical address)

        cpu.Reset(0x1000);
        cpu.WriteMachineStateRegister(0x0000_0020); // Instruction relocation enabled.
        cpu.WriteSpecialPurposeRegister(784, 0x0200_0000); // MI_CTR index 0
        cpu.WriteSpecialPurposeRegister(787, 0x0000_1200); // MI_EPN for EA 0x1000 with valid bit
        cpu.WriteSpecialPurposeRegister(790, 0x0000_3000); // MI_RPN for PA 0x3000

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x55u, cpu.Registers[3]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void LswiShouldLoadSequentialBytesIntoRegisters()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x4000);

        memory.WriteUInt32(0x1000, 0x7C8B_E4AA); // lswi r4, r11, 0x1C

        cpu.Reset(0x1000);
        cpu.Registers[11] = 0x1200;

        for (var index = 0; index < 28; index++)
        {
            memory.WriteByte(0x1200u + (uint)index, (byte)(index + 1));
        }

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x0102_0304u, cpu.Registers[4]);
        Assert.Equal(0x0506_0708u, cpu.Registers[5]);
        Assert.Equal(0x090A_0B0Cu, cpu.Registers[6]);
        Assert.Equal(0x191A_1B1Cu, cpu.Registers[10]);
    }

    [Fact]
    public void MfcrShouldCopyConditionRegisterToGeneralPurposeRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7D20_0026); // mfcr r9

        cpu.Reset(0x1000);
        cpu.Registers.Cr = 0xA5A5_5A5A;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xA5A5_5A5Au, cpu.Registers[9]);
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void MtcrfShouldUpdateOnlySelectedConditionRegisterFields()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7D88_0120); // mtcrf 0x80, r12
        memory.WriteUInt32(0x1004, 0x7D80_8120); // mtcrf 0x08, r12

        cpu.Reset(0x1000);
        cpu.Registers[12] = 0xABCD_EF12;
        cpu.Registers.Cr = 0x1111_2222;

        cpu.ExecuteCycle(memory);
        Assert.Equal(0xA111_2222u, cpu.Registers.Cr);

        cpu.ExecuteCycle(memory);
        Assert.Equal(0xA111_E222u, cpu.Registers.Cr);
    }

    [Fact]
    public void StswiShouldStoreSequentialBytesFromRegisters()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x4000);

        memory.WriteUInt32(0x1000, 0x7C8B_E5AA); // stswi r4, r11, 0x1C

        cpu.Reset(0x1000);
        cpu.Registers[11] = 0x1200;
        cpu.Registers[4] = 0x1122_3344;
        cpu.Registers[5] = 0x5566_7788;
        cpu.Registers[6] = 0x99AA_BBCC;
        cpu.Registers[7] = 0xDDEE_FF00;
        cpu.Registers[8] = 0x0102_0304;
        cpu.Registers[9] = 0x0506_0708;
        cpu.Registers[10] = 0x090A_0B0C;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x11u, memory.ReadByte(0x1200));
        Assert.Equal(0x22u, memory.ReadByte(0x1201));
        Assert.Equal(0x33u, memory.ReadByte(0x1202));
        Assert.Equal(0x44u, memory.ReadByte(0x1203));
        Assert.Equal(0x55u, memory.ReadByte(0x1204));
        Assert.Equal(0x66u, memory.ReadByte(0x1205));
        Assert.Equal(0x77u, memory.ReadByte(0x1206));
        Assert.Equal(0x88u, memory.ReadByte(0x1207));
        Assert.Equal(0x09u, memory.ReadByte(0x1218));
        Assert.Equal(0x0Au, memory.ReadByte(0x1219));
        Assert.Equal(0x0Bu, memory.ReadByte(0x121A));
        Assert.Equal(0x0Cu, memory.ReadByte(0x121B));
    }

    [Fact]
    public void SthxShouldStoreHalfWordAtIndexedAddress()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C1D_F32E); // sthx r0, r29, r30

        cpu.Reset(0x1000);
        cpu.Registers[0] = 0xA1B2_C3D4;
        cpu.Registers[29] = 0x1200;
        cpu.Registers[30] = 0x0010;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0xC3u, memory.ReadByte(0x1210));
        Assert.Equal(0xD4u, memory.ReadByte(0x1211));
        Assert.Equal(0x1004u, cpu.ProgramCounter);
    }

    [Fact]
    public void SnapshotRoundTripShouldRestoreCpuState()
    {
        var sourceCpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7C60_0124); // mtmsr r3
        memory.WriteUInt32(0x1004, 0x7C8C_42E6); // mftb r4
        memory.WriteUInt32(0x1008, 0x3860_0077); // li r3, 0x77
        memory.WriteUInt32(0x100C, 0x4400_0002); // sc

        sourceCpu.Reset(0x1000);
        sourceCpu.Registers[0] = 0xAAAA_BBBB;
        sourceCpu.Registers[1] = 0x1111_2222;
        sourceCpu.Registers[3] = 0x1234_5678;

        for (var step = 0; step < 4; step++)
        {
            sourceCpu.ExecuteCycle(memory);
        }

        var snapshot = sourceCpu.CreateSnapshot();
        var restoredCpu = new PowerPc32CpuCore();
        restoredCpu.RestoreSnapshot(snapshot);

        Assert.Equal(sourceCpu.ProgramCounter, restoredCpu.ProgramCounter);
        Assert.Equal(sourceCpu.Registers[0], restoredCpu.Registers[0]);
        Assert.Equal(sourceCpu.Registers[1], restoredCpu.Registers[1]);
        Assert.Equal(sourceCpu.Registers[4], restoredCpu.Registers[4]);
        Assert.Equal(sourceCpu.Registers.Lr, restoredCpu.Registers.Lr);
        Assert.Equal(sourceCpu.Registers.Cr, restoredCpu.Registers.Cr);
        Assert.Equal(sourceCpu.Registers.Xer, restoredCpu.Registers.Xer);
        Assert.Equal(sourceCpu.Halted, restoredCpu.Halted);
        Assert.Equal(sourceCpu.SupervisorCallCounters[0x77], restoredCpu.SupervisorCallCounters[0x77]);

        memory.WriteUInt32(0x1010, 0x7CA0_00A6); // mfmsr r5
        memory.WriteUInt32(0x1014, 0x7CC0_00A6); // mfmsr r6

        restoredCpu.ExecuteCycle(memory); // mfmsr r5
        restoredCpu.ExecuteCycle(memory); // mfmsr r6

        Assert.Equal(0x1234_5678u, restoredCpu.Registers[5]);
        Assert.Equal(0x1234_5678u, restoredCpu.Registers[6]);
    }

    [Fact]
    public void StwuShouldStoreOldRsValueBeforeUpdatingBaseRegister()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x9421_FFF8); // stwu r1, -8(r1)

        cpu.Reset(0x1000);
        cpu.Registers[1] = 0x2000;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x1FF8u, cpu.Registers[1]);
        Assert.Equal(0x2000u, memory.ReadUInt32(0x1FF8));
    }

    [Fact]
    public void SystemCallShouldDefaultReturnCodeToZero()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_002B); // li r3, 0x2B
        memory.WriteUInt32(0x1004, 0x4400_0002); // sc

        cpu.Reset(0x1000);
        cpu.ExecuteCycle(memory); // li
        cpu.ExecuteCycle(memory); // sc

        Assert.Equal(0u, cpu.Registers[3]);
        Assert.Equal(0x1008u, cpu.ProgramCounter);
    }

    [Fact]
    public void SystemCallShouldUseInjectedSupervisorHandler()
    {
        var handler = new StaticSupervisorCallHandler(
            new PowerPcSupervisorCallResult(
                ReturnValue: 0xABCD1234,
                Halt: true,
                NextProgramCounter: 0x2000));

        var cpu = new PowerPc32CpuCore(handler);
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);
        memory.WriteUInt32(0x1000, 0x4400_0002); // sc

        cpu.Reset(0x1000);
        cpu.Registers[3] = 0x77;
        cpu.Registers[4] = 0x11;
        cpu.Registers[5] = 0x22;
        cpu.Registers[6] = 0x33;
        cpu.Registers[7] = 0x44;

        cpu.ExecuteCycle(memory);

        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD1234u, cpu.Registers[3]);
        Assert.Equal(0x2000u, cpu.ProgramCounter);

        Assert.NotNull(handler.LastContext);
        Assert.Equal(0x1000u, handler.LastContext.Value.ProgramCounter);
        Assert.Equal(0x77u, handler.LastContext.Value.ServiceCode);
        Assert.Equal(0x11u, handler.LastContext.Value.Argument0);
        Assert.Equal(0x22u, handler.LastContext.Value.Argument1);
        Assert.Equal(0x33u, handler.LastContext.Value.Argument2);
        Assert.Equal(0x44u, handler.LastContext.Value.Argument3);
    }

    [Fact]
    public void SystemCallShouldTrackServiceCodeCounters()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0011); // li r3, 0x11
        memory.WriteUInt32(0x1004, 0x4400_0002); // sc
        memory.WriteUInt32(0x1008, 0x3860_0022); // li r3, 0x22
        memory.WriteUInt32(0x100C, 0x4400_0002); // sc
        memory.WriteUInt32(0x1010, 0x3860_0011); // li r3, 0x11
        memory.WriteUInt32(0x1014, 0x4400_0002); // sc

        cpu.Reset(0x1000);

        for (var step = 0; step < 6; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(2, cpu.SupervisorCallCounters[0x11]);
        Assert.Equal(1, cpu.SupervisorCallCounters[0x22]);
    }

    [Fact]
    public void CompareAndBeqShouldBranchOnEqual()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0000); // li r3, 0
        memory.WriteUInt32(0x1004, 0x2C03_0000); // cmpwi r3, 0
        memory.WriteUInt32(0x1008, 0x4182_000C); // beq 0x1014
        memory.WriteUInt32(0x100C, 0x3880_0001); // li r4, 1
        memory.WriteUInt32(0x1010, 0x4800_0008); // b 0x1018
        memory.WriteUInt32(0x1014, 0x3880_0002); // li r4, 2

        cpu.Reset(0x1000);

        for (var step = 0; step < 4; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0x2u, cpu.Registers[4]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void CompareAndBneShouldBranchOnNotEqual()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x3860_0001); // li r3, 1
        memory.WriteUInt32(0x1004, 0x2C03_0000); // cmpwi r3, 0
        memory.WriteUInt32(0x1008, 0x4082_000C); // bne 0x1014
        memory.WriteUInt32(0x100C, 0x3880_0001); // li r4, 1
        memory.WriteUInt32(0x1010, 0x4800_0008); // b 0x1018
        memory.WriteUInt32(0x1014, 0x3880_0002); // li r4, 2

        cpu.Reset(0x1000);

        for (var step = 0; step < 4; step++)
        {
            cpu.ExecuteCycle(memory);
        }

        Assert.Equal(0x2u, cpu.Registers[4]);
        Assert.Equal(0x1018u, cpu.ProgramCounter);
    }

    [Fact]
    public void SrwiAliasViaRlwinmShouldShiftRightLogical()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x576A_F0BE); // srwi r10, r27, 2

        cpu.Reset(0x1000);
        cpu.Registers[27] = 0x0136_9144;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x004D_A451u, cpu.Registers[10]);
    }

    [Fact]
    public void ClrlwiAliasViaRlwinmShouldClearMostSignificantBits()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x57E0_043E); // clrlwi r0, r31, 16

        cpu.Reset(0x1000);
        cpu.Registers[31] = 0xABCD_1234;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0x0000_1234u, cpu.Registers[0]);
    }

    [Fact]
    public void SlwShouldZeroResultWhenShiftAmountIsAtLeastThirtyTwo()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA41830); // slw r4, r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[5] = 0xDEAD_BEEF;
        cpu.Registers[3] = 32;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[4]);
    }

    [Fact]
    public void SrwShouldZeroResultWhenShiftAmountIsAtLeastThirtyTwo()
    {
        var cpu = new PowerPc32CpuCore();
        var memory = new ArrayMemoryBus(baseAddress: 0x1000, sizeBytes: 0x3000);

        memory.WriteUInt32(0x1000, 0x7CA41830 + (uint)((536 - 24) << 1)); // srw r4, r5, r3

        cpu.Reset(0x1000);
        cpu.Registers[5] = 0xDEAD_BEEF;
        cpu.Registers[3] = 63;

        cpu.ExecuteCycle(memory);

        Assert.Equal(0u, cpu.Registers[4]);
    }

    private sealed class StaticSupervisorCallHandler(PowerPcSupervisorCallResult result)
        : IPowerPcSupervisorCallHandler
    {
        public PowerPcSupervisorCallContext? LastContext { get; private set; }

        public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
        {
            LastContext = context;
            return result;
        }
    }
}
