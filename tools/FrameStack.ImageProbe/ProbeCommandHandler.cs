using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

internal sealed record ProbeCliInvocation(
    string ImagePath,
    long InstructionBudget,
    int MemoryMb,
    int TimelineSteps,
    Dictionary<string, uint> RegisterOverrides,
    ProbeCliOptions CliOptions);

internal static class ProbeCommandHandler
{
    private const int DefaultChunkBudget = 200_000_000;
    private const int DefaultTailLength = 64;
    private const int DefaultMaxHotSpots = 4096;
    private const int DefaultMaxMemoryAccessWatchEvents = 4096;
    private const int DefaultMaxInstructionTraceEvents = 4096;

    internal static bool TryParse(
        string[] args,
        out ProbeCliInvocation? invocation,
        out int exitCode)
    {
        var rootCommand = BuildRootCommand();

        if (args.Length == 0)
        {
            rootCommand.Parse("--help").Invoke();
            invocation = null;
            exitCode = 0;
            return false;
        }

        var parseResult = rootCommand.Parse(args);

        if (parseResult.Tokens.Any(token =>
                token.Type == TokenType.Option &&
                (string.Equals(token.Value, "--help", StringComparison.Ordinal) ||
                 string.Equals(token.Value, "-h", StringComparison.Ordinal))))
        {
            rootCommand.Parse("--help").Invoke();
            invocation = null;
            exitCode = 0;
            return false;
        }

        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.WriteLine($"CLI parse error: {error.Message}");
            }

            Console.WriteLine();
            rootCommand.Parse("--help").Invoke();
            invocation = null;
            exitCode = 1;
            return false;
        }

        invocation = Bind(parseResult);
        exitCode = 0;
        return true;
    }

    private static RootCommand BuildRootCommand()
    {
        var command = new RootCommand("FrameStack ImageProbe");
        command.Arguments.Add(CliArguments.ImagePath);
        command.Options.Add(CliOptions.InstructionBudget);
        command.Options.Add(CliOptions.MemoryMb);
        command.Options.Add(CliOptions.TimelineSteps);
        command.Options.Add(CliOptions.RegisterOverrides);
        command.Options.Add(CliOptions.CheckpointFilePath);
        command.Options.Add(CliOptions.SaveCheckpointFilePath);
        command.Options.Add(CliOptions.ReportJsonPath);
        command.Options.Add(CliOptions.CheckpointAtInstructions);
        command.Options.Add(CliOptions.CheckpointForceRebuild);
        command.Options.Add(CliOptions.ResumeHalted);
        command.Options.Add(CliOptions.ChunkBudget);
        command.Options.Add(CliOptions.MaxHotSpots);
        command.Options.Add(CliOptions.FullHotSpots);
        command.Options.Add(CliOptions.ProgressEveryInstructions);
        command.Options.Add(CliOptions.StopOnConsoleRepeatRules);
        command.Options.Add(CliOptions.ProfileNames);
        command.Options.Add(CliOptions.DisableNullProgramCounterRedirect);
        command.Options.Add(CliOptions.Disable8MbHighBitAlias);
        command.Options.Add(CliOptions.SupervisorReturnOverrides);
        command.Options.Add(CliOptions.SupervisorReturnCallerOverrides);
        command.Options.Add(CliOptions.SupervisorReturnSignatureOverrides);
        command.Options.Add(CliOptions.SupervisorReturnCallerHitOverrides);
        command.Options.Add(CliOptions.MemoryWriteOverrides);
        command.Options.Add(CliOptions.StopAtProgramCounter);
        command.Options.Add(CliOptions.StopAtProgramCounterHits);
        command.Options.Add(CliOptions.StopOnSupervisorService);
        command.Options.Add(CliOptions.StopOnSupervisorSignatures);
        command.Options.Add(CliOptions.TailLength);
        command.Options.Add(CliOptions.AdditionalInstructionWindows);
        command.Options.Add(CliOptions.WatchWordAddresses);
        command.Options.Add(CliOptions.DynamicWatchWordRequests);
        command.Options.Add(CliOptions.WatchWordEffectiveAddresses);
        command.Options.Add(CliOptions.StopOnWatchWordChangeAddresses);
        command.Options.Add(CliOptions.DynamicStopOnWatchWordChangeRequests);
        command.Options.Add(CliOptions.StopOnWatchWordChangeEffectiveAddresses);
        command.Options.Add(CliOptions.TraceWatch32Accesses);
        command.Options.Add(CliOptions.TraceWatch32AccessesMaxEvents);
        command.Options.Add(CliOptions.TraceWatch32ProgramCounterRanges);
        command.Options.Add(CliOptions.TraceInstructionProgramCounterRanges);
        command.Options.Add(CliOptions.TraceInstructionMaxEvents);
        command.Options.Add(CliOptions.TrackedProgramCounters);
        command.Options.Add(CliOptions.NamedGlobalAddresses);
        command.Options.Add(CliOptions.NamedGlobalEffectiveAddresses);

        return command;
    }

    private static ProbeCliInvocation Bind(ParseResult parseResult)
    {
        var imagePath = parseResult.GetValue(CliArguments.ImagePath)
            ?? throw new InvalidOperationException("Missing required image path argument.");
        var instructionBudget = EnsurePositive(
            "--instruction-budget",
            parseResult.GetValue(CliOptions.InstructionBudget));
        var memoryMb = EnsurePositiveInt(
            "--memory-mb",
            parseResult.GetValue(CliOptions.MemoryMb));
        var timelineSteps = EnsureNonNegativeInt(
            "--timeline-steps",
            parseResult.GetValue(CliOptions.TimelineSteps));
        var registerOverrideTokens = (parseResult.GetValue(CliOptions.RegisterOverrides) ?? [])
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

        return new ProbeCliInvocation(
            imagePath,
            instructionBudget,
            memoryMb,
            timelineSteps,
            ParseRegisterOverrides(registerOverrideTokens),
            BuildCliOptions(parseResult, registerOverrideTokens));
    }

    private static ProbeCliOptions BuildCliOptions(
        ParseResult parseResult,
        string[] registerOverrideTokens)
    {
        var chunkBudgetValue = parseResult.GetValue(CliOptions.ChunkBudget);
        var chunkBudget = chunkBudgetValue.HasValue
            ? EnsurePositiveInt("--chunk-budget", chunkBudgetValue.Value)
            : DefaultChunkBudget;

        var maxHotSpots = parseResult.GetValue(CliOptions.FullHotSpots)
            ? int.MaxValue
            : parseResult.GetValue(CliOptions.MaxHotSpots) is int maxHotSpotsValue
                ? EnsureNonNegativeInt("--max-hotspots", maxHotSpotsValue)
                : DefaultMaxHotSpots;

        var progressEvery = parseResult.GetValue(CliOptions.ProgressEveryInstructions);
        var progressEveryInstructions = progressEvery.HasValue
            ? EnsurePositive("--progress-every", progressEvery.Value)
            : 0;

        var stopOnConsoleRepeatRules = new List<ConsoleRepeatStopRule>();
        foreach (var token in parseResult.GetValue(CliOptions.StopOnConsoleRepeatRules) ?? [])
        {
            AddDistinct(
                stopOnConsoleRepeatRules,
                ParseConsoleRepeatStopRule("--stop-on-console-repeat", token));
        }

        var stopAtProgramCounter = ParseOptionalUInt32(
            parseResult.GetValue(CliOptions.StopAtProgramCounter));
        var stopOnSupervisorService = ParseOptionalUInt32(
            parseResult.GetValue(CliOptions.StopOnSupervisorService));
        var stopOnSupervisorSignatures = new HashSet<SupervisorCallSignatureKey>();
        foreach (var token in parseResult.GetValue(CliOptions.StopOnSupervisorSignatures) ?? [])
        {
            AddDistinct(
                stopOnSupervisorSignatures,
                ParseSupervisorCallSignatureKey("--stop-on-svc-signature", token));
        }

        var stopAtProgramCounterHits = new Dictionary<uint, long>();
        foreach (var token in parseResult.GetValue(CliOptions.StopAtProgramCounterHits) ?? [])
        {
            var parsed = ParseProgramCounterHitStop("--stop-at-pc-hit", token);
            stopAtProgramCounterHits[parsed.ProgramCounter] = parsed.RequiredHits;
        }

        var tailLengthValue = parseResult.GetValue(CliOptions.TailLength);
        var tailLength = tailLengthValue.HasValue
            ? EnsureNonNegativeInt("--tail-length", tailLengthValue.Value)
            : DefaultTailLength;

        var supervisorReturnOverrides = new Dictionary<uint, uint>();
        foreach (var token in parseResult.GetValue(CliOptions.SupervisorReturnOverrides) ?? [])
        {
            var parsed = ParseSupervisorReturnOverride(token);
            supervisorReturnOverrides[parsed.ServiceCode] = parsed.ReturnValue;
        }

        var supervisorReturnCallerOverrides = new Dictionary<SupervisorCallsiteKey, uint>();
        foreach (var token in parseResult.GetValue(CliOptions.SupervisorReturnCallerOverrides) ?? [])
        {
            var parsed = ParseSupervisorReturnCallerOverride(token);
            supervisorReturnCallerOverrides[parsed.Callsite] = parsed.ReturnValue;
        }

        var supervisorReturnSignatureOverrides = new Dictionary<SupervisorCallSignatureKey, uint>();
        foreach (var token in parseResult.GetValue(CliOptions.SupervisorReturnSignatureOverrides) ?? [])
        {
            var parsed = ParseSupervisorReturnSignatureOverride(token);
            supervisorReturnSignatureOverrides[parsed.Signature] = parsed.ReturnValue;
        }

        var supervisorReturnCallerHitOverrides = new Dictionary<SupervisorCallsiteHitKey, uint>();
        foreach (var token in parseResult.GetValue(CliOptions.SupervisorReturnCallerHitOverrides) ?? [])
        {
            var parsed = ParseSupervisorReturnCallerHitOverride(token);
            supervisorReturnCallerHitOverrides[parsed.CallsiteHit] = parsed.ReturnValue;
        }

        var memoryWriteOverrides = new Dictionary<uint, uint>();
        foreach (var token in parseResult.GetValue(CliOptions.MemoryWriteOverrides) ?? [])
        {
            var parsed = ParseSupervisorReturnOverride(token);
            memoryWriteOverrides[parsed.ServiceCode] = parsed.ReturnValue;
        }

        var additionalInstructionWindows = new List<InstructionWindowRequest>();
        foreach (var token in parseResult.GetValue(CliOptions.AdditionalInstructionWindows) ?? [])
        {
            AddDistinct(additionalInstructionWindows, ParseInstructionWindowRequest(token));
        }

        var watchWordAddresses = new List<uint>();
        foreach (var token in parseResult.GetValue(CliOptions.WatchWordAddresses) ?? [])
        {
            AddDistinct(watchWordAddresses, ParseUInt32Flexible(token));
        }

        var dynamicWatchWordRequests = new List<DynamicWatchWordRequest>();
        foreach (var token in parseResult.GetValue(CliOptions.DynamicWatchWordRequests) ?? [])
        {
            AddDistinct(dynamicWatchWordRequests, ParseDynamicWatchWordRequest("--watch32-reg", token));
        }

        var watchWordEffectiveAddresses = new List<uint>();
        foreach (var token in parseResult.GetValue(CliOptions.WatchWordEffectiveAddresses) ?? [])
        {
            AddDistinct(watchWordEffectiveAddresses, ParseUInt32Flexible(token));
        }

        var stopOnWatchWordChangeAddresses = new HashSet<uint>();
        foreach (var token in parseResult.GetValue(CliOptions.StopOnWatchWordChangeAddresses) ?? [])
        {
            stopOnWatchWordChangeAddresses.Add(ParseUInt32Flexible(token));
        }

        var dynamicStopOnWatchWordChangeRequests = new List<DynamicWatchWordRequest>();
        foreach (var token in parseResult.GetValue(CliOptions.DynamicStopOnWatchWordChangeRequests) ?? [])
        {
            AddDistinct(
                dynamicStopOnWatchWordChangeRequests,
                ParseDynamicWatchWordRequest("--stop-on-watch32-change-reg", token));
        }

        var stopOnWatchWordChangeEffectiveAddresses = new HashSet<uint>();
        foreach (var token in parseResult.GetValue(CliOptions.StopOnWatchWordChangeEffectiveAddresses) ?? [])
        {
            stopOnWatchWordChangeEffectiveAddresses.Add(ParseUInt32Flexible(token));
        }

        var traceWatch32AccessesMaxEventsValue = parseResult.GetValue(CliOptions.TraceWatch32AccessesMaxEvents);
        var traceWatch32AccessesMaxEvents = traceWatch32AccessesMaxEventsValue.HasValue
            ? EnsureNonNegativeInt("--trace-watch32-accesses-max", traceWatch32AccessesMaxEventsValue.Value)
            : DefaultMaxMemoryAccessWatchEvents;

        var traceWatch32ProgramCounterRanges = new List<AddressRange>();
        foreach (var token in parseResult.GetValue(CliOptions.TraceWatch32ProgramCounterRanges) ?? [])
        {
            AddDistinct(
                traceWatch32ProgramCounterRanges,
                ParseAddressRange("--trace-watch32-pc-range", token));
        }

        var traceInstructionProgramCounterRanges = new List<AddressRange>();
        foreach (var token in parseResult.GetValue(CliOptions.TraceInstructionProgramCounterRanges) ?? [])
        {
            AddDistinct(
                traceInstructionProgramCounterRanges,
                ParseAddressRange("--trace-insn-pc-range", token));
        }

        var traceInstructionMaxEventsValue = parseResult.GetValue(CliOptions.TraceInstructionMaxEvents);
        var traceInstructionMaxEvents = traceInstructionMaxEventsValue.HasValue
            ? EnsureNonNegativeInt("--trace-insn-max", traceInstructionMaxEventsValue.Value)
            : DefaultMaxInstructionTraceEvents;

        var trackedProgramCounters = new List<uint>();
        foreach (var token in parseResult.GetValue(CliOptions.TrackedProgramCounters) ?? [])
        {
            AddDistinct(trackedProgramCounters, ParseUInt32Flexible(token));
        }

        var namedGlobalAddresses = new List<NamedAddress>();
        foreach (var token in parseResult.GetValue(CliOptions.NamedGlobalAddresses) ?? [])
        {
            AddDistinct(namedGlobalAddresses, ParseNamedAddress("--global32", token));
        }

        var namedGlobalEffectiveAddresses = new List<NamedAddress>();
        foreach (var token in parseResult.GetValue(CliOptions.NamedGlobalEffectiveAddresses) ?? [])
        {
            AddDistinct(namedGlobalEffectiveAddresses, ParseNamedAddress("--global32-ea", token));
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileName in parseResult.GetValue(CliOptions.ProfileNames) ?? [])
        {
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                profileNames.Add(profileName);
            }
        }

        ApplyProbeProfiles(
            profileNames,
            additionalInstructionWindows,
            watchWordAddresses,
            trackedProgramCounters,
            namedGlobalAddresses,
            dynamicWatchWordRequests);

        return new ProbeCliOptions(
            parseResult.GetValue(CliOptions.CheckpointFilePath),
            parseResult.GetValue(CliOptions.SaveCheckpointFilePath),
            parseResult.GetValue(CliOptions.ReportJsonPath),
            parseResult.GetValue(CliOptions.CheckpointAtInstructions),
            parseResult.GetValue(CliOptions.CheckpointForceRebuild),
            parseResult.GetValue(CliOptions.ResumeHalted),
            chunkBudget,
            maxHotSpots,
            progressEveryInstructions,
            stopOnConsoleRepeatRules,
            stopAtProgramCounter,
            stopAtProgramCounterHits,
            stopOnSupervisorService,
            stopOnSupervisorSignatures,
            tailLength,
            supervisorReturnOverrides,
            supervisorReturnCallerOverrides,
            supervisorReturnSignatureOverrides,
            supervisorReturnCallerHitOverrides,
            memoryWriteOverrides,
            additionalInstructionWindows,
            watchWordAddresses,
            dynamicWatchWordRequests,
            watchWordEffectiveAddresses,
            stopOnWatchWordChangeAddresses.ToArray(),
            dynamicStopOnWatchWordChangeRequests,
            stopOnWatchWordChangeEffectiveAddresses.ToArray(),
            parseResult.GetValue(CliOptions.TraceWatch32Accesses),
            traceWatch32AccessesMaxEvents,
            traceWatch32ProgramCounterRanges,
            traceInstructionProgramCounterRanges,
            traceInstructionMaxEvents,
            trackedProgramCounters,
            namedGlobalAddresses,
            namedGlobalEffectiveAddresses,
            profileNames.ToArray(),
            parseResult.GetValue(CliOptions.DisableNullProgramCounterRedirect),
            parseResult.GetValue(CliOptions.Disable8MbHighBitAlias),
            registerOverrideTokens);
    }

    private static void ApplyProbeProfiles(
        IReadOnlySet<string> profileNames,
        IList<InstructionWindowRequest> additionalInstructionWindows,
        IList<uint> watchWordAddresses,
        IList<uint> trackedProgramCounters,
        IList<NamedAddress> namedGlobalAddresses,
        IList<DynamicWatchWordRequest> dynamicWatchWordRequests)
    {
        foreach (var profileNameRaw in profileNames)
        {
            var profileName = profileNameRaw.Trim();

            if (profileName.Equals("c2600-ram-probe", StringComparison.OrdinalIgnoreCase) ||
                profileName.Equals("c2600-boot-probe", StringComparison.OrdinalIgnoreCase) ||
                profileName.Equals("cisco-c2600-boot", StringComparison.OrdinalIgnoreCase))
            {
                const uint baseGlobal = 0x82F40774;

                for (var index = 0; index <= 11; index++)
                {
                    AddDistinct(watchWordAddresses, unchecked(baseGlobal + (uint)(index * 4)));
                }

                for (var offset = 0x774; offset <= 0x7A0; offset += 4)
                {
                    AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(9, offset));
                }

                AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(10, 0x77C));
                AddDistinct(dynamicWatchWordRequests, new DynamicWatchWordRequest(10, 0x780));

                var descriptorWatchAddresses = new uint[]
                {
                    0x82F406A8,
                    0x82F406AC,
                    0x82F406B0,
                    0x82F406B4,
                    0x82F406B8,
                    0x82F406BC,
                    0x82F406C0,
                    0x82F406C4,
                    0x82F406C8,
                    0x82F406CC,
                };

                foreach (var address in descriptorWatchAddresses)
                {
                    AddDistinct(watchWordAddresses, address);
                }

                var trackedPcs = new uint[]
                {
                    0x816E2928,
                    0x816E292C,
                    0x816E29BC,
                    0x816E2DD4,
                    0x816E2F70,
                };

                foreach (var trackedPc in trackedPcs)
                {
                    AddDistinct(trackedProgramCounters, trackedPc);
                }

                AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E2928, 12, 12));
                AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E29BC, 8, 8));
                AddDistinct(additionalInstructionWindows, new InstructionWindowRequest(0x816E2DD4, 8, 8));

                var c2600Globals = new NamedAddress[]
                {
                    new("g_0x8000BCEC", 0x8000BCEC),
                    new("g_0x8000BCF0", 0x8000BCF0),
                    new("g_0x8000BCF4", 0x8000BCF4),
                    new("g_0x8000BCF8", 0x8000BCF8),
                    new("g_0x8000BCFC", 0x8000BCFC),
                    new("g_0x8000BD00", 0x8000BD00),
                    new("g_0x8000BD04", 0x8000BD04),
                    new("g_0x8000BD4C", 0x8000BD4C),
                    new("g_0x8000BD50", 0x8000BD50),
                    new("g_0x8000BD54", 0x8000BD54),
                    new("g_0x80090780", 0x80090780),
                    new("g_0x80090784", 0x80090784),
                    new("g_0x82F40774", 0x82F40774),
                    new("g_0x82F40778", 0x82F40778),
                    new("g_0x82F4077C", 0x82F4077C),
                    new("g_0x82F40780", 0x82F40780),
                    new("g_0x82F40784", 0x82F40784),
                    new("g_0x82F40788", 0x82F40788),
                    new("g_0x82F4078C", 0x82F4078C),
                    new("g_0x82F40790", 0x82F40790),
                    new("g_0x82F40794", 0x82F40794),
                    new("g_0x82F40798", 0x82F40798),
                    new("g_0x82F4079C", 0x82F4079C),
                    new("g_0x82F407A0", 0x82F407A0),
                };

                foreach (var global in c2600Globals)
                {
                    AddDistinct(namedGlobalAddresses, global);
                }

                continue;
            }

            throw new ArgumentException(
                $"Unsupported profile '{profileNameRaw}'. Supported profiles: c2600-ram-probe, c2600-boot-probe, cisco-c2600-boot.");
        }
    }

    private static uint? ParseOptionalUInt32(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return ParseUInt32Flexible(token);
    }

    private static long EnsurePositive(string optionName, long value)
    {
        if (value <= 0)
        {
            throw new ArgumentException($"Option '{optionName}' requires a positive integer value.");
        }

        return value;
    }

    private static int EnsurePositiveInt(string optionName, int value)
    {
        if (value <= 0)
        {
            throw new ArgumentException($"Option '{optionName}' requires a positive integer value.");
        }

        return value;
    }

    private static int EnsureNonNegativeInt(string optionName, int value)
    {
        if (value < 0)
        {
            throw new ArgumentException($"Option '{optionName}' requires a non-negative integer value.");
        }

        return value;
    }

    private static void AddDistinct<T>(ICollection<T> collection, T value)
    {
        if (!collection.Contains(value))
        {
            collection.Add(value);
        }
    }

    private static (uint ServiceCode, uint ReturnValue) ParseSupervisorReturnOverride(string token)
    {
        var separator = token.IndexOf('=');

        if (separator <= 0 || separator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor override '{token}'. Expected format '<service>=<value>'.");
        }

        var serviceCode = ParseUInt32Flexible(token[..separator]);
        var returnValue = ParseUInt32Flexible(token[(separator + 1)..]);

        return (serviceCode, returnValue);
    }

    private static (SupervisorCallsiteKey Callsite, uint ReturnValue) ParseSupervisorReturnCallerOverride(string token)
    {
        var equalsSeparator = token.IndexOf('=');

        if (equalsSeparator <= 0 || equalsSeparator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor caller override '{token}'. Expected format '<service>@<caller>=<value>'.");
        }

        var callsiteToken = token[..equalsSeparator];
        var returnValue = ParseUInt32Flexible(token[(equalsSeparator + 1)..]);
        var atSeparator = callsiteToken.IndexOf('@');

        if (atSeparator <= 0 || atSeparator == callsiteToken.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor caller override '{token}'. Expected format '<service>@<caller>=<value>'.");
        }

        var serviceCode = ParseUInt32Flexible(callsiteToken[..atSeparator]);
        var callerProgramCounter = ParseUInt32Flexible(callsiteToken[(atSeparator + 1)..]);
        return (new SupervisorCallsiteKey(serviceCode, callerProgramCounter), returnValue);
    }

    private static (SupervisorCallsiteHitKey CallsiteHit, uint ReturnValue) ParseSupervisorReturnCallerHitOverride(string token)
    {
        var equalsSeparator = token.IndexOf('=');

        if (equalsSeparator <= 0 || equalsSeparator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor caller-hit override '{token}'. Expected format '<service>@<caller>#<hit>=<value>'.");
        }

        var callsiteToken = token[..equalsSeparator];
        var returnValue = ParseUInt32Flexible(token[(equalsSeparator + 1)..]);
        var hashSeparator = callsiteToken.IndexOf('#');

        if (hashSeparator <= 0 || hashSeparator == callsiteToken.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor caller-hit override '{token}'. Expected format '<service>@<caller>#<hit>=<value>'.");
        }

        var callsitePart = callsiteToken[..hashSeparator];
        var hitToken = callsiteToken[(hashSeparator + 1)..];
        var callerOverride = ParseSupervisorReturnCallerOverride($"{callsitePart}=0");
        var hit = EnsurePositive("--svc-return-caller-hit", long.Parse(hitToken, CultureInfo.InvariantCulture));

        if (hit > int.MaxValue)
        {
            throw new ArgumentException("Option '--svc-return-caller-hit' requires hit count <= 2147483647.");
        }

        var key = new SupervisorCallsiteHitKey(
            callerOverride.Callsite.ServiceCode,
            callerOverride.Callsite.CallerProgramCounter,
            (int)hit);
        return (key, returnValue);
    }

    private static (SupervisorCallSignatureKey Signature, uint ReturnValue) ParseSupervisorReturnSignatureOverride(string token)
    {
        var equalsSeparator = token.IndexOf('=');

        if (equalsSeparator <= 0 || equalsSeparator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid supervisor signature override '{token}'. Expected format '<service>@<caller>/<a0>/<a1>/<a2>/<a3>=<value>'.");
        }

        var signatureToken = token[..equalsSeparator];
        var returnValue = ParseUInt32Flexible(token[(equalsSeparator + 1)..]);
        var signature = ParseSupervisorCallSignatureKey("--svc-return-signature", signatureToken);
        return (signature, returnValue);
    }

    private static SupervisorCallSignatureKey ParseSupervisorCallSignatureKey(string optionName, string token)
    {
        var atSeparator = token.IndexOf('@');

        if (atSeparator <= 0 || atSeparator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<service>@<caller>/<a0>/<a1>/<a2>/<a3>'.");
        }

        var serviceCode = ParseUInt32Flexible(token[..atSeparator]);
        var signatureTail = token[(atSeparator + 1)..];
        var slashParts = signatureTail.Split('/', StringSplitOptions.TrimEntries);

        if (slashParts.Length != 5)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<service>@<caller>/<a0>/<a1>/<a2>/<a3>'.");
        }

        var callerProgramCounter = ParseUInt32Flexible(slashParts[0]);
        var argument0 = ParseUInt32Flexible(slashParts[1]);
        var argument1 = ParseUInt32Flexible(slashParts[2]);
        var argument2 = ParseUInt32Flexible(slashParts[3]);
        var argument3 = ParseUInt32Flexible(slashParts[4]);

        return new SupervisorCallSignatureKey(
            serviceCode,
            callerProgramCounter,
            argument0,
            argument1,
            argument2,
            argument3);
    }

    private static (uint ProgramCounter, long RequiredHits) ParseProgramCounterHitStop(string optionName, string token)
    {
        var separator = token.IndexOf('=');

        if (separator <= 0 || separator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<address>=<hit-count>'.");
        }

        var programCounter = ParseUInt32Flexible(token[..separator]);
        var requiredHits = EnsurePositive(optionName, long.Parse(token[(separator + 1)..], CultureInfo.InvariantCulture));
        return (programCounter, requiredHits);
    }

    private static ConsoleRepeatStopRule ParseConsoleRepeatStopRule(string optionName, string token)
    {
        var separator = token.LastIndexOf('=');

        if (separator <= 0 || separator >= token.Length - 1)
        {
            throw new ArgumentException(
                $"Option '{optionName}' requires '<text>=<count>' format.");
        }

        var text = token[..separator];

        if (!long.TryParse(
                token[(separator + 1)..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var requiredHits) ||
            requiredHits <= 0)
        {
            throw new ArgumentException(
                $"Option '{optionName}' requires a positive hit count.");
        }

        return new ConsoleRepeatStopRule(text, requiredHits);
    }

    private static InstructionWindowRequest ParseInstructionWindowRequest(string token)
    {
        var parts = token.Split(':', StringSplitOptions.TrimEntries);

        if (parts.Length != 3)
        {
            throw new ArgumentException(
                $"Invalid window specification '{token}'. Expected '<address>:<before>:<after>'.");
        }

        var address = ParseUInt32Flexible(parts[0]);
        var before = EnsureNonNegativeInt("--window", int.Parse(parts[1], CultureInfo.InvariantCulture));
        var after = EnsureNonNegativeInt("--window", int.Parse(parts[2], CultureInfo.InvariantCulture));

        return new InstructionWindowRequest(address, before, after);
    }

    private static AddressRange ParseAddressRange(string optionName, string token)
    {
        var parts = token.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<start>:<end>'.");
        }

        var start = ParseUInt32Flexible(parts[0]);
        var end = ParseUInt32Flexible(parts[1]);

        if (end < start)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. End address must be greater than or equal to start address.");
        }

        return new AddressRange(start, end);
    }

    private static NamedAddress ParseNamedAddress(string optionName, string token)
    {
        var separator = token.IndexOf('=');

        if (separator <= 0 || separator == token.Length - 1)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<name>=<address>'.");
        }

        var name = token[..separator].Trim();

        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException($"Invalid '{optionName}' value '{token}'. Global name cannot be empty.");
        }

        var address = ParseUInt32Flexible(token[(separator + 1)..].Trim());
        return new NamedAddress(name, address);
    }

    private static DynamicWatchWordRequest ParseDynamicWatchWordRequest(string optionName, string token)
    {
        var parts = token.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            throw new ArgumentException(
                $"Invalid '{optionName}' value '{token}'. Expected format '<register>:<offset>'.");
        }

        var registerToken = parts[0];

        if (registerToken.Length < 2 ||
            (registerToken[0] != 'r' && registerToken[0] != 'R') ||
            !int.TryParse(registerToken[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var registerIndex) ||
            registerIndex is < 0 or > 31)
        {
            throw new ArgumentException(
                $"Invalid register '{registerToken}' in '{optionName}'. Expected r0..r31.");
        }

        var offset = ParseInt32Flexible(parts[1]);
        return new DynamicWatchWordRequest(registerIndex, offset);
    }

    private static uint ParseUInt32Flexible(string input)
    {
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.Parse(input[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return uint.Parse(input, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static int ParseInt32Flexible(string input)
    {
        var token = input.Trim();
        var negative = token.Length > 0 && token[0] == '-';
        var numericPart = negative ? token[1..] : token;

        if (numericPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = int.Parse(numericPart[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return negative ? -parsed : parsed;
        }

        return int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, uint> ParseRegisterOverrides(string[] tokens)
    {
        var overrides = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            var separator = token.IndexOf('=');

            if (separator <= 0 || separator == token.Length - 1)
            {
                throw new ArgumentException(
                    $"Invalid register override '{token}'. Expected format '<register>=<value>'.");
            }

            var registerName = token[..separator].Trim();
            var valueToken = token[(separator + 1)..].Trim();
            var value = ParseUInt32Flexible(valueToken);

            overrides[registerName] = value;
        }

        return overrides;
    }
}
