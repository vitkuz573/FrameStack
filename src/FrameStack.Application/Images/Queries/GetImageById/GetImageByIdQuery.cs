using FrameStack.Application.Abstractions.Messaging;

namespace FrameStack.Application.Images.Queries.GetImageById;

public sealed record GetImageByIdQuery(Guid ImageId) : IQuery<ImageDto>;
