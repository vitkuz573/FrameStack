using FrameStack.Emulation.Abstractions;
using System.Collections.Generic;

namespace FrameStack.Emulation.Runtime;

public sealed class CiscoC2600ConsoleUartIoDevice : IMemoryMappedDevice
{
    private const uint DefaultBaseAddress = 0xFFE0_0000;
    private const uint DefaultMappedSizeBytes = 0x20;
    private const uint DataRegisterOffset = 0x00;
    private const uint StatusRegisterOffset = 0x05;
    private const byte StatusTransmitReadyMask = 0x70;
    private const byte StatusDataReadyMask = 0x01;

    private readonly Queue<byte> _receiveFifo = new();

    public CiscoC2600ConsoleUartIoDevice(
        uint baseAddress = DefaultBaseAddress,
        Action<byte>? transmitByteSink = null,
        IEnumerable<byte>? initialReceiveBytes = null)
    {
        BaseAddress = baseAddress;
        SizeBytes = DefaultMappedSizeBytes;
        TransmitByteSink = transmitByteSink;

        var seededBytes = initialReceiveBytes ?? [(byte)'\r'];
        foreach (var value in seededBytes)
        {
            _receiveFifo.Enqueue(value);
        }
    }

    public uint BaseAddress { get; }

    public uint SizeBytes { get; }

    public Action<byte>? TransmitByteSink { get; set; }

    public byte ReadByte(uint offset)
    {
        if (offset == StatusRegisterOffset)
        {
            return BuildStatusValue();
        }

        if (offset == DataRegisterOffset)
        {
            return _receiveFifo.Count > 0
                ? _receiveFifo.Dequeue()
                : (byte)0;
        }

        return 0;
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

    public void EnqueueReceiveByte(byte value)
    {
        _receiveFifo.Enqueue(value);
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

    private byte BuildStatusValue()
    {
        var value = StatusTransmitReadyMask;

        if (_receiveFifo.Count > 0)
        {
            value |= StatusDataReadyMask;
        }

        return value;
    }
}
