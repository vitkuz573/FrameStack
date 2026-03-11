namespace FrameStack.Emulation.PowerPc32;

public sealed class DefaultPowerPcSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint QueryInstalledRamService = 0x04;
    private const uint DefaultReportedMemoryBytes = 64u * 1024u * 1024u;
    private const uint MemoryProbeSizeArgument = 0x0040_0000;
    private const uint CiscoC2600SafeReportedMemoryCeiling = 0x0B00_0000;
    private const uint CiscoC2600ProbeHeadroomBytes = 0x0080_0000;

    private readonly uint _reportedMemoryBytes;
    private readonly bool _enableCiscoC2600SmartInitProfile;

    public DefaultPowerPcSupervisorCallHandler(
        uint reportedMemoryBytes = DefaultReportedMemoryBytes,
        bool enableCiscoC2600SmartInitProfile = false)
    {
        _reportedMemoryBytes = reportedMemoryBytes;
        _enableCiscoC2600SmartInitProfile = enableCiscoC2600SmartInitProfile;
    }

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        if (context.ServiceCode == QueryInstalledRamService)
        {
            if (!_enableCiscoC2600SmartInitProfile)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: _reportedMemoryBytes);
            }

            if (context.Argument1 == 0 &&
                context.Argument0 == MemoryProbeSizeArgument &&
                context.Argument0 <= _reportedMemoryBytes)
            {
                return new PowerPcSupervisorCallResult(ReturnValue: context.Argument0);
            }

            var reportedMemory = _reportedMemoryBytes > CiscoC2600SafeReportedMemoryCeiling
                ? CiscoC2600SafeReportedMemoryCeiling
                : _reportedMemoryBytes;

            if (reportedMemory > CiscoC2600ProbeHeadroomBytes)
            {
                reportedMemory -= CiscoC2600ProbeHeadroomBytes;
            }

            return new PowerPcSupervisorCallResult(ReturnValue: reportedMemory);
        }

        // Most firmware wrappers expect zero on success.
        // A richer syscall/exception model will replace this default behavior.
        return new PowerPcSupervisorCallResult(ReturnValue: 0);
    }
}
