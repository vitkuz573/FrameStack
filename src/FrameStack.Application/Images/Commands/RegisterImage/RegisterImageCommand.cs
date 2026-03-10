using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Images.Commands.RegisterImage;

public sealed record RegisterImageCommand(
    string Vendor,
    string Platform,
    string Name,
    string Version,
    string ArtifactPath,
    string? Sha256,
    long? DeclaredSizeBytes) : ICommand<ImageDto>;
