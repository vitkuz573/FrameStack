using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Abstractions.Results;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Domain.Images;

namespace FrameStack.Application.Images.Queries.GetImageById;

public sealed class GetImageByIdQueryHandler(
    IImageRepository imageRepository)
    : IQueryHandler<GetImageByIdQuery, ImageDto>
{
    public async Task<Result<ImageDto>> Handle(GetImageByIdQuery query, CancellationToken cancellationToken)
    {
        var imageId = new ImageId(query.ImageId);
        var image = await imageRepository.GetByIdAsync(imageId, cancellationToken);

        if (image is null)
        {
            return Result.Failure<ImageDto>(ImageErrors.NotFound(query.ImageId));
        }

        return Result.Success<ImageDto>(image.ToDto());
    }
}
