using FrameStack.Application.Abstractions.Results;

namespace FrameStack.Application.Sessions;

public static class SessionErrors
{
    public static readonly AppError InvalidResources =
        new("sessions.resources.invalid", "CPU and memory must be greater than zero.");

    public static AppError ImageNotFound(Guid imageId) =>
        new("sessions.image_not_found", $"Image '{imageId}' was not found.");

    public static AppError SessionNotFound(Guid sessionId) =>
        new("sessions.not_found", $"Session '{sessionId}' was not found.");

    public static AppError ImageArtifactNotFound(string artifactPath) =>
        new("sessions.artifact_not_found", $"Image artifact not found on disk: '{artifactPath}'.");

    public static AppError StartFailed(Guid sessionId, string reason) =>
        new("sessions.start_failed", $"Failed to start session '{sessionId}': {reason}");

    public static AppError StopFailed(Guid sessionId, string reason) =>
        new("sessions.stop_failed", $"Failed to stop session '{sessionId}': {reason}");
}
