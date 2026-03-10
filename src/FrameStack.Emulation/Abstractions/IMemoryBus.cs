namespace FrameStack.Emulation.Abstractions;

public interface IMemoryBus
{
    byte ReadByte(uint address);

    void WriteByte(uint address, byte value);

    uint ReadUInt32(uint address);

    void WriteUInt32(uint address, uint value);

    void LoadBytes(uint baseAddress, byte[] bytes);
}
