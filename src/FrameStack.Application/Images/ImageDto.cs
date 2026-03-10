namespace FrameStack.Application.Images;

public sealed record ImageDto(
    Guid Id,
    string Vendor,
    string Platform,
    string Name,
    string Version,
    string ArtifactPath,
    string? Sha256,
    long? DeclaredSizeBytes,
    DateTimeOffset RegisteredAtUtc);
