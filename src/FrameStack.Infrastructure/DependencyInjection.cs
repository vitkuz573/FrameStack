using FrameStack.Application.Abstractions.Persistence;
using FrameStack.Application.Abstractions.Runtime;
using FrameStack.Application.Abstractions.Storage;
using FrameStack.Application.Abstractions.Time;
using FrameStack.Infrastructure.Persistence;
using FrameStack.Infrastructure.Runtime;
using FrameStack.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace FrameStack.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IImageRepository, InMemoryImageRepository>();
        services.AddSingleton<ISessionRepository, InMemorySessionRepository>();

        services.AddSingleton<IRuntimeOrchestrator, NativeRuntimeOrchestrator>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IUnitOfWork, NoOpUnitOfWork>();

        return services;
    }
}
