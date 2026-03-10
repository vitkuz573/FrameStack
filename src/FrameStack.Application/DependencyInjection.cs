using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Dispatching;
using FrameStack.Application.Images;
using FrameStack.Application.Images.Commands.RegisterImage;
using FrameStack.Application.Images.Queries.GetImageById;
using FrameStack.Application.Sessions;
using FrameStack.Application.Sessions.Commands.CreateSession;
using FrameStack.Application.Sessions.Commands.StartSession;
using FrameStack.Application.Sessions.Commands.StopSession;
using FrameStack.Application.Sessions.Queries.GetSessionById;
using Microsoft.Extensions.DependencyInjection;

namespace FrameStack.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        services.AddScoped<ICommandHandler<RegisterImageCommand, ImageDto>, RegisterImageCommandHandler>();
        services.AddScoped<IQueryHandler<GetImageByIdQuery, ImageDto>, GetImageByIdQueryHandler>();

        services.AddScoped<ICommandHandler<CreateSessionCommand, SessionDto>, CreateSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StartSessionCommand, SessionDto>, StartSessionCommandHandler>();
        services.AddScoped<ICommandHandler<StopSessionCommand, SessionDto>, StopSessionCommandHandler>();
        services.AddScoped<IQueryHandler<GetSessionByIdQuery, SessionDto>, GetSessionByIdQueryHandler>();

        return services;
    }
}
