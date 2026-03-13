using System.CommandLine;
using System.CommandLine.Parsing;

internal sealed record RunCliInvocation(
    string ImagePath,
    int MemoryMb,
    bool RunnerDebug,
    long? MaxInstructions,
    bool DisableDynarec);

internal static class RunCommandHandler
{
    internal static bool TryParse(
        string[] args,
        out RunCliInvocation? invocation,
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

        try
        {
            invocation = Bind(parseResult);
            exitCode = 0;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or OverflowException)
        {
            Console.WriteLine($"CLI validation error: {exception.Message}");
            Console.WriteLine();
            rootCommand.Parse("--help").Invoke();
            invocation = null;
            exitCode = 1;
            return false;
        }
    }

    private static RootCommand BuildRootCommand()
    {
        var command = new RootCommand("FrameStack runtime image runner");
        command.Arguments.Add(CliArguments.ImagePath);
        command.Options.Add(CliOptions.MemoryMb);
        command.Options.Add(CliOptions.RunnerDebug);
        command.Options.Add(CliOptions.MaxInstructions);
        command.Options.Add(CliOptions.DisableDynarec);
        return command;
    }

    private static RunCliInvocation Bind(ParseResult parseResult)
    {
        var imagePath = parseResult.GetValue(CliArguments.ImagePath)
            ?? throw new InvalidOperationException("Missing required image path argument.");
        var memoryMb = parseResult.GetValue(CliOptions.MemoryMb);

        if (memoryMb <= 0)
        {
            throw new ArgumentException("Option '--memory-mb' requires a positive integer value.");
        }

        var maxInstructions = parseResult.GetValue(CliOptions.MaxInstructions);

        if (maxInstructions.HasValue &&
            maxInstructions.Value <= 0)
        {
            throw new ArgumentException("Option '--max-instructions' requires a positive integer value.");
        }

        return new RunCliInvocation(
            imagePath,
            memoryMb,
            parseResult.GetValue(CliOptions.RunnerDebug),
            maxInstructions,
            parseResult.GetValue(CliOptions.DisableDynarec));
    }
}
