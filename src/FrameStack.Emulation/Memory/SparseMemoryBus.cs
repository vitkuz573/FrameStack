using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Memory;

public sealed class SparseMemoryBus : IMemoryBus
{
    private const int PageSize = 4096;
    private const int PageShift = 12;
    private const uint PageMask = PageSize - 1;

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
        var pageIndex = address >> PageShift;
        var pageOffset = (int)(address & PageMask);

        if (!_pages.TryGetValue(pageIndex, out var page))
        {
            return 0;
        }

        return page[pageOffset];
    }

    public void WriteByte(uint address, byte value)
    {
        var pageIndex = address >> PageShift;
        var pageOffset = (int)(address & PageMask);
        var page = GetOrCreatePage(pageIndex);

        page[pageOffset] = value;
    }

    public uint ReadUInt32(uint address)
    {
        var pageIndex = address >> PageShift;
        var pageOffset = (int)(address & PageMask);

        if (pageOffset <= PageSize - sizeof(uint))
        {
            if (!_pages.TryGetValue(pageIndex, out var page))
            {
                return 0;
            }

            return ((uint)page[pageOffset] << 24) |
                   ((uint)page[pageOffset + 1] << 16) |
                   ((uint)page[pageOffset + 2] << 8) |
                   page[pageOffset + 3];
        }

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
        var pageIndex = address >> PageShift;
        var pageOffset = (int)(address & PageMask);

        if (pageOffset <= PageSize - sizeof(uint))
        {
            var page = GetOrCreatePage(pageIndex);
            page[pageOffset] = (byte)(value >> 24);
            page[pageOffset + 1] = (byte)(value >> 16);
            page[pageOffset + 2] = (byte)(value >> 8);
            page[pageOffset + 3] = (byte)value;
            return;
        }

        WriteByte(address, (byte)(value >> 24));
        WriteByte(address + 1, (byte)(value >> 16));
        WriteByte(address + 2, (byte)(value >> 8));
        WriteByte(address + 3, (byte)value);
    }

    public void LoadBytes(uint baseAddress, byte[] bytes)
    {
        var sourceOffset = 0;

        while (sourceOffset < bytes.Length)
        {
            var address = baseAddress + (uint)sourceOffset;
            var pageIndex = address >> PageShift;
            var pageOffset = (int)(address & PageMask);
            var copyLength = Math.Min(PageSize - pageOffset, bytes.Length - sourceOffset);
            var page = GetOrCreatePage(pageIndex);

            Buffer.BlockCopy(bytes, sourceOffset, page, pageOffset, copyLength);
            sourceOffset += copyLength;
        }
    }

    private byte[] GetOrCreatePage(uint pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var existing))
        {
            return existing;
        }

        var projectedBytes = checked(((ulong)_pages.Count + 1UL) * PageSize);

        if (projectedBytes > _maxMappedBytes)
        {
            throw new InvalidOperationException(
                $"Mapped memory exceeds configured limit {_maxMappedBytes} bytes.");
        }

        var page = new byte[PageSize];
        _pages[pageIndex] = page;
        return page;
    }
}
