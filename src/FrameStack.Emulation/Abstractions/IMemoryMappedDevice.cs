namespace FrameStack.Emulation.Abstractions;

public interface IMemoryMappedDevice
{
    uint BaseAddress { get; }

    uint SizeBytes { get; }

    byte ReadByte(uint offset);

    void WriteByte(uint offset, byte value);

    uint ReadUInt32(uint offset);

    void WriteUInt32(uint offset, uint value);
}
