using FrameStack.Application.Images;

namespace FrameStack.Api.Contracts;

public sealed record ImageResponse(
    Guid Id,
    string Vendor,
    string Platform,
    string Name,
    string Version,
    string ArtifactPath,
    string? Sha256,
    long? DeclaredSizeBytes,
    DateTimeOffset RegisteredAtUtc)
{
    public static ImageResponse FromDto(ImageDto dto)
    {
        return new ImageResponse(
            dto.Id,
            dto.Vendor,
            dto.Platform,
            dto.Name,
            dto.Version,
            dto.ArtifactPath,
            dto.Sha256,
            dto.DeclaredSizeBytes,
            dto.RegisteredAtUtc);
    }
}
