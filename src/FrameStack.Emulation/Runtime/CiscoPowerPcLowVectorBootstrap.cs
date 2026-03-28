using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Images;

namespace FrameStack.Emulation.Runtime;

public static class CiscoPowerPcLowVectorBootstrap
{
    private const uint LisR0OpcodeBase = 0x3C00_0000;
    private const uint OriR0OpcodeBase = 0x6000_0000;
    private const uint MoveR0ToCounterRegister = 0x7C09_03A6;
    private const uint BranchToCounterRegister = 0x4E80_0420;
    private const uint ReturnFromInterrupt = 0x4C00_0064;

    private static readonly uint[] ExceptionVectorOffsets =
    [
        0x0000_0100,
        0x0000_0200,
        0x0000_0300,
        0x0000_0400,
        0x0000_0500,
        0x0000_0600,
        0x0000_0700,
        0x0000_0800,
        0x0000_0900,
        0x0000_0C00,
        0x0000_0D00,
        0x0000_0F00
    ];

    public static bool TryInstallEntryStub(
        IMemoryBus memoryBus,
        ImageInspectionResult inspection,
        uint entryPoint)
    {
        ArgumentNullException.ThrowIfNull(memoryBus);

        if (inspection.Architecture != ImageArchitecture.PowerPc32 ||
            inspection.Endianness != ImageEndianness.BigEndian ||
            string.IsNullOrWhiteSpace(inspection.CiscoFamily))
        {
            return false;
        }

        if (!ShouldInstallEntryStub(memoryBus))
        {
            return false;
        }

        var entryUpper = (entryPoint >> 16) & 0xFFFFu;
        var entryLower = entryPoint & 0xFFFFu;

        memoryBus.WriteUInt32(0x0000_0000, LisR0OpcodeBase | entryUpper);
        memoryBus.WriteUInt32(0x0000_0004, OriR0OpcodeBase | entryLower);
        memoryBus.WriteUInt32(0x0000_0008, MoveR0ToCounterRegister);
        memoryBus.WriteUInt32(0x0000_000C, BranchToCounterRegister);
        InstallDefaultExceptionStubs(memoryBus);

        return true;
    }

    private static bool ShouldInstallEntryStub(IMemoryBus memoryBus)
    {
        // A zero first word indicates no executable null-vector handler.
        // Other words may contain boot scratch values in restored checkpoints.
        return memoryBus.ReadUInt32(0x0000_0000) == 0;
    }

    private static void InstallDefaultExceptionStubs(IMemoryBus memoryBus)
    {
        foreach (var vectorOffset in ExceptionVectorOffsets)
        {
            if (memoryBus.ReadUInt32(vectorOffset) == 0)
            {
                memoryBus.WriteUInt32(vectorOffset, ReturnFromInterrupt);
            }
        }
    }
}
