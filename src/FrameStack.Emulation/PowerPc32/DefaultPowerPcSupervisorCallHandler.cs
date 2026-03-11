namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint QueryInstalledRamService = 0x04;
    private const uint BootstrapIoMemoryProfileService = 0x2C;
    private const uint QueryIoMemoryProfileService = 0x3A;
    private const uint SetIoMemoryProfileService = 0x3B;
    private const uint ReadIoMemoryProfileService = 0x3C;
    private const uint StageIoMemoryProfileService = 0x3D;
    private const uint CommitIoMemoryProfileService = 0x3E;
    private const uint DefaultReportedMemoryBytes = 64u * 1024u * 1024u;
    private const uint MaxIoMemoryProfileValue = 125;
    private const uint IoMemoryProfileOffsetEncodingBase = 100;

    private readonly uint _reportedMemoryBytes;
    private uint _ioMemoryProfile;

    public DefaultPowerPcSupervisorCallHandler(
        uint reportedMemoryBytes = DefaultReportedMemoryBytes)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == QueryInstalledRamService)
        {
            return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
        }

        if (context.ServiceCode == BootstrapIoMemoryProfileService)
        {
            SeedIoMemoryProfileBlock(context);
            return new PowerPcSupervisorCallResult(ReturnValue: _ioMemoryProfile);
        }

        if (context.ServiceCode == QueryIoMemoryProfileService ||
            context.ServiceCode == ReadIoMemoryProfileService ||
            context.ServiceCode == CommitIoMemoryProfileService)
        {
            return new PowerPcSupervisorCallResult(ReturnValue: _ioMemoryProfile);
        }

        if (context.ServiceCode == SetIoMemoryProfileService ||
            context.ServiceCode == StageIoMemoryProfileService)
        {
            _ioMemoryProfile = NormalizeIoMemoryProfile(context.Argument0);
            return new PowerPcSupervisorCallResult(ReturnValue: _ioMemoryProfile);
        }

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }

    private void SeedIoMemoryProfileBlock(PowerPcSupervisorCallContext context)
    {
        if (context.Argument0 == 0)
        {
            return;
        }

        // Cisco ROM wrappers pass a pointer-sized descriptor in A0.
        // Keeping the leading words deterministic avoids random stale-state reads.
        context.TryWriteDataUInt32(context.Argument0, _ioMemoryProfile);
        context.TryWriteDataUInt32(context.Argument0 + 4, 0);
        context.TryWriteDataUInt32(context.Argument0 + 8, 0);
    }

    private static uint NormalizeIoMemoryProfile(uint rawValue)
    {
        if (rawValue >= IoMemoryProfileOffsetEncodingBase &&
            rawValue <= IoMemoryProfileOffsetEncodingBase + MaxIoMemoryProfileValue)
        {
            rawValue -= IoMemoryProfileOffsetEncodingBase;
        }

        if (rawValue > MaxIoMemoryProfileValue)
        {
            return 0;
        }

        return rawValue;
    }
}
