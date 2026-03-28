namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint CiscoC2600HardwarePlatformId = 0x2B;
    private const uint CiscoC2600NvramSizeWordAddress = 0x830E_04D0;
    private const uint CiscoC2600NvramSizeCachedWordAddress = 0x830E_045C;
    private const uint CiscoC2600DefaultNvramSizeBytes = 0x0000_2000;
    private const uint CiscoC2600DeferredNvramSeedCallerPc = 0x8100_0000;
    private const uint QueryInstalledRamService = 0x04;
    private const uint QueryHardwareProfileService = 0x07;
    private const uint BootstrapIoMemoryProfileService = 0x2C;
    private const uint QueryIoMemoryProfileService = 0x3A;
    private const uint SetIoMemoryProfileService = 0x3B;
    private const uint ReadIoMemoryProfileService = 0x3C;
    private const uint StageIoMemoryProfileService = 0x3D;
    private const uint CommitIoMemoryProfileService = 0x3E;
    private const uint IoMemoryProfileEncodingBase = 100;
    private const uint DefaultReportedMemoryBytes = 64u * 1024u * 1024u;
    private const uint DefaultIoMemoryProfilePercent = 20;
    private const uint MaxIoMemoryProfilePercent = 25;
    private const uint IoMemoryDescriptorTimingClassWord = 0x0000_8000;
    private const uint IoMemoryDescriptorAdapterCodeWord = 0x0091_0091;
    private const uint IoMemorySizingProbeBytes = 0x0040_0000;

    private readonly uint _reportedMemoryBytes;
    private readonly uint _defaultIoMemoryProfilePercent;
    private readonly uint _hardwarePlatformId;
    private uint _manualIoMemoryProfilePercent;
    private bool _hasManualIoMemoryProfile;
    private bool _ciscoC2600NvramSizeWordsSeeded;

    public DefaultPowerPcSupervisorCallHandler(
        uint reportedMemoryBytes = DefaultReportedMemoryBytes,
        uint defaultIoMemoryProfilePercent = DefaultIoMemoryProfilePercent,
        uint hardwarePlatformId = 0)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
        _defaultIoMemoryProfilePercent = NormalizeIoMemoryProfile(defaultIoMemoryProfilePercent);
        _hardwarePlatformId = hardwarePlatformId;
        _manualIoMemoryProfilePercent = _defaultIoMemoryProfilePercent;
        _hasManualIoMemoryProfile = false;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        TrySeedCiscoC2600NvramSizeWords(context);

        if (context.ServiceCode == QueryInstalledRamService)
        {
            if (IsSmartInitIoMemorySizingQuery(context))
            {
                return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
            }

            if (context.Argument1 == 0 &&
                context.Argument2 >= 0x8000_0000 &&
                context.Argument3 == 0)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: 0);
            }

            if (context.Argument1 == 0 &&
                context.Argument0 > 0 &&
                context.Argument0 <= _reportedMemoryBytes &&
                context.Argument2 == 0 &&
                context.Argument3 == 0)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: context.Argument0);
            }

            return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
        }

        if (context.ServiceCode == QueryHardwareProfileService)
        {
            if (_hardwarePlatformId != 0 &&
                context.Argument0 == 0 &&
                context.Argument1 == 0 &&
                context.Argument2 != 0 &&
                context.Argument3 != 0)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: _hardwarePlatformId);
            }

            return new PowerPcSupervisorCallResult(ReturnValue: 0);
        }

        if (context.ServiceCode == BootstrapIoMemoryProfileService)
        {
            var bootstrapProfile = ResolveIoMemoryProfileQueryResponse();
            SeedIoMemoryProfileBlock(context, bootstrapProfile);
            return new PowerPcSupervisorCallResult(ReturnValue: 0);
        }

        if (context.ServiceCode == QueryIoMemoryProfileService ||
            context.ServiceCode == ReadIoMemoryProfileService ||
            context.ServiceCode == CommitIoMemoryProfileService)
        {
            var queryResponse = ResolveIoMemoryProfileQueryResponse();
            SeedIoMemoryProfileBlock(context, queryResponse);
            return new PowerPcSupervisorCallResult(ReturnValue: queryResponse);
        }

        if (context.ServiceCode == SetIoMemoryProfileService ||
            context.ServiceCode == StageIoMemoryProfileService)
        {
            if (context.Argument0 != 0)
            {
                var normalizedProfile = NormalizeIoMemoryProfile(context.Argument0);

                if (normalizedProfile == 0)
                {
                    _manualIoMemoryProfilePercent = _defaultIoMemoryProfilePercent;
                    _hasManualIoMemoryProfile = false;
                    return new PowerPcSupervisorCallResult(ReturnValue: 0);
                }

                _manualIoMemoryProfilePercent = normalizedProfile;
                _hasManualIoMemoryProfile = true;
                return new PowerPcSupervisorCallResult(
                    ReturnValue: EncodeIoMemoryProfile(_manualIoMemoryProfilePercent));
            }

            // Zero requests keep Smart Init in auto mode while preserving Cisco's zero-ack contract.
            _manualIoMemoryProfilePercent = _defaultIoMemoryProfilePercent;
            _hasManualIoMemoryProfile = false;
            return new PowerPcSupervisorCallResult(ReturnValue: 0);
        }

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }

    private void TrySeedCiscoC2600NvramSizeWords(PowerPcSupervisorCallContext context)
    {
        if (_ciscoC2600NvramSizeWordsSeeded ||
            _hardwarePlatformId != CiscoC2600HardwarePlatformId ||
            context.CallerProgramCounter < CiscoC2600DeferredNvramSeedCallerPc)
        {
            return;
        }

        var primaryWritten = context.TryWriteDataUInt32(
            CiscoC2600NvramSizeWordAddress,
            CiscoC2600DefaultNvramSizeBytes);
        var cachedWritten = context.TryWriteDataUInt32(
            CiscoC2600NvramSizeCachedWordAddress,
            CiscoC2600DefaultNvramSizeBytes);

        if (!primaryWritten || !cachedWritten)
        {
            return;
        }

        _ciscoC2600NvramSizeWordsSeeded =
            context.TryReadDataUInt32(CiscoC2600NvramSizeWordAddress, out var primaryWord) &&
            primaryWord == CiscoC2600DefaultNvramSizeBytes &&
            context.TryReadDataUInt32(CiscoC2600NvramSizeCachedWordAddress, out var cachedWord) &&
            cachedWord == CiscoC2600DefaultNvramSizeBytes;
    }

    private static bool IsSmartInitIoMemorySizingQuery(PowerPcSupervisorCallContext context)
    {
        return context.Argument0 == IoMemorySizingProbeBytes &&
               context.Argument1 == 0 &&
               context.Argument3 == 0 &&
               context.Argument2 >= 0x8000_0000;
    }

    private static void SeedIoMemoryProfileBlock(
        PowerPcSupervisorCallContext context,
        uint profileWord)
    {
        if (context.Argument0 == 0)
        {
            return;
        }

        // Cisco ROM wrappers pass a pointer-sized descriptor in A0.
        // Keeping the leading words deterministic avoids random stale-state reads.
        context.TryWriteDataUInt32(context.Argument0, profileWord);
        context.TryWriteDataUInt32(context.Argument0 + 4, 0);
        context.TryWriteDataUInt32(context.Argument0 + 8, IoMemoryDescriptorTimingClassWord);
        context.TryWriteDataUInt32(context.Argument0 + 12, IoMemoryDescriptorAdapterCodeWord);
    }

    private uint ResolveIoMemoryProfileQueryResponse()
    {
        var activePercent = _hasManualIoMemoryProfile
            ? _manualIoMemoryProfilePercent
            : _defaultIoMemoryProfilePercent;

        return EncodeIoMemoryProfile(activePercent);
    }

    private static uint EncodeIoMemoryProfile(uint ioMemoryProfilePercent)
    {
        if (ioMemoryProfilePercent == 0)
        {
            return 0;
        }

        return IoMemoryProfileEncodingBase + ioMemoryProfilePercent;
    }

    private static uint NormalizeIoMemoryProfile(uint rawValue)
    {
        if (rawValue > MaxIoMemoryProfilePercent)
        {
            return 0;
        }

        return rawValue;
    }
}
