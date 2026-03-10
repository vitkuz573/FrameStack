namespace FrameStack.Api.Contracts;

public sealed class RegisterImageRequest
{
    public required string Vendor { get; init; }

    public required string Platform { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ArtifactPath { get; init; }

    public string? Sha256 { get; init; }

    public long? DeclaredSizeBytes { get; init; }
}
