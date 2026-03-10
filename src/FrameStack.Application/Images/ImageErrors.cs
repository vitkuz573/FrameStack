using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Application.Images;

public static class ImageErrors
{
    public static readonly AppError InvalidVendor =
        new("images.vendor.invalid", "Vendor is required.");

    public static readonly AppError InvalidName =
        new("images.name.invalid", "Image name is required.");

    public static readonly AppError InvalidVersion =
        new("images.version.invalid", "Image version is required.");

    public static readonly AppError InvalidPlatform =
        new("images.platform.invalid", "Platform is invalid.");

    public static readonly AppError InvalidArtifactPath =
        new("images.artifact_path.invalid", "Artifact path is required and must be absolute.");

    public static AppError NotFound(Guid imageId) =>
        new("images.not_found", $"Image '{imageId}' was not found.");
}
