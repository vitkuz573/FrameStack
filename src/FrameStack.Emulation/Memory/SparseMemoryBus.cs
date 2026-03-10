using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Memory;

public sealed class SparseMemoryBus : IMemoryBus
{
    private const int PageSize = 4096;

    private readonly ulong _maxMappedBytes;
    private readonly Dictionary<uint, byte[]> _pages = new();

    public SparseMemoryBus(ulong maxMappedBytes)
    {
        if (maxMappedBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMappedBytes), "Max mapped bytes must be greater than zero.");
        }

        _maxMappedBytes = maxMappedBytes;
    }

    public byte ReadByte(uint address)
    {
        var pageIndex = address / PageSize;
        var pageOffset = (int)(address % PageSize);

        if (!_pages.TryGetValue(pageIndex, out var page))
        {
            return 0;
        }

        return page[pageOffset];
    }

    public void WriteByte(uint address, byte value)
    {
        var pageIndex = address / PageSize;
        var pageOffset = (int)(address % PageSize);

        if (!_pages.TryGetValue(pageIndex, out var page))
        {
            var projectedBytes = checked(((ulong)_pages.Count + 1UL) * PageSize);

            if (projectedBytes > _maxMappedBytes)
            {
                throw new InvalidOperationException(
                    $"Mapped memory exceeds configured limit {_maxMappedBytes} bytes.");
            }

            page = new byte[PageSize];
            _pages[pageIndex] = page;
        }

        page[pageOffset] = value;
    }

    public uint ReadUInt32(uint address)
    {
        var b0 = ReadByte(address);
        var b1 = ReadByte(address + 1);
        var b2 = ReadByte(address + 2);
        var b3 = ReadByte(address + 3);

        return ((uint)b0 << 24) |
               ((uint)b1 << 16) |
               ((uint)b2 << 8) |
               b3;
    }

    public void WriteUInt32(uint address, uint value)
    {
        WriteByte(address, (byte)(value >> 24));
        WriteByte(address + 1, (byte)(value >> 16));
        WriteByte(address + 2, (byte)(value >> 8));
        WriteByte(address + 3, (byte)value);
    }

    public void LoadBytes(uint baseAddress, byte[] bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            WriteByte(baseAddress + (uint)index, bytes[index]);
        }
    }
}
