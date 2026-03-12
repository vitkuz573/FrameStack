using System.CommandLine;

internal static class CliOptions
{
    internal static readonly Option<long> InstructionBudget = new("--instruction-budget")
    {
        Description = "Instruction budget for the traced run.",
        DefaultValueFactory = _ => 2048L,
    };

    internal static readonly Option<int> MemoryMb = new("--memory-mb")
    {
        Description = "Emulated RAM size in megabytes.",
        DefaultValueFactory = _ => 256,
    };

    internal static readonly Option<int> TimelineSteps = new("--timeline-steps")
    {
        Description = "Number of timeline instructions to print before main run.",
        DefaultValueFactory = _ => 0,
    };

    internal static readonly Option<string[]> RegisterOverrides = CreateManyValueOption(
        "--register",
        "Register override token in form <reg>=<value>.");

    internal static readonly Option<string?> CheckpointFilePath = CreateSingleValueOption(
        "--checkpoint-file",
        "Checkpoint path for load/save bootstrap state.");

    internal static readonly Option<string?> SaveCheckpointFilePath = CreateSingleValueOption(
        "--save-checkpoint",
        "Save final checkpoint to path.");

    internal static readonly Option<string?> ReportJsonPath = CreateSingleValueOption(
        "--report-json",
        "Write JSON report to path.");

    internal static readonly Option<long?> CheckpointAtInstructions = new("--checkpoint-at")
    {
        Description = "Instruction threshold used when building checkpoint.",
    };

    internal static readonly Option<bool> CheckpointForceRebuild = new("--checkpoint-force-rebuild")
    {
        Description = "Force rebuild checkpoint even if file exists.",
    };

    internal static readonly Option<bool> ResumeHalted = new("--resume-halted")
    {
        Description = "Resume CPU if checkpoint was halted.",
    };

    internal static readonly Option<int?> ChunkBudget = new("--chunk-budget")
    {
        Description = "Instruction chunk budget for long runs.",
    };

    internal static readonly Option<int?> MaxHotSpots = new("--max-hotspots")
    {
        Description = "Maximum number of hotspots to keep.",
    };

    internal static readonly Option<bool> FullHotSpots = new("--full-hotspots")
    {
        Description = "Disable hotspot cap.",
    };

    internal static readonly Option<long?> ProgressEveryInstructions = new("--progress-every")
    {
        Description = "Print periodic run progress every N instructions.",
    };

    internal static readonly Option<string[]> StopOnConsoleRepeatRules = CreateManyValueOption(
        "--stop-on-console-repeat",
        "Console repeat stop rule <text>=<count>.");

    internal static readonly Option<string[]> ProfileNames = CreateManyValueOption(
        "--profile",
        "Probe profile name.");

    internal static readonly Option<bool> DisableNullProgramCounterRedirect = new("--disable-null-pc-redirect")
    {
        Description = "Disable null-PC redirect policy.",
    };

    internal static readonly Option<bool> Disable8MbHighBitAlias = new("--disable-8mb-high-bit-alias")
    {
        Description = "Disable MPC8xx 8MB high-bit alias in address translation.",
    };

    internal static readonly Option<string[]> SupervisorReturnOverrides = CreateManyValueOption(
        "--svc-return",
        "Supervisor override <service>=<value>.");

    internal static readonly Option<string[]> SupervisorReturnCallerOverrides = CreateManyValueOption(
        "--svc-return-caller",
        "Supervisor caller override <service>@<caller>=<value>.");

    internal static readonly Option<string[]> SupervisorReturnSignatureOverrides = CreateManyValueOption(
        "--svc-return-signature",
        "Supervisor signature override <service>@<caller>/<a0>/<a1>/<a2>/<a3>=<value>.");

    internal static readonly Option<string[]> SupervisorReturnCallerHitOverrides = CreateManyValueOption(
        "--svc-return-caller-hit",
        "Supervisor caller-hit override <service>@<caller>#<hit>=<value>.");

    internal static readonly Option<string[]> MemoryWriteOverrides = CreateManyValueOption(
        "--poke32",
        "Memory write override <address>=<value>.");

    internal static readonly Option<string?> StopAtProgramCounter = CreateSingleValueOption(
        "--stop-at-pc",
        "Stop when PC reaches address.");

    internal static readonly Option<string[]> StopAtProgramCounterHits = CreateManyValueOption(
        "--stop-at-pc-hit",
        "Stop when PC reaches hit count <address>=<hit-count>.");

    internal static readonly Option<string?> StopOnSupervisorService = CreateSingleValueOption(
        "--stop-on-svc",
        "Stop when specified supervisor service is called.");

    internal static readonly Option<string[]> StopOnSupervisorSignatures = CreateManyValueOption(
        "--stop-on-svc-signature",
        "Stop on supervisor signature <service>@<caller>/<a0>/<a1>/<a2>/<a3>.");

    internal static readonly Option<string[]> StopOnSupervisorSignatureHits = CreateManyValueOption(
        "--stop-on-svc-signature-hit",
        "Stop on supervisor signature hit <service>@<caller>/<a0>/<a1>/<a2>/<a3>#<hit-count>.");

    internal static readonly Option<int?> TailLength = new("--tail-length")
    {
        Description = "Program counter tail length in report.",
    };

    internal static readonly Option<string[]> AdditionalInstructionWindows = CreateManyValueOption(
        "--window",
        "Instruction window descriptor <address>:<before>:<after>.");

    internal static readonly Option<string[]> WatchWordAddresses = CreateManyValueOption(
        "--watch32",
        "Watch word address.");

    internal static readonly Option<string[]> DynamicWatchWordRequests = CreateManyValueOption(
        "--watch32-reg",
        "Watch word at <reg>:<offset>.");

    internal static readonly Option<string[]> WatchWordEffectiveAddresses = CreateManyValueOption(
        "--watch32-ea",
        "Watch word effective address.");

    internal static readonly Option<string[]> StopOnWatchWordChangeAddresses = CreateManyValueOption(
        "--stop-on-watch32-change",
        "Stop when watched word address changes.");

    internal static readonly Option<string[]> DynamicStopOnWatchWordChangeRequests = CreateManyValueOption(
        "--stop-on-watch32-change-reg",
        "Stop when watched word <reg>:<offset> changes.");

    internal static readonly Option<string[]> StopOnWatchWordChangeEffectiveAddresses = CreateManyValueOption(
        "--stop-on-watch32-change-ea",
        "Stop when watched word effective address changes.");

    internal static readonly Option<bool> TraceWatch32Accesses = new("--trace-watch32-accesses")
    {
        Description = "Trace accesses for watched words.",
    };

    internal static readonly Option<int?> TraceWatch32AccessesMaxEvents = new("--trace-watch32-accesses-max")
    {
        Description = "Maximum watch access events to keep.",
    };

    internal static readonly Option<string[]> TraceWatch32ProgramCounterRanges = CreateManyValueOption(
        "--trace-watch32-pc-range",
        "Access trace program counter range <start>:<end>.");

    internal static readonly Option<string[]> TraceInstructionProgramCounterRanges = CreateManyValueOption(
        "--trace-insn-pc-range",
        "Instruction trace program counter range <start>:<end>.");

    internal static readonly Option<int?> TraceInstructionMaxEvents = new("--trace-insn-max")
    {
        Description = "Maximum number of instruction trace events to keep.",
    };

    internal static readonly Option<string[]> TrackedProgramCounters = CreateManyValueOption(
        "--count-pc",
        "Track execution hits for program counter.");

    internal static readonly Option<string[]> NamedGlobalAddresses = CreateManyValueOption(
        "--global32",
        "Named global address <name>=<address>.");

    internal static readonly Option<string[]> NamedGlobalEffectiveAddresses = CreateManyValueOption(
        "--global32-ea",
        "Named global effective address <name>=<address>.");

    private static Option<string?> CreateSingleValueOption(string name, string description)
    {
        return new Option<string?>(name)
        {
            Description = description,
        };
    }

    private static Option<string[]> CreateManyValueOption(string name, string description)
    {
        return new Option<string[]>(name)
        {
            Description = description,
            Arity = ArgumentArity.ZeroOrMore,
        };
    }
}
