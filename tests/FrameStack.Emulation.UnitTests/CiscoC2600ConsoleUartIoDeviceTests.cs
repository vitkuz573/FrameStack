using FrameStack.Emulation.Runtime;

namespace FrameStack.Emulation.UnitTests;

public sealed class CiscoC2600ConsoleUartIoDeviceTests
{
    [Fact]
    public void ReadByteShouldReportReadyStatusAtOffsetFive()
    {
        var device = new CiscoC2600ConsoleUartIoDevice();

        Assert.Equal((byte)0x71, device.ReadByte(0x05));
        Assert.Equal((byte)'\r', device.ReadByte(0x00));
        Assert.Equal((byte)0x70, device.ReadByte(0x05));
    }

    [Fact]
    public void ReadUInt32ShouldExposeReadyStatusInBigEndianWindow()
    {
        var device = new CiscoC2600ConsoleUartIoDevice();

        Assert.Equal(0x0071_0000u, device.ReadUInt32(0x04));
    }

    [Fact]
    public void WriteDataByteShouldKeepTransmitterReady()
    {
        var device = new CiscoC2600ConsoleUartIoDevice();

        device.WriteByte(0x00, 0x41);
        device.WriteByte(0x00, 0x42);

        Assert.Equal((byte)0x70, (byte)(device.ReadByte(0x05) & 0x70));
    }

    [Fact]
    public void WriteDataByteShouldInvokeTransmitSink()
    {
        byte? transmitted = null;
        var device = new CiscoC2600ConsoleUartIoDevice(transmitByteSink: value => transmitted = value);

        device.WriteByte(0x00, 0x41);

        Assert.Equal((byte)0x41, transmitted);
    }

    [Fact]
    public void WriteNonDataByteShouldNotInvokeTransmitSink()
    {
        var transmitCount = 0;
        var device = new CiscoC2600ConsoleUartIoDevice(transmitByteSink: _ => transmitCount++);

        device.WriteByte(0x01, 0x41);

        Assert.Equal(0, transmitCount);
    }

    [Fact]
    public void ReadByteShouldConsumeReceiveFifoInOrder()
    {
        var device = new CiscoC2600ConsoleUartIoDevice(initialReceiveBytes: [(byte)'A', (byte)'B']);

        Assert.Equal((byte)0x71, device.ReadByte(0x05));
        Assert.Equal((byte)'A', device.ReadByte(0x00));
        Assert.Equal((byte)0x71, device.ReadByte(0x05));
        Assert.Equal((byte)'B', device.ReadByte(0x00));
        Assert.Equal((byte)0x70, device.ReadByte(0x05));
    }

    [Fact]
    public void AutoCarriageReturnModeShouldKeepDataReadyWhenReceiveFifoDrains()
    {
        var device = new CiscoC2600ConsoleUartIoDevice(
            initialReceiveBytes: [],
            autoCarriageReturnWhenIdle: true);

        Assert.Equal((byte)0x71, device.ReadByte(0x05));
        Assert.Equal((byte)'\r', device.ReadByte(0x00));
        Assert.Equal((byte)0x71, device.ReadByte(0x05));
    }
}
