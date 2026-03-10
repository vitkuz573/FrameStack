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
}
