using System.CommandLine;

internal static class CliArguments
{
    internal static readonly Argument<string> ImagePath = new("image-path")
    {
        Description = "Path to Cisco image file.",
    };
}
