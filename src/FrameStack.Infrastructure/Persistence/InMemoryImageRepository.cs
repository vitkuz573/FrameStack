using System.Collections.Concurrent;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Domain.Images;

namespace FrameStack.Infrastructure.Persistence;

public sealed class InMemoryImageRepository : IImageRepository
{
    private readonly ConcurrentDictionary<ImageId, EmulatorImage> _storage = new();

    public Task AddAsync(EmulatorImage image, CancellationToken cancellationToken)
    {
        if (!_storage.TryAdd(image.Id, image))
        {
            throw new InvalidOperationException($"Image '{image.Id}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<EmulatorImage?> GetByIdAsync(ImageId id, CancellationToken cancellationToken)
    {
        _storage.TryGetValue(id, out var image);
        return Task.FromResult(image);
    }
}
