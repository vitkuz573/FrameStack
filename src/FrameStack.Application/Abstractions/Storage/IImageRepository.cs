using FrameStack.Domain.Images;

namespace FrameStack.Application.Abstractions.Storage;

public interface IImageRepository
{
    Task AddAsync(EmulatorImage image, CancellationToken cancellationToken);

    Task<EmulatorImage?> GetByIdAsync(ImageId id, CancellationToken cancellationToken);
}
