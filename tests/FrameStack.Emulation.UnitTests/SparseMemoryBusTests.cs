using FrameStack.Emulation.Memory;

namespace FrameStack.Emulation.UnitTests;

public sealed class SparseMemoryBusTests
{
    [Fact]
    public void RestoreSnapshotShouldReplaceCurrentPages()
    {
        var bus = new SparseMemoryBus(maxMappedBytes: 64 * 1024 * 1024);

        bus.WriteByte(0x1000, 0x11);
        bus.WriteByte(0x2000, 0x22);
        var snapshot = bus.CreateSnapshot();

        bus.WriteByte(0x1000, 0x33);
        bus.WriteByte(0x3000, 0x44);

        bus.RestoreSnapshot(snapshot);

        Assert.Equal(0x11u, bus.ReadByte(0x1000));
        Assert.Equal(0x22u, bus.ReadByte(0x2000));
        Assert.Equal(0u, bus.ReadByte(0x3000));
    }

    [Fact]
    public void RestoreSnapshotShouldRejectInvalidPageLength()
    {
        var bus = new SparseMemoryBus(maxMappedBytes: 64 * 1024 * 1024);
        var snapshot = new[]
        {
            new SparseMemoryPageSnapshot(1, new byte[128])
        };

        var exception = Assert.Throws<InvalidOperationException>(() => bus.RestoreSnapshot(snapshot));
        Assert.Contains("invalid length", exception.Message);
    }

    [Fact]
    public void WriteShouldBeIgnoredForProtectedRange()
    {
        var bus = new SparseMemoryBus(maxMappedBytes: 64 * 1024 * 1024);
        bus.WriteUInt32(0x2000, 0x1122_3344);
        bus.ProtectWriteRange(0x2000, 0x10);

        bus.WriteUInt32(0x2000, 0xAABB_CCDD);
        bus.WriteByte(0x2003, 0xEF);

        Assert.Equal(0x1122_3344u, bus.ReadUInt32(0x2000));
    }

    [Fact]
    public void PrivilegedWriteScopeShouldAllowWritesIntoProtectedRange()
    {
        var bus = new SparseMemoryBus(maxMappedBytes: 64 * 1024 * 1024);
        bus.ProtectWriteRange(0x3000, 0x10);

        using (bus.BeginPrivilegedWriteScope())
        {
            bus.WriteUInt32(0x3000, 0xDEAD_BEEF);
            bus.WriteByte(0x3003, 0xAA);
        }

        Assert.Equal(0xDEAD_BEAAu, bus.ReadUInt32(0x3000));
    }
}
