using FrameStack.Emulation.Abstractions;

namespace FrameStack.Emulation.Memory;

public sealed class MemoryMappedBus : IMemoryBus, IMemoryWriteProtectionBus
{
    private readonly IMemoryBus _backingBus;
    private readonly List<DeviceMapping> _deviceMappings = [];

    public MemoryMappedBus(IMemoryBus backingBus)
    {
        _backingBus = backingBus ?? throw new ArgumentNullException(nameof(backingBus));
    }

    public IMemoryBus BackingBus => _backingBus;

    public void RegisterDevice(IMemoryMappedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.SizeBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(device), "Mapped device size must be greater than zero.");
        }

        var start = device.BaseAddress;
        var end = checked(device.BaseAddress + device.SizeBytes - 1);

        foreach (var mapping in _deviceMappings)
        {
            if ((ulong)end < mapping.Start ||
                start > mapping.End)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Mapped device range 0x{start:X8}..0x{end:X8} overlaps with existing range 0x{mapping.Start:X8}..0x{mapping.End:X8}.");
        }

        _deviceMappings.Add(new DeviceMapping(device, start, end));
        _deviceMappings.Sort(static (left, right) => left.Start.CompareTo(right.Start));
    }

    public void ProtectWriteRange(uint baseAddress, uint sizeBytes)
    {
        if (_backingBus is not IMemoryWriteProtectionBus protectionBus)
        {
            throw new NotSupportedException(
                $"Backing memory bus '{_backingBus.GetType().Name}' does not support write protection.");
        }

        protectionBus.ProtectWriteRange(baseAddress, sizeBytes);
    }

    public IDisposable BeginPrivilegedWriteScope()
    {
        if (_backingBus is not IMemoryWriteProtectionBus protectionBus)
        {
            throw new NotSupportedException(
                $"Backing memory bus '{_backingBus.GetType().Name}' does not support privileged write scope.");
        }

        return protectionBus.BeginPrivilegedWriteScope();
    }

    public byte ReadByte(uint address)
    {
        if (TryResolveDevice(address, sizeof(byte), out var mapping, out var offset))
        {
            return mapping.Device.ReadByte(offset);
        }

        return _backingBus.ReadByte(address);
    }

    public void WriteByte(uint address, byte value)
    {
        if (TryResolveDevice(address, sizeof(byte), out var mapping, out var offset))
        {
            mapping.Device.WriteByte(offset, value);
            return;
        }

        _backingBus.WriteByte(address, value);
    }

    public uint ReadUInt32(uint address)
    {
        if (_deviceMappings.Count == 0)
        {
            return _backingBus.ReadUInt32(address);
        }

        if (TryResolveDevice(address, sizeof(uint), out var mapping, out var offset))
        {
            return mapping.Device.ReadUInt32(offset);
        }

        if (!HasMappedDeviceOverlap(address, sizeof(uint)))
        {
            return _backingBus.ReadUInt32(address);
        }

        // Cross-device or edge-case reads fallback to byte-granular composition.
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
        if (_deviceMappings.Count == 0)
        {
            _backingBus.WriteUInt32(address, value);
            return;
        }

        if (TryResolveDevice(address, sizeof(uint), out var mapping, out var offset))
        {
            mapping.Device.WriteUInt32(offset, value);
            return;
        }

        if (!HasMappedDeviceOverlap(address, sizeof(uint)))
        {
            _backingBus.WriteUInt32(address, value);
            return;
        }

        // Cross-device or edge-case writes fallback to byte-granular stores.
        WriteByte(address, (byte)(value >> 24));
        WriteByte(address + 1, (byte)(value >> 16));
        WriteByte(address + 2, (byte)(value >> 8));
        WriteByte(address + 3, (byte)value);
    }

    public void LoadBytes(uint baseAddress, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            return;
        }

        if (!HasMappedDeviceOverlap(baseAddress, bytes.Length))
        {
            _backingBus.LoadBytes(baseAddress, bytes);
            return;
        }

        for (var index = 0; index < bytes.Length; index++)
        {
            WriteByte(baseAddress + (uint)index, bytes[index]);
        }
    }

    private bool TryResolveDevice(
        uint address,
        int sizeBytes,
        out DeviceMapping mapping,
        out uint offset)
    {
        foreach (var candidate in _deviceMappings)
        {
            if (address < candidate.Start ||
                address > candidate.End)
            {
                continue;
            }

            var end = (ulong)address + (ulong)sizeBytes - 1UL;

            if (end > candidate.End)
            {
                break;
            }

            mapping = candidate;
            offset = address - candidate.Start;
            return true;
        }

        mapping = default;
        offset = 0;
        return false;
    }

    private bool HasMappedDeviceOverlap(uint baseAddress, int lengthBytes)
    {
        var start = (ulong)baseAddress;
        var end = start + (ulong)lengthBytes - 1UL;

        foreach (var mapping in _deviceMappings)
        {
            if (end < mapping.Start ||
                start > mapping.End)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private readonly record struct DeviceMapping(
        IMemoryMappedDevice Device,
        uint Start,
        uint End);
}
