namespace FrameStack.Emulation.Abstractions;

public interface IMemoryWriteProtectionBus
{
    void ProtectWriteRange(uint baseAddress, uint sizeBytes);

    IDisposable BeginPrivilegedWriteScope();
}
