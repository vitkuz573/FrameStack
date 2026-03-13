using System.CommandLine;

internal static class CliOptions
{
    internal static readonly Option<int> MemoryMb = new("--memory-mb")
    {
        Description = "Emulated RAM size in megabytes.",
        DefaultValueFactory = _ => 256,
    };

    internal static readonly Option<bool> RunnerDebug = new("--runner-debug")
    {
        Description = "Enable runner diagnostic status output.",
    };

    internal static readonly Option<long?> MaxInstructions = new("--max-instructions")
    {
        Description = "Stop after executing the specified number of instructions.",
    };

    internal static readonly Option<bool> DisableDynarec = new("--disable-dynarec")
    {
        Description = "Disable PPC dynarec/JIT and use interpreter-only execution.",
    };
}
