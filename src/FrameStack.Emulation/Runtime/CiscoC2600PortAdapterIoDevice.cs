using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Runtime;

public sealed class CiscoC2600PortAdapterIoDevice : IMemoryMappedDevice
{
    private const uint DefaultBaseAddress = 0x6740_0000;
    private const uint DefaultMappedSizeBytes = 0x20;
    private const uint ControlRegisterOffset = 0x14;
    private const uint AdapterRegisterOffset = 0x1C;
    private const byte ReadyBitMask = 0x80;
    private const byte TransactionSelectBitMask = 0x10;
    private const byte ClockBitMask = 0x04;
    private const byte WritableBitsMask = 0x7F;

    private readonly ushort _adapterId;
    private byte _controlRegister;
    private byte _adapterRegisterControl = 0x01;
    private int _adapterIdBitIndex;
    private bool _sawLowControlWriteSinceLastClockPulse;

    public CiscoC2600PortAdapterIoDevice(
        uint baseAddress = DefaultBaseAddress,
        ushort adapterId = 0x0091)
    {
        BaseAddress = baseAddress;
        SizeBytes = DefaultMappedSizeBytes;
        _adapterId = adapterId;
    }

    public uint BaseAddress { get; }

    public uint SizeBytes { get; }

    public byte ReadByte(uint offset)
    {
        return offset switch
        {
            ControlRegisterOffset => (byte)(_controlRegister | ReadyBitMask),
            AdapterRegisterOffset => ComposeAdapterRegisterReadValue(),
            _ => 0,
        };
    }

    public void WriteByte(uint offset, byte value)
    {
        switch (offset)
        {
            case ControlRegisterOffset:
                _controlRegister = (byte)(value & WritableBitsMask);
                return;

            case AdapterRegisterOffset:
                WriteAdapterRegister(value);
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

    private byte ComposeAdapterRegisterReadValue()
    {
        var value = _adapterRegisterControl;
        var serialBit = (_adapterId >> _adapterIdBitIndex) & 1;

        if (serialBit != 0)
        {
            value |= ReadyBitMask;
        }

        return value;
    }

    private void WriteAdapterRegister(byte value)
    {
        var previousValue = _adapterRegisterControl;
        var nextValue = (byte)(value & WritableBitsMask);
        var transferWasActive = (previousValue & TransactionSelectBitMask) != 0;
        var transferIsActive = (nextValue & TransactionSelectBitMask) != 0;

        _adapterRegisterControl = nextValue;

        if (!transferWasActive &&
            transferIsActive)
        {
            // A transaction starts on select assertion.
            _adapterIdBitIndex = 0;
            _sawLowControlWriteSinceLastClockPulse = false;
        }
        else if (transferWasActive &&
                 !transferIsActive)
        {
            _sawLowControlWriteSinceLastClockPulse = false;
        }

        var clockWasHigh = (previousValue & ClockBitMask) != 0;
        var clockIsHigh = (nextValue & ClockBitMask) != 0;

        if (transferWasActive &&
            transferIsActive &&
            !clockWasHigh &&
            !clockIsHigh)
        {
            // Command serialization writes a control value while clock is low
            // before pulsing the clock. Data-sample pulses skip this write.
            _sawLowControlWriteSinceLastClockPulse = true;
        }

        if (clockWasHigh &&
            !clockIsHigh &&
            transferWasActive &&
            transferIsActive &&
            !_sawLowControlWriteSinceLastClockPulse)
        {
            _adapterIdBitIndex = (_adapterIdBitIndex + 1) & 0x0F;
        }

        if (clockWasHigh &&
            !clockIsHigh)
        {
            _sawLowControlWriteSinceLastClockPulse = false;
        }
    }
}
