using FrameStack.Api.Contracts;
using FrameStack.Api.Extensions;
using FrameStack.Application;
using FrameStack.Application.Abstractions.Messaging;
using FrameStack.Application.Images;
using FrameStack.Application.Images.Commands.RegisterImage;
using FrameStack.Application.Images.Queries.GetImageById;
using FrameStack.Application.Sessions;
using FrameStack.Application.Sessions.Commands.CreateSession;
using FrameStack.Application.Sessions.Commands.StartSession;
using FrameStack.Application.Sessions.Commands.StopSession;
using FrameStack.Application.Sessions.Queries.GetSessionById;
using FrameStack.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api/v1");

api.MapPost("/images", async (
    RegisterImageRequest request,
    ICommandDispatcher commandDispatcher,
    CancellationToken cancellationToken) =>
{
    var command = new RegisterImageCommand(
        request.Vendor,
        request.Platform,
        request.Name,
        request.Version,
        request.ArtifactPath,
        request.Sha256,
        request.DeclaredSizeBytes);

    var result = await commandDispatcher.Send<RegisterImageCommand, ImageDto>(
        command,
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Created($"/api/v1/images/{result.Value.Id}", ImageResponse.FromDto(result.Value));
});

api.MapGet("/images/{id:guid}", async (
    Guid id,
    IQueryDispatcher queryDispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await queryDispatcher.Query<GetImageByIdQuery, ImageDto>(
        new GetImageByIdQuery(id),
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Ok(ImageResponse.FromDto(result.Value));
});

api.MapPost("/sessions", async (
    CreateSessionRequest request,
    ICommandDispatcher commandDispatcher,
    CancellationToken cancellationToken) =>
{
    var command = new CreateSessionCommand(request.ImageId, request.CpuCores, request.MemoryMb);

    var result = await commandDispatcher.Send<CreateSessionCommand, SessionDto>(
        command,
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Created($"/api/v1/sessions/{result.Value.Id}", SessionResponse.FromDto(result.Value));
});

api.MapPost("/sessions/{id:guid}/start", async (
    Guid id,
    ICommandDispatcher commandDispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await commandDispatcher.Send<StartSessionCommand, SessionDto>(
        new StartSessionCommand(id),
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Ok(SessionResponse.FromDto(result.Value));
});

api.MapPost("/sessions/{id:guid}/stop", async (
    Guid id,
    ICommandDispatcher commandDispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await commandDispatcher.Send<StopSessionCommand, SessionDto>(
        new StopSessionCommand(id),
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Ok(SessionResponse.FromDto(result.Value));
});

api.MapGet("/sessions/{id:guid}", async (
    Guid id,
    IQueryDispatcher queryDispatcher,
    CancellationToken cancellationToken) =>
{
    var result = await queryDispatcher.Query<GetSessionByIdQuery, SessionDto>(
        new GetSessionByIdQuery(id),
        cancellationToken);

    if (result.IsFailure)
    {
        return result.ToProblem();
    }

    return Results.Ok(SessionResponse.FromDto(result.Value));
});

app.Run();

public partial class Program;
