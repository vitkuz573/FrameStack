using FrameStack.Emulation.Abstractions;
using FrameStack.Emulation.Memory;

namespace FrameStack.Emulation.UnitTests;

public sealed class MemoryMappedBusTests
{
    [Fact]
    public void ReadAndWriteShouldDispatchToMappedDeviceRange()
    {
        var backing = new SparseMemoryBus(maxMappedBytes: 16UL * 1024UL * 1024UL);
        var bus = new MemoryMappedBus(backing);
        var device = new TestMappedDevice(baseAddress: 0x1000, sizeBytes: 0x100);
        bus.RegisterDevice(device);

        bus.WriteByte(0x1000, 0xAA);
        bus.WriteUInt32(0x1004, 0x1122_3344);
        backing.WriteByte(0x2000, 0x5A);

        Assert.Equal(0u, backing.ReadByte(0x1000));
        Assert.Equal(0xAAu, bus.ReadByte(0x1000));
        Assert.Equal(0x1122_3344u, bus.ReadUInt32(0x1004));
        Assert.Equal(0x5Au, bus.ReadByte(0x2000));
        Assert.Equal(5, device.WriteByteCallCount);
        Assert.Equal(1, device.WriteUInt32CallCount);
    }

    [Fact]
    public void RegisterDeviceShouldRejectOverlappingRanges()
    {
        var backing = new SparseMemoryBus(maxMappedBytes: 16UL * 1024UL * 1024UL);
        var bus = new MemoryMappedBus(backing);
        bus.RegisterDevice(new TestMappedDevice(baseAddress: 0x3000, sizeBytes: 0x20));

        var exception = Assert.Throws<InvalidOperationException>(
            () => bus.RegisterDevice(new TestMappedDevice(baseAddress: 0x3010, sizeBytes: 0x20)));

        Assert.Contains("overlaps", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteProtectionShouldForwardToBackingBus()
    {
        var backing = new SparseMemoryBus(maxMappedBytes: 16UL * 1024UL * 1024UL);
        var bus = new MemoryMappedBus(backing);
        bus.ProtectWriteRange(0x5000, 0x10);

        bus.WriteUInt32(0x5000, 0xAABB_CCDD);
        Assert.Equal(0u, bus.ReadUInt32(0x5000));

        using (bus.BeginPrivilegedWriteScope())
        {
            bus.WriteUInt32(0x5000, 0xAABB_CCDD);
        }

        Assert.Equal(0xAABB_CCDDu, bus.ReadUInt32(0x5000));
    }

    private sealed class TestMappedDevice : IMemoryMappedDevice
    {
        private readonly Dictionary<uint, byte> _byteStorage = new();

        public TestMappedDevice(uint baseAddress, uint sizeBytes)
        {
            BaseAddress = baseAddress;
            SizeBytes = sizeBytes;
        }

        public uint BaseAddress { get; }

        public uint SizeBytes { get; }

        public int WriteByteCallCount { get; private set; }

        public int WriteUInt32CallCount { get; private set; }

        public byte ReadByte(uint offset)
        {
            return _byteStorage.GetValueOrDefault(offset);
        }

        public void WriteByte(uint offset, byte value)
        {
            WriteByteCallCount++;
            _byteStorage[offset] = value;
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
            WriteUInt32CallCount++;
            WriteByte(offset, (byte)(value >> 24));
            WriteByte(offset + 1, (byte)(value >> 16));
            WriteByte(offset + 2, (byte)(value >> 8));
            WriteByte(offset + 3, (byte)value);
        }
    }
}
