using FrameStack.Emulation.Abstractions;
using System.Buffers.Binary;

namespace FrameStack.Emulation.Memory;

public sealed class ArrayMemoryBus : IMemoryBus
{
    private readonly uint _baseAddress;
    private readonly byte[] _memory;

    public ArrayMemoryBus(uint baseAddress, int sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Memory size must be greater than zero.");
        }

        _baseAddress = baseAddress;
        _memory = new byte[sizeBytes];
    }

    public byte ReadByte(uint address)
    {
        var offset = ResolveOffset(address, sizeof(byte));
        return _memory[offset];
    }

    public void WriteByte(uint address, byte value)
    {
        var offset = ResolveOffset(address, sizeof(byte));
        _memory[offset] = value;
    }

    public uint ReadUInt32(uint address)
    {
        var offset = ResolveOffset(address, sizeof(uint));
        return BinaryPrimitives.ReadUInt32BigEndian(_memory.AsSpan(offset, sizeof(uint)));
    }

    public void WriteUInt32(uint address, uint value)
    {
        var offset = ResolveOffset(address, sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(_memory.AsSpan(offset, sizeof(uint)), value);
    }

    public void LoadBytes(uint baseAddress, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var offset = ResolveOffset(baseAddress, bytes.Length);
        Array.Copy(bytes, 0, _memory, offset, bytes.Length);
    }

    private int ResolveOffset(uint address, int length)
    {
        if (address < _baseAddress)
        {
            throw new InvalidOperationException($"Address 0x{address:X8} is below memory base 0x{_baseAddress:X8}.");
        }

        var offset = checked((int)(address - _baseAddress));

        if (offset < 0 || offset + length > _memory.Length)
        {
            throw new InvalidOperationException(
                $"Address range 0x{address:X8}..0x{address + (uint)length - 1:X8} is out of memory bounds.");
        }

        return offset;
    }
}
