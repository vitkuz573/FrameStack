using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Runtime;

public sealed class CiscoC2600ConsoleUartIoDevice : IMemoryMappedDevice
{
    private const uint DefaultBaseAddress = 0xFFE0_0000;
    private const uint DefaultMappedSizeBytes = 0x20;
    private const uint DataRegisterOffset = 0x00;
    private const uint StatusRegisterOffset = 0x05;
    private const byte StatusReadyMask = 0x70;

    public CiscoC2600ConsoleUartIoDevice(
        uint baseAddress = DefaultBaseAddress,
        Action<byte>? transmitByteSink = null)
    {
        BaseAddress = baseAddress;
        SizeBytes = DefaultMappedSizeBytes;
        TransmitByteSink = transmitByteSink;
    }

    public uint BaseAddress { get; }

    public uint SizeBytes { get; }

    public Action<byte>? TransmitByteSink { get; set; }

    public byte ReadByte(uint offset)
    {
        return offset == StatusRegisterOffset
            ? StatusReadyMask
            : (byte)0;
    }

    public void WriteByte(uint offset, byte value)
    {
        // IOS writes outgoing characters through the TX data register.
        // The minimal bootstrap model keeps the transmitter always ready.
        if (offset == DataRegisterOffset)
        {
            TransmitByteSink?.Invoke(value);
            return;
        }
    }

    public uint ReadUInt32(uint offset)
    {
        var b0 = ReadByte(offset);
        var b1 = ReadByte(offset + 1);
        var b2 = ReadByte(offset + 2);
        var b3 = ReadByte(offset + 3);

        return ((uint)b0 << 24) |
               ((uint)b1 << 16) |
               ((uint)b2 << 8) |
               b3;
    }

    public void WriteUInt32(uint offset, uint value)
    {
        WriteByte(offset, (byte)(value >> 24));
        WriteByte(offset + 1, (byte)(value >> 16));
        WriteByte(offset + 2, (byte)(value >> 8));
        WriteByte(offset + 3, (byte)value);
    }
}
