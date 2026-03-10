using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Domain.Images;

namespace FrameStack.Application.Images.Commands.RegisterImage;

public sealed class RegisterImageCommandHandler(
    IImageRepository imageRepository,
    IUnitOfWork unitOfWork,
    IClock clock)
    : ICommandHandler<RegisterImageCommand, ImageDto>
{
    public async Task<Result<ImageDto>> Handle(
        RegisterImageCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Vendor))
        {
            return Result.Failure<ImageDto>(ImageErrors.InvalidVendor);
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result.Failure<ImageDto>(ImageErrors.InvalidName);
        }

        if (string.IsNullOrWhiteSpace(command.Version))
        {
            return Result.Failure<ImageDto>(ImageErrors.InvalidVersion);
        }

        if (!Enum.TryParse<ImagePlatform>(command.Platform, true, out var platform))
        {
            return Result.Failure<ImageDto>(ImageErrors.InvalidPlatform);
        }

        if (string.IsNullOrWhiteSpace(command.ArtifactPath) || !Path.IsPathRooted(command.ArtifactPath))
        {
            return Result.Failure<ImageDto>(ImageErrors.InvalidArtifactPath);
        }

        var image = EmulatorImage.Register(
            command.Vendor,
            platform,
            command.Name,
            command.Version,
            command.ArtifactPath,
            command.Sha256,
            command.DeclaredSizeBytes,
            clock.UtcNow);

        await imageRepository.AddAsync(image, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success<ImageDto>(image.ToDto());
    }
}
