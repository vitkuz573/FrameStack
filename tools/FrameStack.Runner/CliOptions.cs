using System.CommandLine;

internal static class CliOptions
{
    internal static readonly Option<int> MemoryMb = new("--memory-mb")
    {
        Description = "Emulated RAM size in megabytes.",
        DefaultValueFactory = _ => 256,
    };
}
