using FrameStack.Emulation.Runtime;

namespace FrameStack.Emulation.UnitTests;

public sealed class CiscoC2600PortAdapterIoDeviceTests
{
    [Fact]
    public void ControlRegisterShouldExposeReadyBitOnReads()
    {
        var device = new CiscoC2600PortAdapterIoDevice();

        Assert.Equal((byte)0x80, device.ReadByte(0x14));

        device.WriteByte(0x14, 0x40);

        Assert.Equal((byte)0xC0, device.ReadByte(0x14));
    }

    [Fact]
    public void AdapterRegisterShouldShiftConfiguredAdapterIdBits()
    {
        var device = new CiscoC2600PortAdapterIoDevice(adapterId: 0x0091);

        // Assert transaction-select before sampling bit stream.
        device.WriteByte(0x1C, 0x10);

        Assert.True(ReadBitAndAdvance(device)); // bit0 = 1
        Assert.False(ReadBitAndAdvance(device)); // bit1 = 0
        Assert.False(ReadBitAndAdvance(device)); // bit2 = 0
        Assert.False(ReadBitAndAdvance(device)); // bit3 = 0
        Assert.True(ReadBitAndAdvance(device)); // bit4 = 1
    }

    [Fact]
    public void CommandClockWithoutSampleShouldNotAdvanceBitCursor()
    {
        var device = new CiscoC2600PortAdapterIoDevice(adapterId: 0x0091);

        device.WriteByte(0x1C, 0x10);

        // Command-phase pulses toggle data and clock but never read serial input.
        device.WriteByte(0x1C, 0x50);
        device.WriteByte(0x1C, 0x54);
        device.WriteByte(0x1C, 0x50);
        device.WriteByte(0x1C, 0x10);
        device.WriteByte(0x1C, 0x14);
        device.WriteByte(0x1C, 0x10);

        Assert.True(ReadBitAndAdvance(device)); // still bit0 = 1
    }

    [Fact]
    public void TransactionSelectReassertShouldResetBitCursor()
    {
        var device = new CiscoC2600PortAdapterIoDevice(adapterId: 0x0091);

        device.WriteByte(0x1C, 0x10);

        Assert.True(ReadBitAndAdvance(device)); // bit0
        Assert.False(ReadBitAndAdvance(device)); // bit1

        // Deassert and assert select starts a new transaction.
        device.WriteByte(0x1C, 0x00);
        device.WriteByte(0x1C, 0x10);

        Assert.True(ReadBitAndAdvance(device)); // bit0 again
    }

    private static bool ReadBitAndAdvance(CiscoC2600PortAdapterIoDevice device)
    {
        device.WriteByte(0x1C, 0x14);
        var bitSet = IsDataBitSet(device.ReadByte(0x1C));
        device.WriteByte(0x1C, 0x10);
        return bitSet;
    }

    private static bool IsDataBitSet(byte value)
    {
        return (value & 0x80) != 0;
    }
}
