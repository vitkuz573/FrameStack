using FrameStack.Emulation.Abstractions;
using System.Buffers.Binary;

namespace FrameStack.Emulation.Memory;

public sealed class ArrayMemoryBus : IMemoryBus, IMemoryWriteProtectionBus
{
    private readonly uint _baseAddress;
    private readonly byte[] _memory;
    private readonly List<WriteProtectedRange> _writeProtectedRanges = [];
    private int _privilegedWriteScopeDepth;

    public ArrayMemoryBus(uint baseAddress, int sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Memory size must be greater than zero.");
        }

        _baseAddress = baseAddress;
        _memory = new byte[sizeBytes];
    }

    public void ProtectWriteRange(uint baseAddress, uint sizeBytes)
    {
        if (sizeBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Protected range size must be greater than zero.");
        }

        var start = baseAddress;
        var end = checked(baseAddress + sizeBytes - 1);

        _writeProtectedRanges.Add(new WriteProtectedRange(start, end));
        _writeProtectedRanges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        MergeWriteProtectedRanges();
    }

    public IDisposable BeginPrivilegedWriteScope()
    {
        _privilegedWriteScopeDepth++;
        return new PrivilegedWriteScope(this);
    }

    public byte ReadByte(uint address)
    {
        var offset = ResolveOffset(address, sizeof(byte));
        return _memory[offset];
    }

    public void WriteByte(uint address, byte value)
    {
        if (IsWriteProtected(address, sizeof(byte)))
        {
            return;
        }

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
        if (IsWriteProtected(address, sizeof(uint)))
        {
            return;
        }

        var offset = ResolveOffset(address, sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(_memory.AsSpan(offset, sizeof(uint)), value);
    }

    public void LoadBytes(uint baseAddress, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        if (_writeProtectedRanges.Count > 0 &&
            _privilegedWriteScopeDepth == 0)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                WriteByte(baseAddress + (uint)index, bytes[index]);
            }

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

    private bool IsWriteProtected(uint address, int sizeBytes)
    {
        if (_privilegedWriteScopeDepth > 0 ||
            _writeProtectedRanges.Count == 0)
        {
            return false;
        }

        var start = (ulong)address;
        var end = start + (ulong)sizeBytes - 1;

        foreach (var range in _writeProtectedRanges)
        {
            if (end < range.Start ||
                start > range.End)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private void MergeWriteProtectedRanges()
    {
        if (_writeProtectedRanges.Count <= 1)
        {
            return;
        }

        var targetIndex = 0;

        for (var sourceIndex = 1; sourceIndex < _writeProtectedRanges.Count; sourceIndex++)
        {
            var previous = _writeProtectedRanges[targetIndex];
            var current = _writeProtectedRanges[sourceIndex];
            var previousEndPlusOne = previous.End == uint.MaxValue
                ? (ulong)uint.MaxValue
                : (ulong)previous.End + 1UL;

            if ((ulong)current.Start <= previousEndPlusOne)
            {
                var mergedEnd = Math.Max(previous.End, current.End);
                _writeProtectedRanges[targetIndex] = new WriteProtectedRange(previous.Start, mergedEnd);
                continue;
            }

            targetIndex++;
            _writeProtectedRanges[targetIndex] = current;
        }

        _writeProtectedRanges.RemoveRange(targetIndex + 1, _writeProtectedRanges.Count - targetIndex - 1);
    }

    private void EndPrivilegedWriteScope()
    {
        if (_privilegedWriteScopeDepth == 0)
        {
            return;
        }

        _privilegedWriteScopeDepth--;
    }

    private readonly record struct WriteProtectedRange(uint Start, uint End);

    private sealed class PrivilegedWriteScope(ArrayMemoryBus owner) : IDisposable
    {
        private ArrayMemoryBus? _owner = owner;

        public void Dispose()
        {
            _owner?.EndPrivilegedWriteScope();
            _owner = null;
        }
    }
}
