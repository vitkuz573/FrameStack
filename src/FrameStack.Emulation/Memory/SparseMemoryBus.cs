using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Memory;

public sealed class SparseMemoryBus : IMemoryBus, IMemoryWriteProtectionBus
{
    private const int PageSize = 4096;
    private const int PageShift = 12;
    private const uint PageMask = PageSize - 1;
    private const int AddressSpacePageCount = 1 << (32 - PageShift);

    private readonly ulong _maxMappedBytes;
    private readonly byte[]?[] _pages = new byte[AddressSpacePageCount][];
    private readonly List<WriteProtectedRange> _writeProtectedRanges = [];
    private int _privilegedWriteScopeDepth;
    private int _mappedPageCount;

    public ulong MaxMappedBytes => _maxMappedBytes;

    public SparseMemoryBus(ulong maxMappedBytes)
    {
        if (maxMappedBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMappedBytes), "Max mapped bytes must be greater than zero.");
        }

        _maxMappedBytes = maxMappedBytes;
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
        var pageIndex = (int)(address >> PageShift);
        var pageOffset = (int)(address & PageMask);
        var page = _pages[pageIndex];

        if (page is null)
        {
            return 0;
        }

        return page[pageOffset];
    }

    public void WriteByte(uint address, byte value)
    {
        if (IsWriteProtected(address, sizeof(byte)))
        {
            return;
        }

        var pageIndex = address >> PageShift;
        var pageOffset = (int)(address & PageMask);
        var page = GetOrCreatePage(pageIndex);

        page[pageOffset] = value;
    }

    public uint ReadUInt32(uint address)
    {
        var pageIndex = (int)(address >> PageShift);
        var pageOffset = (int)(address & PageMask);

        if (pageOffset <= PageSize - sizeof(uint))
        {
            var page = _pages[pageIndex];

            if (page is null)
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
        if (IsWriteProtected(address, sizeof(uint)))
        {
            return;
        }

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
        if (_writeProtectedRanges.Count > 0 &&
            _privilegedWriteScopeDepth == 0)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                WriteByte(baseAddress + (uint)index, bytes[index]);
            }

            return;
        }

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

    public IReadOnlyList<SparseMemoryPageSnapshot> CreateSnapshot()
    {
        var snapshots = new List<SparseMemoryPageSnapshot>(_mappedPageCount);

        for (var pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
        {
            var pageData = _pages[pageIndex];

            if (pageData is null)
            {
                continue;
            }

            var dataCopy = new byte[PageSize];
            Buffer.BlockCopy(pageData, 0, dataCopy, 0, PageSize);
            snapshots.Add(new SparseMemoryPageSnapshot((uint)pageIndex, dataCopy));
        }

        return snapshots;
    }

    public void RestoreSnapshot(IEnumerable<SparseMemoryPageSnapshot> pages)
    {
        Array.Clear(_pages, 0, _pages.Length);
        _mappedPageCount = 0;

        foreach (var page in pages)
        {
            if (page.Data.Length != PageSize)
            {
                throw new InvalidOperationException(
                    $"Sparse page {page.PageIndex} has invalid length {page.Data.Length}, expected {PageSize}.");
            }

            if (page.PageIndex >= AddressSpacePageCount)
            {
                throw new InvalidOperationException(
                    $"Sparse page index {page.PageIndex} exceeds 32-bit address space.");
            }

            var pageCopy = new byte[PageSize];
            Buffer.BlockCopy(page.Data, 0, pageCopy, 0, PageSize);
            var pageIndex = (int)page.PageIndex;

            if (_pages[pageIndex] is null)
            {
                _mappedPageCount++;
            }

            _pages[pageIndex] = pageCopy;
        }

        var mappedBytes = checked((ulong)_mappedPageCount * PageSize);

        if (mappedBytes > _maxMappedBytes)
        {
            throw new InvalidOperationException(
                $"Restored memory exceeds configured limit {_maxMappedBytes} bytes.");
        }
    }

    private byte[] GetOrCreatePage(uint pageIndex)
    {
        var pageIndexInt = (int)pageIndex;
        var existing = _pages[pageIndexInt];

        if (existing is not null)
        {
            return existing;
        }

        var projectedBytes = checked(((ulong)_mappedPageCount + 1UL) * PageSize);

        if (projectedBytes > _maxMappedBytes)
        {
            throw new InvalidOperationException(
                $"Mapped memory exceeds configured limit {_maxMappedBytes} bytes.");
        }

        var page = new byte[PageSize];
        _pages[pageIndexInt] = page;
        _mappedPageCount++;
        return page;
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

    private sealed class PrivilegedWriteScope(SparseMemoryBus owner) : IDisposable
    {
        private SparseMemoryBus? _owner = owner;

        public void Dispose()
        {
            _owner?.EndPrivilegedWriteScope();
            _owner = null;
        }
    }
}
