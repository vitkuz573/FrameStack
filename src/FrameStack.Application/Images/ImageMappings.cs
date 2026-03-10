using FrameStack.Domain.Images;

namespace FrameStack.Application.Images;

internal static class ImageMappings
{
    public static ImageDto ToDto(this EmulatorImage image)
    {
        return new ImageDto(
            image.Id.Value,
            image.Vendor,
            image.Platform.ToString(),
            image.Name,
            image.Version,
            image.ArtifactPath,
            image.Sha256,
            image.DeclaredSizeBytes,
            image.RegisteredAtUtc);
    }
}
