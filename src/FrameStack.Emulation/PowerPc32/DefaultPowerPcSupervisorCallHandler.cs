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
    private const uint DefaultIoMemoryProfilePercent = 20;
    private const uint MaxIoMemoryProfileValue = 125;
    private const uint IoMemoryProfileOffsetEncodingBase = 100;
    private const uint IoMemoryDescriptorTimingClassWord = 0x0000_8000;

    private readonly uint _reportedMemoryBytes;
    private uint _manualIoMemoryProfilePercent;
    private bool _hasManualIoMemoryProfile;

    public DefaultPowerPcSupervisorCallHandler(
        uint reportedMemoryBytes = DefaultReportedMemoryBytes,
        uint defaultIoMemoryProfilePercent = DefaultIoMemoryProfilePercent)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
        _manualIoMemoryProfilePercent = NormalizeIoMemoryProfile(defaultIoMemoryProfilePercent);
        _hasManualIoMemoryProfile = false;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == QueryInstalledRamService)
        {
            if (context.Argument1 == 0 &&
                context.Argument0 > 0 &&
                context.Argument0 <= _reportedMemoryBytes)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: context.Argument0);
            }

            return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
        }

        if (context.ServiceCode == BootstrapIoMemoryProfileService)
        {
            SeedIoMemoryProfileBlock(context);
            return new PowerPcSupervisorCallResult(ReturnValue: 0);
        }

        if (context.ServiceCode == QueryIoMemoryProfileService ||
            context.ServiceCode == ReadIoMemoryProfileService ||
            context.ServiceCode == CommitIoMemoryProfileService)
        {
            return new PowerPcSupervisorCallResult(ReturnValue: ResolveIoMemoryProfileResponse());
        }

        if (context.ServiceCode == SetIoMemoryProfileService ||
            context.ServiceCode == StageIoMemoryProfileService)
        {
            if (context.Argument0 != 0)
            {
                _manualIoMemoryProfilePercent = NormalizeIoMemoryProfile(context.Argument0);
                _hasManualIoMemoryProfile = true;
            }
            else
            {
                // Zero requests keep Smart Init in auto mode.
                _hasManualIoMemoryProfile = false;
            }

            return new PowerPcSupervisorCallResult(ReturnValue: ResolveIoMemoryProfileResponse());
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
        context.TryWriteDataUInt32(context.Argument0, ResolveIoMemoryProfileResponse());
        context.TryWriteDataUInt32(context.Argument0 + 4, 0);
        context.TryWriteDataUInt32(context.Argument0 + 8, IoMemoryDescriptorTimingClassWord);
    }

    private uint ResolveIoMemoryProfileResponse()
    {
        return _hasManualIoMemoryProfile
            ? EncodeIoMemoryProfile(_manualIoMemoryProfilePercent)
            : 0u;
    }

    private static uint EncodeIoMemoryProfile(uint ioMemoryProfilePercent)
    {
        return ioMemoryProfilePercent + IoMemoryProfileOffsetEncodingBase;
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
