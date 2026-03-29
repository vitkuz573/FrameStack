using System.Collections.Generic;
using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Runtime;

public sealed class CiscoC2600Amdp2IoDevice : IMemoryMappedDevice
{
    private const uint DefaultBaseAddress = 0x6800_0000;
    private const uint DefaultMappedSizeBytes = 0x0020_0000;
    private const uint AdapterCommandOffset = 0x0000_0500;
    private const uint AdapterDataOffset = 0x0000_0504;
    private const uint PresenceStatusOffset = 0x0001_00F0;
    private const uint DefaultAmdp2DeviceId = 0x2000_1022;
    private const uint DefaultPresenceStatusMask = 0x4000_0000;
    private const uint ControlStatusRegister0 = 0x0000_0000;
    private const uint Csr0InitBit = 0x0000_0001;
    private const uint Csr0StartBit = 0x0000_0002;
    private const uint Csr0StopBit = 0x0000_0004;
    private const uint Csr0TxOnBit = 0x0000_0010;
    private const uint Csr0RxOnBit = 0x0000_0020;
    private const uint Csr0InterruptEnableBit = 0x0000_0040;
    private const uint Csr0InitDoneBit = 0x0000_0100;
    private const uint Csr0WritableMask = Csr0InitBit |
                                          Csr0StartBit |
                                          Csr0StopBit |
                                          Csr0InterruptEnableBit;

    private readonly byte[] _registerWindow;
    private readonly Dictionary<uint, uint> _adapterRegisterBank = new();
    private uint _adapterCommand;
    private uint _presenceStatus = DefaultPresenceStatusMask;

    public CiscoC2600Amdp2IoDevice(uint baseAddress = DefaultBaseAddress)
    {
        BaseAddress = baseAddress;
        SizeBytes = DefaultMappedSizeBytes;
        _registerWindow = new byte[checked((int)SizeBytes)];

        // Before software reset the chip presents its PCI VID/DID through RDP with RAP=0.
        // IOS reads this value to verify it is talking to an Am79C971 before starting INIT.
        // After IOS writes STOP (bit 2) the state machine below transitions to 0x0004.
        _adapterRegisterBank[ControlStatusRegister0] = DefaultAmdp2DeviceId;

        StoreUInt32(PresenceStatusOffset, _presenceStatus);
    }

    public uint BaseAddress { get; }

    public uint SizeBytes { get; }

    public byte ReadByte(uint offset)
    {
        if (!IsInRange(offset))
        {
            return 0;
        }

        if (IsWordRegisterByte(offset, AdapterDataOffset))
        {
            return ExtractBigEndianByte(ReadAdapterDataRegister(), offset - AdapterDataOffset);
        }

        if (IsWordRegisterByte(offset, PresenceStatusOffset))
        {
            return ExtractBigEndianByte(_presenceStatus, offset - PresenceStatusOffset);
        }

        return _registerWindow[offset];
    }

    public void WriteByte(uint offset, byte value)
    {
        if (!IsInRange(offset))
        {
            return;
        }

        _registerWindow[offset] = value;

        if (IsWordRegisterByte(offset, AdapterCommandOffset))
        {
            _adapterCommand = LoadUInt32(AdapterCommandOffset);
            return;
        }

        if (IsWordRegisterByte(offset, AdapterDataOffset))
        {
            var writeValue = LoadUInt32(AdapterDataOffset);
            _adapterRegisterBank[_adapterCommand] = ResolveAdapterDataWriteValue(_adapterCommand, writeValue);
            return;
        }

        if (IsWordRegisterByte(offset, PresenceStatusOffset))
        {
            _presenceStatus = LoadUInt32(PresenceStatusOffset);
        }
    }

    public uint ReadUInt32(uint offset)
    {
        if (!IsInRange(offset))
        {
            return 0;
        }

        if (offset == AdapterDataOffset)
        {
            return ReadAdapterDataRegister();
        }

        if (offset == PresenceStatusOffset)
        {
            return _presenceStatus;
        }

        if (offset == AdapterCommandOffset)
        {
            return _adapterCommand;
        }

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
        if (!IsInRange(offset))
        {
            return;
        }

        if (offset == AdapterCommandOffset)
        {
            _adapterCommand = value;
            StoreUInt32(offset, value);
            return;
        }

        if (offset == AdapterDataOffset)
        {
            _adapterRegisterBank[_adapterCommand] = ResolveAdapterDataWriteValue(_adapterCommand, value);
            StoreUInt32(offset, value);
            return;
        }

        if (offset == PresenceStatusOffset)
        {
            _presenceStatus = value;
            StoreUInt32(offset, value);
            return;
        }

        WriteByte(offset, (byte)(value >> 24));
        WriteByte(offset + 1, (byte)(value >> 16));
        WriteByte(offset + 2, (byte)(value >> 8));
        WriteByte(offset + 3, (byte)value);
    }

    private static bool IsWordRegisterByte(uint offset, uint registerOffset)
    {
        return offset >= registerOffset &&
               offset < registerOffset + sizeof(uint);
    }

    private bool IsInRange(uint offset)
    {
        return offset < SizeBytes;
    }

    private uint LoadUInt32(uint offset)
    {
        var b0 = _registerWindow[offset];
        var b1 = _registerWindow[offset + 1];
        var b2 = _registerWindow[offset + 2];
        var b3 = _registerWindow[offset + 3];

        return ((uint)b0 << 24) |
               ((uint)b1 << 16) |
               ((uint)b2 << 8) |
               b3;
    }

    private void StoreUInt32(uint offset, uint value)
    {
        _registerWindow[offset] = (byte)(value >> 24);
        _registerWindow[offset + 1] = (byte)(value >> 16);
        _registerWindow[offset + 2] = (byte)(value >> 8);
        _registerWindow[offset + 3] = (byte)value;
    }

    private static byte ExtractBigEndianByte(uint value, uint byteOffset)
    {
        return byteOffset switch
        {
            0 => (byte)(value >> 24),
            1 => (byte)(value >> 16),
            2 => (byte)(value >> 8),
            _ => (byte)value,
        };
    }

    private uint ReadAdapterDataRegister()
    {
        return _adapterRegisterBank.TryGetValue(_adapterCommand, out var value)
            ? value
            : 0u;
    }

    private static uint ResolveAdapterDataWriteValue(uint command, uint value)
    {
        if (command != ControlStatusRegister0)
        {
            return value;
        }

        return ResolveControlStatusRegister0Write(value);
    }

    private static uint ResolveControlStatusRegister0Write(uint value)
    {
        var writable = value & Csr0WritableMask;

        if ((writable & Csr0StopBit) != 0)
        {
            // STOP request forces controller into reset-like state.
            return Csr0StopBit;
        }

        if ((writable & Csr0StartBit) != 0)
        {
            // Firmware expects START to transition RX/TX online.
            writable |= Csr0RxOnBit | Csr0TxOnBit | Csr0InitDoneBit;
        }
        else if ((writable & Csr0InitBit) != 0)
        {
            // Minimal INIT emulation: complete initialization immediately.
            writable |= Csr0InitDoneBit;
        }

        return writable;
    }
}
