using FrameStack.Emulation.Runtime;

namespace FrameStack.Emulation.UnitTests;

public sealed class CiscoC2600Amdp2IoDeviceTests
{
    [Fact]
    public void ReadUInt32ShouldExposeDefaultAmdp2DeviceId()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        Assert.Equal(0x2000_1022u, device.ReadUInt32(0x0504));
    }

    [Fact]
    public void ReadUInt32ShouldExposePresenceStatusMask()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        Assert.Equal(0x4000_0000u, device.ReadUInt32(0x100F0));
    }

    [Fact]
    public void WriteUInt32ShouldUpdateAdapterCommandRegister()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x1234_5678u);

        Assert.Equal(0x1234_5678u, device.ReadUInt32(0x0500));
    }

    [Fact]
    public void WriteUInt32ShouldUpdateAdapterDataRegister()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x0000_0001);
        device.WriteUInt32(0x0504, 0xDEAD_BEEFu);

        Assert.Equal(0xDEAD_BEEFu, device.ReadUInt32(0x0504));
    }

    [Fact]
    public void AdapterDataRegisterShouldBeBankedByCommandRegister()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x0000_0014);
        device.WriteUInt32(0x0504, 0x4000_0000u);
        device.WriteUInt32(0x0500, 0x0000_0000);

        Assert.Equal(0x2000_1022u, device.ReadUInt32(0x0504));

        device.WriteUInt32(0x0500, 0x0000_0014);
        Assert.Equal(0x4000_0000u, device.ReadUInt32(0x0504));
    }

    [Fact]
    public void ByteAccessShouldRoundTripGenericOffsets()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteByte(0x10910, 0x07);
        device.WriteByte(0x10914, 0x02);
        device.WriteByte(0x109C4, 0x12);
        device.WriteByte(0x109C5, 0x34);

        Assert.Equal((byte)0x07, device.ReadByte(0x10910));
        Assert.Equal((byte)0x02, device.ReadByte(0x10914));
        Assert.Equal((byte)0x12, device.ReadByte(0x109C4));
        Assert.Equal((byte)0x34, device.ReadByte(0x109C5));
    }

    [Fact]
    public void WriteUInt32ShouldAcknowledgeInitOnControlStatusRegister0()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x0000_0000);
        device.WriteUInt32(0x0504, 0x0000_0001);

        Assert.Equal(0x2000_0101u, device.ReadUInt32(0x0504));
    }

    [Fact]
    public void WriteUInt32ShouldAcknowledgeStartOnControlStatusRegister0()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x0000_0000);
        device.WriteUInt32(0x0504, 0x0000_0002);

        Assert.Equal(0x2000_0132u, device.ReadUInt32(0x0504));
    }

    [Fact]
    public void WriteUInt32ShouldAcknowledgeStopOnControlStatusRegister0()
    {
        var device = new CiscoC2600Amdp2IoDevice();

        device.WriteUInt32(0x0500, 0x0000_0000);
        device.WriteUInt32(0x0504, 0x0000_0004);

        Assert.Equal(0x2000_0004u, device.ReadUInt32(0x0504));
    }
}
