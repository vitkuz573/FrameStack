using System.Reflection;

internal interface IProbeProfile
{
    string Name { get; }

    void Apply(ProbeProfileContext context);
}

internal sealed class ProbeProfileContext
{
    private readonly IList<InstructionWindowRequest> _additionalInstructionWindows;
    private readonly IList<uint> _watchWordAddresses;
    private readonly IList<uint> _trackedProgramCounters;
    private readonly IList<NamedAddress> _namedGlobalAddresses;
    private readonly IList<DynamicWatchWordRequest> _dynamicWatchWordRequests;

    public ProbeProfileContext(
        IList<InstructionWindowRequest> additionalInstructionWindows,
        IList<uint> watchWordAddresses,
        IList<uint> trackedProgramCounters,
        IList<NamedAddress> namedGlobalAddresses,
        IList<DynamicWatchWordRequest> dynamicWatchWordRequests)
    {
        _additionalInstructionWindows = additionalInstructionWindows;
        _watchWordAddresses = watchWordAddresses;
        _trackedProgramCounters = trackedProgramCounters;
        _namedGlobalAddresses = namedGlobalAddresses;
        _dynamicWatchWordRequests = dynamicWatchWordRequests;
    }

    public void AddInstructionWindow(InstructionWindowRequest request) =>
        AddDistinct(_additionalInstructionWindows, request);

    public void AddWatchWordAddress(uint address) =>
        AddDistinct(_watchWordAddresses, address);

    public void AddTrackedProgramCounter(uint address) =>
        AddDistinct(_trackedProgramCounters, address);

    public void AddNamedGlobalAddress(NamedAddress namedAddress) =>
        AddDistinct(_namedGlobalAddresses, namedAddress);

    public void AddDynamicWatchWordRequest(DynamicWatchWordRequest request) =>
        AddDistinct(_dynamicWatchWordRequests, request);

    private static void AddDistinct<T>(ICollection<T> collection, T value)
    {
        if (!collection.Contains(value))
        {
            collection.Add(value);
        }
    }
}

internal static class ProbeProfileCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, IProbeProfile>> ProfilesByName =
        new(DiscoverProfiles);

    internal static IReadOnlyList<string> SupportedProfileNames =>
        ProfilesByName.Value.Keys
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static void ApplyProfiles(
        IReadOnlySet<string> profileNames,
        ProbeProfileContext context)
    {
        foreach (var profileNameRaw in profileNames)
        {
            var profileName = profileNameRaw.Trim();

            if (string.IsNullOrWhiteSpace(profileName))
            {
                continue;
            }

            if (!ProfilesByName.Value.TryGetValue(profileName, out var profile))
            {
                throw new ArgumentException(
                    $"Unsupported profile '{profileNameRaw}'. Supported profiles: {string.Join(", ", SupportedProfileNames)}.");
            }

            profile.Apply(context);
        }
    }

    private static Dictionary<string, IProbeProfile> DiscoverProfiles()
    {
        var profileType = typeof(IProbeProfile);
        var profileMap = new Dictionary<string, IProbeProfile>(StringComparer.OrdinalIgnoreCase);

        var profileTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(type => profileType.IsAssignableFrom(type) &&
                           type is { IsInterface: false, IsAbstract: false });

        foreach (var profileDefinitionType in profileTypes)
        {
            if (Activator.CreateInstance(profileDefinitionType) is not IProbeProfile profileDefinition)
            {
                throw new InvalidOperationException(
                    $"Failed to instantiate probe profile type '{profileDefinitionType.FullName}'.");
            }

            if (string.IsNullOrWhiteSpace(profileDefinition.Name))
            {
                throw new InvalidOperationException(
                    $"Probe profile type '{profileDefinitionType.FullName}' returned an empty name.");
            }

            if (!profileMap.TryAdd(profileDefinition.Name, profileDefinition))
            {
                throw new InvalidOperationException(
                    $"Duplicate probe profile name '{profileDefinition.Name}'.");
            }
        }

        return profileMap;
    }
}

internal static class CiscoC2600ProbeProfileData
{
    internal static void ApplyBootBase(ProbeProfileContext context)
    {
        var trackedProgramCounters = new uint[]
        {
            0x816E2928,
            0x816E292C,
            0x816E29BC,
            0x816E2DD4,
            0x816E2F70,
        };

        foreach (var address in trackedProgramCounters)
        {
            context.AddTrackedProgramCounter(address);
        }

        context.AddInstructionWindow(new InstructionWindowRequest(0x816E2928, 12, 12));
        context.AddInstructionWindow(new InstructionWindowRequest(0x816E29BC, 8, 8));
        context.AddInstructionWindow(new InstructionWindowRequest(0x816E2DD4, 8, 8));

        var globalAddresses = new NamedAddress[]
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

        foreach (var globalAddress in globalAddresses)
        {
            context.AddNamedGlobalAddress(globalAddress);
        }
    }

    internal static void ApplyBootWatch(ProbeProfileContext context)
    {
        const uint baseGlobal = 0x82F40774;

        for (var index = 0; index <= 11; index++)
        {
            context.AddWatchWordAddress(unchecked(baseGlobal + (uint)(index * 4)));
        }

        for (var offset = 0x774; offset <= 0x7A0; offset += 4)
        {
            context.AddDynamicWatchWordRequest(new DynamicWatchWordRequest(9, offset));
        }

        context.AddDynamicWatchWordRequest(new DynamicWatchWordRequest(10, 0x77C));
        context.AddDynamicWatchWordRequest(new DynamicWatchWordRequest(10, 0x780));

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
            context.AddWatchWordAddress(address);
        }
    }
}

internal sealed class CiscoC2600BootProfile : IProbeProfile
{
    public string Name => "cisco-c2600-boot";

    public void Apply(ProbeProfileContext context)
    {
        CiscoC2600ProbeProfileData.ApplyBootBase(context);
    }
}

internal sealed class CiscoC2600BootWatchProfile : IProbeProfile
{
    public string Name => "cisco-c2600-boot-watch";

    public void Apply(ProbeProfileContext context)
    {
        CiscoC2600ProbeProfileData.ApplyBootBase(context);
        CiscoC2600ProbeProfileData.ApplyBootWatch(context);
    }
}
