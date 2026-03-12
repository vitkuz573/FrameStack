using System.Text;

namespace FrameStack.Emulation.PowerPc32;

public sealed class PowerPcTracingSupervisorCallHandler : IPowerPcSupervisorCallHandler
{
    private const uint CiscoMonitorDispatcherService = 0x2B;
    private const uint PutCharacterService = 0x01;

    private readonly IPowerPcSupervisorCallHandler _innerHandler;
    private readonly int _maxTraceEntries;
    private readonly Dictionary<uint, long> _serviceCounters = new();
    private readonly Dictionary<PowerPcSupervisorSubcallKey, long> _subserviceCounters = new();
    private readonly StringBuilder _consoleOutput = new();
    private readonly List<PowerPcSupervisorCallTraceEntry> _callTrace = new();

    public PowerPcTracingSupervisorCallHandler(
        IPowerPcSupervisorCallHandler innerHandler,
        int maxTraceEntries = 4096)
    {
        _innerHandler = innerHandler
            ?? throw new ArgumentNullException(nameof(innerHandler));
        _maxTraceEntries = maxTraceEntries >= 0
            ? maxTraceEntries
            : throw new ArgumentOutOfRangeException(
                nameof(maxTraceEntries),
                "Maximum trace entries must be non-negative.");
    }

    public IReadOnlyDictionary<uint, long> ServiceCounters => _serviceCounters;

    public IReadOnlyDictionary<PowerPcSupervisorSubcallKey, long> SubserviceCounters => _subserviceCounters;

    public IReadOnlyList<PowerPcSupervisorCallTraceEntry> CallTrace => _callTrace;

    public string ConsoleOutput => _consoleOutput.ToString();

    public PowerPcSupervisorCallResult Handle(PowerPcSupervisorCallContext context)
    {
        IncrementCounter(_serviceCounters, context.ServiceCode);

        if (context.ServiceCode == CiscoMonitorDispatcherService)
        {
            var subcall = new PowerPcSupervisorSubcallKey(
                context.ServiceCode,
                context.Argument0);

            IncrementCounter(_subserviceCounters, subcall);
        }

        if (context.ServiceCode == PutCharacterService)
        {
            AppendConsoleCharacter(unchecked((byte)context.Argument0));
        }

        var result = _innerHandler.Handle(context);

        if (context.ServiceCode != PutCharacterService &&
            _maxTraceEntries > 0 &&
            _callTrace.Count < _maxTraceEntries)
        {
            _callTrace.Add(new PowerPcSupervisorCallTraceEntry(
                context.ProgramCounter,
                context.ServiceCode,
                context.Argument0,
                context.Argument1,
                context.Argument2,
                context.Argument3,
                context.LinkRegister,
                result.ReturnValue,
                result.Halt,
                result.NextProgramCounter));
        }

        return result;
    }

    private static void IncrementCounter<TKey>(IDictionary<TKey, long> dictionary, TKey key)
        where TKey : notnull
    {
        dictionary.TryGetValue(key, out var currentCount);
        dictionary[key] = currentCount + 1;
    }

    private void AppendConsoleCharacter(byte value)
    {
        switch (value)
        {
            case (byte)'\r':
            case (byte)'\n':
            case (byte)'\t':
                _consoleOutput.Append((char)value);
                return;
        }

        if (value is >= 0x20 and <= 0x7E)
        {
            _consoleOutput.Append((char)value);
        }
    }
}
